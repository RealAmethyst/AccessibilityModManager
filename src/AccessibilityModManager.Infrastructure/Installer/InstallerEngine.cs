using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Installer;

/// <summary>
/// Transactional installer engine. Every install is:
/// 1) Extract → 2) Parse manifest → 3) Pre-check → 4) Backup → 5) Apply → 6) Verify → 7) Write receipt
/// On any failure: automatic rollback.
/// </summary>
public sealed class InstallerEngine : IInstallerEngine
{
    private readonly BackupManager _backupManager;
    private readonly InstallActionExecutor _actionExecutor;
    private readonly InstallVerifier _verifier;
    private readonly ManifestParser _manifestParser;
    private readonly SafeZipExtractor _zipExtractor;
    private readonly IReceiptStore _receiptStore;
    private readonly ILogger _logger;

    public InstallerEngine(
        BackupManager backupManager,
        InstallActionExecutor actionExecutor,
        InstallVerifier verifier,
        ManifestParser manifestParser,
        SafeZipExtractor zipExtractor,
        IReceiptStore receiptStore,
        ILogger logger)
    {
        _backupManager = backupManager;
        _actionExecutor = actionExecutor;
        _verifier = verifier;
        _manifestParser = manifestParser;
        _zipExtractor = zipExtractor;
        _receiptStore = receiptStore;
        _logger = logger;
    }

    public async Task<InstallReceipt> InstallAsync(GameInstall game, ModRelease release, string packageZipPath, CancellationToken ct = default)
    {
        _logger.Information("Starting install: {PluginId}/{GameId} v{Version}", release.PluginId, release.GameId, release.Version);

        // Check for conflicting installs from other plugins
        var existingReceipts = await _receiptStore.LoadAllForGameAsync(game.Game.GameId);
        var ownReceipt = existingReceipts.FirstOrDefault(r => r.PluginId == release.PluginId);
        var otherReceipts = existingReceipts.Where(r => r.PluginId != release.PluginId).ToList();

        if (ownReceipt != null)
        {
            _logger.Warning("Plugin {PluginId} already has a mod installed for {GameId}. Use UpdateAsync instead.",
                release.PluginId, release.GameId);
            throw new InvalidOperationException(
                $"A mod from plugin '{release.PluginId}' is already installed for this game. Use update or uninstall first.");
        }

        return await ExecuteTransactionalInstall(game, release, packageZipPath, otherReceipts, ct);
    }

    public async Task<InstallReceipt> UpdateAsync(GameInstall game, ModRelease release, string packageZipPath, CancellationToken ct = default)
    {
        _logger.Information("Starting update: {PluginId}/{GameId} to v{Version}", release.PluginId, release.GameId, release.Version);

        // Uninstall the old version first
        await UninstallAsync(game, release.PluginId, ct);

        // Then install the new version
        var otherReceipts = (await _receiptStore.LoadAllForGameAsync(game.Game.GameId))
            .Where(r => r.PluginId != release.PluginId).ToList();

        return await ExecuteTransactionalInstall(game, release, packageZipPath, otherReceipts, ct);
    }

    public async Task UninstallAsync(GameInstall game, string pluginId, CancellationToken ct = default)
    {
        _logger.Information("Starting uninstall: {PluginId}/{GameId}", pluginId, game.Game.GameId);

        var receipt = await _receiptStore.LoadAsync(game.Game.GameId, pluginId);
        if (receipt == null)
        {
            _logger.Warning("No receipt found for {PluginId}/{GameId}, nothing to uninstall", pluginId, game.Game.GameId);
            return;
        }

        await RollbackAsync(game, receipt, ct);
        await _receiptStore.DeleteAsync(game.Game.GameId, pluginId);

        _logger.Information("Uninstall complete: {PluginId}/{GameId}", pluginId, game.Game.GameId);
    }

    public async Task RollbackAsync(GameInstall game, InstallReceipt receipt, CancellationToken ct = default)
    {
        _logger.Information("Rolling back: {PluginId}/{GameId} v{Version}",
            receipt.PluginId, receipt.GameId, receipt.InstalledVersion);

        // Process changes in reverse order
        foreach (var change in Enumerable.Reverse(receipt.Changes))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                switch (change.Type)
                {
                    case ChangeType.Added:
                        _backupManager.RemoveAddedFile(game.InstallPath, change.RelativePath);
                        break;

                    case ChangeType.Replaced:
                    case ChangeType.Patched:
                        if (!string.IsNullOrEmpty(change.BackupRelativePath))
                        {
                            _backupManager.RestoreFile(game.InstallPath, change.RelativePath,
                                receipt.BackupFolder, change.BackupRelativePath);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to rollback change: {Type} {Path}", change.Type, change.RelativePath);
                // Continue rolling back other files even if one fails
            }
        }

        _logger.Information("Rollback complete for {PluginId}/{GameId}", receipt.PluginId, receipt.GameId);

        await Task.CompletedTask;
    }

    private async Task<InstallReceipt> ExecuteTransactionalInstall(
        GameInstall game,
        ModRelease release,
        string packageZipPath,
        List<InstallReceipt> otherReceipts,
        CancellationToken ct)
    {
        // 1) Extract ZIP to temp folder
        var tempDir = Path.Combine(Path.GetTempPath(), "AccessibilityModManager", $"install_{Guid.NewGuid():N}");
        InstallReceipt? receipt = null;

        try
        {
            await _zipExtractor.ExtractAsync(packageZipPath, tempDir, ct);

            // 2) Parse manifest
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("Package does not contain manifest.json");

            var manifest = _manifestParser.ParseFile(manifestPath);

            // 3) Pre-checks
            if (manifest.GameId != game.Game.GameId)
                throw new InvalidOperationException(
                    $"Manifest gameId '{manifest.GameId}' does not match target game '{game.Game.GameId}'");

            if (manifest.PluginId != release.PluginId)
                throw new InvalidOperationException(
                    $"Manifest pluginId '{manifest.PluginId}' does not match release pluginId '{release.PluginId}'");

            // Check for file collisions with other plugins
            CheckForCollisions(manifest, game.InstallPath, otherReceipts);

            // 4) Create backup folder
            var backupFolder = _backupManager.CreateBackupFolder(game.InstallPath, release.PluginId, game.Game.GameId);

            // 5) Execute install actions
            var allChanges = new List<FileChange>();
            foreach (var action in manifest.InstallActions)
            {
                ct.ThrowIfCancellationRequested();

                var packageFilesDir = Path.Combine(tempDir, "files");
                var changes = _actionExecutor.Execute(action, packageFilesDir, game.InstallPath, backupFolder);
                allChanges.AddRange(changes);
            }

            // Compute manifest hash for the receipt
            var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
            var manifestHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson)));

            // Build receipt
            receipt = new InstallReceipt
            {
                GameId = game.Game.GameId,
                PluginId = release.PluginId,
                InstalledVersion = release.Version,
                InstalledAt = DateTime.UtcNow,
                Changes = allChanges,
                BackupFolder = backupFolder,
                ManifestHash = manifestHash
            };

            // 6) Verify
            if (!_verifier.Verify(manifest.Verify, game.InstallPath))
            {
                _logger.Error("Post-install verification failed, rolling back");
                await RollbackAsync(game, receipt, ct);
                throw new InvalidOperationException("Install verification failed. Changes have been rolled back.");
            }

            // 7) Write receipt
            await _receiptStore.SaveAsync(receipt);

            _logger.Information("Install complete: {PluginId}/{GameId} v{Version} — {ChangeCount} files changed",
                release.PluginId, release.GameId, release.Version, allChanges.Count);

            return receipt;
        }
        catch (Exception) when (receipt != null)
        {
            // If we have a partial receipt, try to rollback
            _logger.Error("Install failed after partial changes, attempting rollback");
            try
            {
                await RollbackAsync(game, receipt, ct);
            }
            catch (Exception rollbackEx)
            {
                _logger.Error(rollbackEx, "Rollback also failed — manual cleanup may be needed");
            }
            throw;
        }
        finally
        {
            // Clean up temp directory
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                    _logger.Debug("Cleaned up temp dir: {TempDir}", tempDir);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to clean up temp dir: {TempDir}", tempDir);
                }
            }
        }
    }

    /// <summary>
    /// Checks if any files this manifest would touch are already modified by another plugin.
    /// </summary>
    private void CheckForCollisions(Manifest manifest, string gameInstallPath, List<InstallReceipt> otherReceipts)
    {
        var otherFiles = otherReceipts
            .SelectMany(r => r.Changes.Select(c => (Receipt: r, Change: c)))
            .ToDictionary(x => x.Change.RelativePath.ToLowerInvariant(), x => x.Receipt.PluginId);

        foreach (var action in manifest.InstallActions)
        {
            var targetPaths = GetTargetPaths(action);
            foreach (var target in targetPaths)
            {
                var normalized = target.ToLowerInvariant();
                if (otherFiles.TryGetValue(normalized, out var conflictingPlugin))
                {
                    throw new InvalidOperationException(
                        $"File conflict: '{target}' is already modified by plugin '{conflictingPlugin}'. " +
                        $"Uninstall that plugin's mod first, or contact the plugin authors to resolve the conflict.");
                }
            }
        }
    }

    private static List<string> GetTargetPaths(InstallAction action)
    {
        return action switch
        {
            CopyFileAction cf => [cf.Target],
            ReplaceFileAction rf => [rf.Target],
            CopyFolderAction => [], // Can't know individual files without scanning the package
            _ => []
        };
    }
}
