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
/// 1) Extract → 2) Parse manifest → 3) Pre-check → 4) Confirm scripts (if any) → 5) Backup
///    → 6) Pre-install script → 7) Apply file actions → 8) Post-install script
///    → 9) Verify → 10) Cache post-uninstall script → 11) Write receipt.
/// On any fatal failure: automatic rollback.
/// </summary>
public sealed class InstallerEngine : IInstallerEngine
{
    private readonly BackupManager _backupManager;
    private readonly InstallActionExecutor _actionExecutor;
    private readonly InstallVerifier _verifier;
    private readonly ManifestParser _manifestParser;
    private readonly SafeZipExtractor _zipExtractor;
    private readonly IReceiptStore _receiptStore;
    private readonly IDependencyChecker _dependencyChecker;
    private readonly LifecycleScriptRunner _scriptRunner;
    private readonly DependencyAutoInstaller _depAutoInstaller;
    private readonly ILogger _logger;

    public InstallerEngine(
        BackupManager backupManager,
        InstallActionExecutor actionExecutor,
        InstallVerifier verifier,
        ManifestParser manifestParser,
        SafeZipExtractor zipExtractor,
        IReceiptStore receiptStore,
        IDependencyChecker dependencyChecker,
        LifecycleScriptRunner scriptRunner,
        DependencyAutoInstaller depAutoInstaller,
        ILogger logger)
    {
        _backupManager = backupManager;
        _actionExecutor = actionExecutor;
        _verifier = verifier;
        _manifestParser = manifestParser;
        _zipExtractor = zipExtractor;
        _receiptStore = receiptStore;
        _dependencyChecker = dependencyChecker;
        _scriptRunner = scriptRunner;
        _depAutoInstaller = depAutoInstaller;
        _logger = logger;
    }

    public async Task<InstallReceipt> InstallAsync(
        GameInstall game, ModRelease release, string packageZipPath,
        IScriptHost? scriptHost = null, IDependencyHost? dependencyHost = null,
        CancellationToken ct = default)
    {
        _logger.Information("Starting install: {PluginId}/{GameId} v{Version}", release.PluginId, release.GameId, release.Version);

        // Q8 redesign: install missing required deps as a step in this flow rather than
        // throwing. Auto-installable deps run themselves; manual-only deps prompt the user.
        await ResolveDependenciesAsync(game, release.PluginId, dependencyHost, ct);

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

        // Q9=A: warn the user about lifecycle scripts on first install (no existing receipt).
        return await ExecuteTransactionalInstall(
            game, release, packageZipPath, otherReceipts,
            scriptHost, scriptsAlreadyConfirmed: false, previousScriptsFingerprint: null, ct);
    }

    public async Task<InstallReceipt> UpdateAsync(
        GameInstall game, ModRelease release, string packageZipPath,
        IScriptHost? scriptHost = null, IDependencyHost? dependencyHost = null,
        CancellationToken ct = default)
    {
        _logger.Information("Starting update: {PluginId}/{GameId} to v{Version}", release.PluginId, release.GameId, release.Version);

        await ResolveDependenciesAsync(game, release.PluginId, dependencyHost, ct);

        var oldReceipt = await _receiptStore.LoadAsync(game.Game.GameId, release.PluginId);
        if (oldReceipt == null)
        {
            // Nothing installed for this plugin yet — treat as a first install (warns about scripts).
            var freshOthers = (await _receiptStore.LoadAllForGameAsync(game.Game.GameId))
                .Where(r => r.PluginId != release.PluginId).ToList();
            return await ExecuteTransactionalInstall(
                game, release, packageZipPath, freshOthers,
                scriptHost, scriptsAlreadyConfirmed: false, previousScriptsFingerprint: null, ct);
        }

        // Atomic update: snapshot the old version's installed files first, so if the new install
        // fails we can put the old version back instead of leaving the user with nothing. Uninstall
        // the old version WITHOUT releasing its shared dependencies (the new version needs the same
        // ones), install the new version, and restore the old on any failure.
        var snapshotDir = Path.Combine(Path.GetTempPath(), "AccessibilityModManager", $"updatebak_{Guid.NewGuid():N}");
        try
        {
            SnapshotInstalledFiles(oldReceipt, game.InstallPath, snapshotDir);

            await UninstallCoreAsync(game, release.PluginId, scriptHost,
                releaseDependencies: false, runPostUninstall: false, ct);

            var otherReceipts = (await _receiptStore.LoadAllForGameAsync(game.Game.GameId))
                .Where(r => r.PluginId != release.PluginId).ToList();

            // Re-warn about scripts only if they changed since the user last agreed (see the consent
            // logic in ExecuteTransactionalInstall) — a new or modified script needs a fresh notice.
            return await ExecuteTransactionalInstall(
                game, release, packageZipPath, otherReceipts,
                scriptHost, scriptsAlreadyConfirmed: true,
                previousScriptsFingerprint: oldReceipt.ScriptsFingerprint, ct);
        }
        catch (Exception)
        {
            try
            {
                RestoreSnapshot(snapshotDir, game.InstallPath);
                await _receiptStore.SaveAsync(oldReceipt);
                _logger.Warning("Update of {PluginId}/{GameId} failed; restored the previous version",
                    release.PluginId, game.Game.GameId);
            }
            catch (Exception restoreEx)
            {
                _logger.Error(restoreEx,
                    "Update failed AND restoring the previous version failed — manual repair may be needed");
            }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(snapshotDir)) Directory.Delete(snapshotDir, recursive: true); } catch { }
        }
    }

    public Task UninstallAsync(GameInstall game, string pluginId, IScriptHost? scriptHost = null, CancellationToken ct = default)
        => UninstallCoreAsync(game, pluginId, scriptHost, releaseDependencies: true, runPostUninstall: true, ct);

    /// <param name="releaseDependencies">
    /// When true (a real uninstall), drop this plugin from each shared dependency's refcount and
    /// remove any dependency no mod needs any more. The update path passes false: it uninstalls the
    /// old version as an internal step but the new version needs the same dependencies.
    /// </param>
    /// <param name="runPostUninstall">
    /// When true (a real uninstall), run the old version's cached post-uninstall script. The update
    /// path passes false: the mod isn't being removed, just replaced, so the removal-cleanup script
    /// shouldn't run — and its side effects wouldn't be undone if the new version then failed to
    /// install and we restored the old one.
    /// </param>
    private async Task UninstallCoreAsync(GameInstall game, string pluginId, IScriptHost? scriptHost,
        bool releaseDependencies, bool runPostUninstall, CancellationToken ct)
    {
        _logger.Information("Starting uninstall: {PluginId}/{GameId}", pluginId, game.Game.GameId);

        var receipt = await _receiptStore.LoadAsync(game.Game.GameId, pluginId);
        if (receipt == null)
        {
            _logger.Warning("No receipt found for {PluginId}/{GameId}, nothing to uninstall", pluginId, game.Game.GameId);
            return;
        }

        // Run post-uninstall script first (best-effort: failures don't block the uninstall).
        if (runPostUninstall &&
            receipt.PostUninstall is not null && !string.IsNullOrEmpty(receipt.CachedPostUninstallExecutable))
        {
            await TryRunPostUninstall(game, receipt, scriptHost, ct);
        }

        await RollbackAsync(game, receipt, ct);
        await _receiptStore.DeleteAsync(game.Game.GameId, pluginId);

        // F4 addendum: delete the cached executable after the script ran (or after we
        // attempted to). The receipt itself is gone; the script binary should follow.
        TryDeleteCachedScripts(game.Game.GameId, pluginId);

        if (releaseDependencies)
        {
            // Release this mod's hold on any shared auto-installed dependencies. When a dependency's
            // refcount hits zero (no mod needs it any more) it is removed too.
            try
            {
                await _depAutoInstaller.ReleaseDependenciesForPluginAsync(game, pluginId, ct);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to release dependencies for {PluginId}/{GameId} — the mod is still uninstalled",
                    pluginId, game.Game.GameId);
            }
        }

        _logger.Information("Uninstall complete: {PluginId}/{GameId}", pluginId, game.Game.GameId);
    }

    public async Task RollbackAsync(GameInstall game, InstallReceipt receipt, CancellationToken ct = default)
    {
        _logger.Information("Rolling back: {PluginId}/{GameId} v{Version}",
            receipt.PluginId, receipt.GameId, receipt.InstalledVersion);

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
        IScriptHost? scriptHost,
        bool scriptsAlreadyConfirmed,
        string? previousScriptsFingerprint,
        CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AccessibilityModManager", $"install_{Guid.NewGuid():N}");
        InstallReceipt? receipt = null;
        // Tracked outside the try so a failure BEFORE the receipt is finalized still rolls back the
        // files already applied (previously a mid-install throw left partial changes with no receipt).
        string? backupFolder = null;
        var allChanges = new List<FileChange>();

        try
        {
            await _zipExtractor.ExtractAsync(packageZipPath, tempDir, ct);

            var manifestPath = Path.Combine(tempDir, "manifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("Package does not contain manifest.json");

            var manifest = _manifestParser.ParseFile(manifestPath);

            if (manifest.GameId != game.Game.GameId)
                throw new InvalidOperationException(
                    $"Manifest gameId '{manifest.GameId}' does not match target game '{game.Game.GameId}'");

            if (manifest.PluginId != release.PluginId)
                throw new InvalidOperationException(
                    $"Manifest pluginId '{manifest.PluginId}' does not match release pluginId '{release.PluginId}'");

            var packageFilesDir = Path.Combine(tempDir, "files");
            CheckForCollisions(manifest, packageFilesDir, otherReceipts);

            // Validate every declared lifecycle script before doing any other work — safer to
            // bail out before backup if the manifest is malformed.
            ValidateLifecycleScripts(manifest, tempDir);

            // Warn about lifecycle scripts on a first install, and on an update only when the scripts
            // changed since the user last agreed — a newly-added or modified script needs a fresh
            // notice (Q9=A first-install consent still applies; the update re-notice is the audit
            // follow-up). An unchanged script the user already approved does not re-prompt.
            var scriptsFingerprint = ComputeScriptsFingerprint(manifest, tempDir);
            var needScriptConsent = HasAnyLifecycleScript(manifest) &&
                (!scriptsAlreadyConfirmed || scriptsFingerprint != previousScriptsFingerprint);
            if (needScriptConsent)
            {
                if (scriptHost == null)
                    throw new InvalidOperationException(
                        $"Manifest declares lifecycle scripts but no script host was supplied. " +
                        "The caller (manager UI or test harness) must provide an IScriptHost when scripts are present.");

                var prompt = BuildPrompt(release, manifest);
                var ok = await scriptHost.ConfirmInstallScriptsAsync(prompt, ct);
                if (!ok)
                    throw new OperationCanceledException("User declined the lifecycle script warning.");
            }

            // F5=B: backup → pre-install → file copy → post-install.
            backupFolder = _backupManager.CreateBackupFolder(game.InstallPath, release.PluginId, game.Game.GameId);

            // scriptsAlreadyConfirmed=true means we're on the update path — that flag is also
            // our signal that pre/post-install hooks should be skipped unless the author opted
            // them in via RunOnUpdate. Default-off matches "registry-write style" scripts that
            // only need to apply once; opt-in matches "version.dll patcher" style scripts that
            // need to re-apply when the mod files change.
            var isUpdate = scriptsAlreadyConfirmed;

            if (manifest.PreInstall is not null && (!isUpdate || manifest.PreInstall.RunOnUpdate))
            {
                await RunHookOrThrow("Pre-install", manifest.PreInstall, tempDir, game, scriptHost, ct);
            }

            // Run install actions
            foreach (var action in manifest.InstallActions)
            {
                ct.ThrowIfCancellationRequested();

                var changes = _actionExecutor.Execute(action, packageFilesDir, game.InstallPath, backupFolder);
                allChanges.AddRange(changes);
            }

            if (manifest.PostInstall is not null && (!isUpdate || manifest.PostInstall.RunOnUpdate))
            {
                await RunHookOrThrow("Post-install", manifest.PostInstall, tempDir, game, scriptHost, ct);
            }

            // Build receipt (with cached post-uninstall script if any)
            var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
            var manifestHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson)));

            string? cachedPostUninstallPath = null;
            string? cachedPostUninstallSha = null;
            if (manifest.PostUninstall is not null)
            {
                (cachedPostUninstallPath, cachedPostUninstallSha) = CachePostUninstallScript(
                    manifest.PostUninstall, tempDir, release.PluginId, game.Game.GameId);
            }

            receipt = new InstallReceipt
            {
                GameId = game.Game.GameId,
                PluginId = release.PluginId,
                InstalledVersion = release.Version,
                InstalledAt = DateTime.UtcNow,
                Changes = allChanges,
                BackupFolder = backupFolder,
                ManifestHash = manifestHash,
                CachedPostUninstallExecutable = cachedPostUninstallPath,
                CachedPostUninstallSha256 = cachedPostUninstallSha,
                PostUninstall = manifest.PostUninstall,
                ScriptsFingerprint = scriptsFingerprint
            };

            if (!_verifier.Verify(manifest.Verify, game.InstallPath))
            {
                // Single rollback path: throw and let the catch below undo the changes (it handles
                // this and any earlier mid-install failure uniformly).
                _logger.Error("Post-install verification failed, rolling back");
                throw new InvalidOperationException("Install verification failed. Changes have been rolled back.");
            }

            await _receiptStore.SaveAsync(receipt);

            _logger.Information("Install complete: {PluginId}/{GameId} v{Version} — {ChangeCount} files changed",
                release.PluginId, release.GameId, release.Version, allChanges.Count);

            return receipt;
        }
        catch (Exception)
        {
            // Roll back whatever was applied, whether or not the receipt was finalized. If we never
            // reached backup-folder creation there's nothing on disk to undo. Use a fresh
            // (non-cancelled) token so a cancel mid-install still cleans up fully.
            var toRollBack = receipt ?? (backupFolder != null
                ? new InstallReceipt
                {
                    GameId = game.Game.GameId,
                    PluginId = release.PluginId,
                    InstalledVersion = release.Version,
                    InstalledAt = DateTime.UtcNow,
                    Changes = allChanges,
                    BackupFolder = backupFolder,
                    ManifestHash = string.Empty // unused for rollback; only Changes + BackupFolder matter
                }
                : null);

            if (toRollBack != null)
            {
                _logger.Error("Install failed after partial changes, attempting rollback");
                try
                {
                    await RollbackAsync(game, toRollBack, CancellationToken.None);
                    TryDeleteCachedScripts(game.Game.GameId, release.PluginId);
                }
                catch (Exception rollbackEx)
                {
                    _logger.Error(rollbackEx, "Rollback also failed — manual cleanup may be needed");
                }
            }
            throw;
        }
        finally
        {
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

    private async Task RunHookOrThrow(
        string hookLabel,
        LifecycleScript script,
        string stagingDir,
        GameInstall game,
        IScriptHost? scriptHost,
        CancellationToken ct)
    {
        scriptHost?.OnScriptStarting(hookLabel, Path.GetFileName(script.Executable));
        var stagingScriptPath = Path.Combine(stagingDir, script.Executable);

        // RunFromGameFolder=true is for scripts that locate their own files via
        // Assembly.Location etc — neither --gameFolder nor WorkingDirectory help, so we copy
        // the script into the game folder, run it from there, and clean up after.
        // PrepareGameFolderRun returns the path the runner should actually invoke + a state
        // bag we use in the finally block to delete/restore.
        GameFolderRunState? gameFolderRun = null;
        var scriptAbsolute = stagingScriptPath;
        if (script.RunFromGameFolder)
        {
            gameFolderRun = PrepareGameFolderRun(stagingScriptPath, script, game.InstallPath, hookLabel);
            scriptAbsolute = gameFolderRun.GameFolderScriptPath;
        }

        LifecycleScriptResult result;
        try
        {
            result = await _scriptRunner.RunAsync(
                script,
                scriptAbsolute,
                gameFolder: game.InstallPath,
                modFolder: stagingDir,
                onOutputLine: scriptHost == null ? null : line => scriptHost.OnScriptOutputLine(line),
                ct);
        }
        catch (Exception ex)
        {
            scriptHost?.OnScriptFinished(-1, succeeded: false);
            _logger.Error(ex, "{Hook} script execution threw", hookLabel);
            CleanupGameFolderRun(gameFolderRun, script);
            // If the script COULDN'T run and the hook is fatal, the install must abort.
            if (script.FailureFatal)
                throw new InvalidOperationException(
                    $"{hookLabel} script failed to run: {ex.Message}", ex);
            return;
        }

        CleanupGameFolderRun(gameFolderRun, script);

        scriptHost?.OnScriptFinished(result.ExitCode, result.Succeeded);

        if (!result.Succeeded)
        {
            _logger.Error("{Hook} script exited {Code}. Output:\n{Output}",
                hookLabel, result.ExitCode, result.CombinedOutput);

            if (script.FailureFatal)
                throw new InvalidOperationException(
                    $"{hookLabel} script failed (exit code {result.ExitCode}). " +
                    $"Install aborted. Last output:\n\n{TruncateForMessage(result.CombinedOutput)}");

            // Non-fatal: log + continue.
            _logger.Warning("{Hook} script reported failure but the manifest marks it non-fatal. " +
                            "Continuing install.", hookLabel);
        }
    }

    /// <summary>
    /// State carried from <see cref="PrepareGameFolderRun"/> through the run to
    /// <see cref="CleanupGameFolderRun"/>. Holds the in-game-folder path we copied the
    /// script to and, optionally, where we stashed any pre-existing file at that path so we
    /// can restore it afterwards.
    /// </summary>
    private sealed record GameFolderRunState(string GameFolderScriptPath, string? BackupOfPreexistingFile);

    /// <summary>
    /// Copies the staging-area script into the game folder so it runs with the game folder
    /// as its own location. If a file with that name already exists, it's stashed to a temp
    /// path so <see cref="CleanupGameFolderRun"/> can restore it.
    /// </summary>
    private GameFolderRunState PrepareGameFolderRun(
        string stagingScriptPath, LifecycleScript script, string gameFolder, string hookLabel)
    {
        var basename = Path.GetFileName(script.Executable);
        if (string.IsNullOrEmpty(basename))
            throw new InvalidOperationException(
                $"{hookLabel}: script executable path '{script.Executable}' has no filename — can't copy to game folder.");

        var targetPath = Path.Combine(gameFolder, basename);
        string? backupPath = null;
        if (File.Exists(targetPath))
        {
            backupPath = Path.Combine(Path.GetTempPath(),
                $"amm-script-backup-{Guid.NewGuid():N}-{basename}");
            File.Copy(targetPath, backupPath, overwrite: true);
            _logger.Debug("{Hook}: stashed existing {Target} to {Backup}", hookLabel, targetPath, backupPath);
        }

        File.Copy(stagingScriptPath, targetPath, overwrite: true);
        _logger.Information("{Hook}: copied script to game folder for run-from-game-folder mode: {Target}",
            hookLabel, targetPath);
        return new GameFolderRunState(targetPath, backupPath);
    }

    /// <summary>
    /// Removes the in-game-folder script copy after the run, then restores the file we
    /// stashed in <see cref="PrepareGameFolderRun"/>. No-op when the script's
    /// <see cref="LifecycleScript.InstallToGameFolder"/> is true — that flag means the user
    /// wants the script to stick around, so we leave the copy alone (and discard the backup).
    /// </summary>
    private void CleanupGameFolderRun(GameFolderRunState? state, LifecycleScript script)
    {
        if (state is null) return;

        if (script.InstallToGameFolder)
        {
            // Author wants the script kept; the install actions also include a copyFile for
            // it (added by ManifestBuilderService) so the receipt knows about it. Discard
            // any backup — the file we left behind is "the new state" the receipt records.
            if (state.BackupOfPreexistingFile is not null)
            {
                try { File.Delete(state.BackupOfPreexistingFile); } catch { }
            }
            return;
        }

        try { File.Delete(state.GameFolderScriptPath); }
        catch (Exception ex) { _logger.Warning(ex, "Failed to remove in-game-folder script copy {Path}", state.GameFolderScriptPath); }

        if (state.BackupOfPreexistingFile is not null)
        {
            try
            {
                File.Copy(state.BackupOfPreexistingFile, state.GameFolderScriptPath, overwrite: true);
                File.Delete(state.BackupOfPreexistingFile);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to restore stashed file from {Backup} to {Target}",
                    state.BackupOfPreexistingFile, state.GameFolderScriptPath);
            }
        }
    }

    private async Task TryRunPostUninstall(
        GameInstall game, InstallReceipt receipt, IScriptHost? scriptHost, CancellationToken ct)
    {
        if (receipt.PostUninstall is null || string.IsNullOrEmpty(receipt.CachedPostUninstallExecutable))
            return;
        if (!File.Exists(receipt.CachedPostUninstallExecutable))
        {
            _logger.Warning("Cached post-uninstall script missing at {Path}; skipping",
                receipt.CachedPostUninstallExecutable);
            return;
        }

        // Integrity: the cached script must still hash to what we recorded at install. If it was
        // swapped on disk, refuse to run it — the consent dialog describes the ORIGINAL script, not a
        // replacement. (Older receipts predate the recorded hash and skip this check.)
        if (receipt.CachedPostUninstallSha256 is { Length: > 0 } expectedSha)
        {
            var actualSha = Convert.ToHexStringLower(
                SHA256.HashData(await File.ReadAllBytesAsync(receipt.CachedPostUninstallExecutable, ct)));
            if (!string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
            {
                _logger.Error(
                    "Cached post-uninstall script for {PluginId}/{GameId} does not match its recorded hash — refusing to run it. Uninstall continues.",
                    receipt.PluginId, receipt.GameId);
                return;
            }
        }

        // Consent is mandatory for running author code. With no script host to ask (e.g. a
        // non-UI caller), skip the script rather than run it silently — fail closed. The uninstall
        // itself still proceeds.
        if (scriptHost == null)
        {
            _logger.Warning(
                "Post-uninstall script present for {PluginId}/{GameId} but no script host to confirm consent — skipping it.",
                receipt.PluginId, receipt.GameId);
            return;
        }

        // Confirm with user (Q9=A semantics: re-confirm because uninstall is a separate action).
        var prompt = new LifecycleScriptPrompt
        {
            ModName = receipt.PluginId,
            Version = receipt.InstalledVersion,
            Author = receipt.PluginId,
            Hooks = new[]
            {
                new LifecycleScriptHookInfo
                {
                    HookLabel = "Post-uninstall",
                    Script = receipt.PostUninstall
                }
            }
        };
        var ok = await scriptHost.ConfirmUninstallScriptAsync(prompt, ct);
        if (!ok)
        {
            _logger.Information("User declined the post-uninstall script; skipping it. Uninstall continues.");
            return;
        }

        scriptHost.OnScriptStarting("Post-uninstall", Path.GetFileName(receipt.CachedPostUninstallExecutable));

        // Same RunFromGameFolder support as the install hooks — but here the source is the
        // cached executable under the receipt dir, not the staging dir. Cleanup always
        // removes the in-game-folder copy: at uninstall the rollback is about to run anyway,
        // and InstallToGameFolder doesn't apply post-uninstall (the mod is going away).
        GameFolderRunState? gameFolderRun = null;
        var scriptAbsolute = receipt.CachedPostUninstallExecutable;
        if (receipt.PostUninstall.RunFromGameFolder)
        {
            gameFolderRun = PrepareGameFolderRun(
                receipt.CachedPostUninstallExecutable, receipt.PostUninstall, game.InstallPath, "Post-uninstall");
            scriptAbsolute = gameFolderRun.GameFolderScriptPath;
        }

        try
        {
            var modFolder = _receiptStore.GetReceiptDirectory(receipt.GameId, receipt.PluginId);
            var result = await _scriptRunner.RunAsync(
                receipt.PostUninstall,
                scriptAbsolute,
                gameFolder: game.InstallPath,
                modFolder: modFolder,
                onOutputLine: scriptHost == null ? null : line => scriptHost.OnScriptOutputLine(line),
                ct);
            scriptHost?.OnScriptFinished(result.ExitCode, result.Succeeded);
            if (!result.Succeeded)
                _logger.Warning("Post-uninstall script exited {Code}; continuing the uninstall anyway.",
                    result.ExitCode);
        }
        catch (Exception ex)
        {
            scriptHost?.OnScriptFinished(-1, succeeded: false);
            _logger.Error(ex, "Post-uninstall script crashed; continuing the uninstall anyway.");
        }
        finally
        {
            // Force-cleanup ignoring InstallToGameFolder — at uninstall time we never want
            // to leave the script behind, the mod is being removed.
            if (gameFolderRun is not null)
                CleanupGameFolderRun(gameFolderRun, new LifecycleScript
                {
                    Executable = receipt.PostUninstall.Executable,
                    What = receipt.PostUninstall.What,
                    Why = receipt.PostUninstall.Why,
                    Modifies = receipt.PostUninstall.Modifies,
                    InstallToGameFolder = false,
                    RunOnUpdate = receipt.PostUninstall.RunOnUpdate,
                    RunFromGameFolder = receipt.PostUninstall.RunFromGameFolder,
                    NeedsAdmin = receipt.PostUninstall.NeedsAdmin,
                    FailureFatal = receipt.PostUninstall.FailureFatal
                });
        }
    }

    private (string Path, string Sha256) CachePostUninstallScript(
        LifecycleScript script, string stagingDir, string pluginId, string gameId)
    {
        var cacheDir = Path.Combine(_receiptStore.GetReceiptDirectory(gameId, pluginId), "scripts");
        Directory.CreateDirectory(cacheDir);

        // Use the original filename so its extension survives — runner picks a runner by ext.
        var fileName = Path.GetFileName(script.Executable);
        var cachedPath = Path.Combine(cacheDir, fileName);

        var sourcePath = Path.Combine(stagingDir, script.Executable);
        File.Copy(sourcePath, cachedPath, overwrite: true);

        // Record the hash so uninstall can confirm the cached file wasn't swapped before running it.
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(cachedPath)));

        _logger.Information("Cached post-uninstall script to {Path}", cachedPath);
        return (cachedPath, sha);
    }

    private void TryDeleteCachedScripts(string gameId, string pluginId)
    {
        try
        {
            var cacheDir = Path.Combine(_receiptStore.GetReceiptDirectory(gameId, pluginId), "scripts");
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, recursive: true);
                _logger.Debug("Deleted cached scripts at {Path}", cacheDir);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete cached scripts for {PluginId}/{GameId}", pluginId, gameId);
        }
    }

    private static void ValidateLifecycleScripts(Manifest manifest, string stagingDir)
    {
        if (manifest.PreInstall is not null)
            LifecycleScriptRunner.ValidateScriptInStaging(manifest.PreInstall, stagingDir);
        if (manifest.PostInstall is not null)
            LifecycleScriptRunner.ValidateScriptInStaging(manifest.PostInstall, stagingDir);
        if (manifest.PostUninstall is not null)
            LifecycleScriptRunner.ValidateScriptInStaging(manifest.PostUninstall, stagingDir);
    }

    private static bool HasAnyLifecycleScript(Manifest manifest) =>
        manifest.PreInstall is not null ||
        manifest.PostInstall is not null ||
        manifest.PostUninstall is not null;

    /// <summary>
    /// A stable hash over the manifest's lifecycle scripts (each script's bytes + its behavior flags),
    /// or null if none are declared. Recorded in the receipt so an update can tell whether the user is
    /// looking at the same scripts they already agreed to, or a new/changed one that needs a re-notice.
    /// </summary>
    private static string? ComputeScriptsFingerprint(Manifest manifest, string stagingDir)
    {
        if (!HasAnyLifecycleScript(manifest)) return null;

        var sb = new StringBuilder();

        void Add(string label, LifecycleScript? s)
        {
            if (s is null) return;
            var path = Path.Combine(stagingDir, s.Executable);
            var fileSha = File.Exists(path)
                ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
                : "missing";
            sb.Append(label).Append('|').Append(s.Executable).Append('|').Append(fileSha)
              .Append("|admin=").Append(s.NeedsAdmin)
              .Append("|onUpdate=").Append(s.RunOnUpdate)
              .Append("|fatal=").Append(s.FailureFatal)
              .Append("|fromGame=").Append(s.RunFromGameFolder)
              .Append("|toGame=").Append(s.InstallToGameFolder)
              .Append('\n');
        }

        Add("pre", manifest.PreInstall);
        Add("post", manifest.PostInstall);
        Add("postUninstall", manifest.PostUninstall);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// Copies the current on-disk version of every file a receipt installed into a snapshot folder,
    /// so an update can restore the previous version if the new install fails.
    /// </summary>
    private static void SnapshotInstalledFiles(InstallReceipt receipt, string gameDir, string snapshotDir)
    {
        foreach (var change in receipt.Changes)
        {
            var src = Path.Combine(gameDir, change.RelativePath);
            if (!File.Exists(src)) continue;
            var dest = Path.Combine(snapshotDir, change.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
        }
    }

    /// <summary>
    /// Restores files captured by <see cref="SnapshotInstalledFiles"/> back into the game folder.
    /// </summary>
    private static void RestoreSnapshot(string snapshotDir, string gameDir)
    {
        if (!Directory.Exists(snapshotDir)) return;
        foreach (var file in Directory.EnumerateFiles(snapshotDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(snapshotDir, file);
            var dest = Path.Combine(gameDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static LifecycleScriptPrompt BuildPrompt(ModRelease release, Manifest manifest)
    {
        var hooks = new List<LifecycleScriptHookInfo>();
        if (manifest.PreInstall is not null)
            hooks.Add(new LifecycleScriptHookInfo { HookLabel = "Pre-install", Script = manifest.PreInstall });
        if (manifest.PostInstall is not null)
            hooks.Add(new LifecycleScriptHookInfo { HookLabel = "Post-install", Script = manifest.PostInstall });
        if (manifest.PostUninstall is not null)
            hooks.Add(new LifecycleScriptHookInfo { HookLabel = "Post-uninstall", Script = manifest.PostUninstall });

        return new LifecycleScriptPrompt
        {
            ModName = release.PluginId,
            Version = release.Version,
            Author = release.PluginId,
            Hooks = hooks
        };
    }

    private static string TruncateForMessage(string output, int maxChars = 2000)
    {
        if (string.IsNullOrEmpty(output)) return "(no output)";
        return output.Length <= maxChars ? output : output[..maxChars] + "\n... (truncated)";
    }

    /// <summary>
    /// Q8 redesign: dep installation is now a step in the install flow. For each missing
    /// required dependency, in manifest order (F14=A):
    ///   - If the dep has an AutoInstall block, prompt the user once with the combined deps
    ///     consent dialog (F16=C step 1) then run all auto-installs in sequence.
    ///   - If the dep has only a download URL (no AutoInstall), open it in a browser and
    ///     pause for the user to install manually + click Continue (F8=C).
    /// After every dep, recheck via <see cref="IDependencyChecker"/>; if a recheck still says
    /// missing we log a warning and continue (F5=B) — the actual mod install will catch a
    /// truly broken setup.
    /// </summary>
    private async Task ResolveDependenciesAsync(
        GameInstall game, string requestingPluginId, IDependencyHost? host, CancellationToken ct)
    {
        if (game.Game.Dependencies.Count == 0)
        {
            _logger.Information("Dep resolution for {GameId}/{PluginId}: game definition declares no dependencies — skipping",
                game.Game.GameId, requestingPluginId);
            return;
        }

        // Game-installer deps (IsGameInstaller) are handled by the manager's pre-install step
        // before detection — by the time we get here the game is already installed. Drop them so
        // they never appear in the dependency consent dialog or get re-run here.
        var statuses = (await _dependencyChecker.CheckAsync(game, ct))
            .Where(s => !s.Dependency.IsGameInstaller)
            .ToList();
        var blockers = statuses
            .Where(s => s.Dependency.Required && s.Status != DependencyStatusKind.Installed)
            .ToList();
        if (blockers.Count == 0)
        {
            _logger.Information(
                "Dep resolution for {GameId}/{PluginId}: all {Count} declared dependencies report Installed — skipping auto-install. " +
                "If a dep is actually missing, the check rule (file/folder path or registry key) is matching something stale; tighten the check.",
                game.Game.GameId, requestingPluginId, statuses.Count);
            return;
        }

        _logger.Information("Dep resolution for {GameId}/{PluginId}: {BlockerCount} of {TotalCount} required deps need install: {Ids}",
            game.Game.GameId, requestingPluginId, blockers.Count, statuses.Count,
            string.Join(", ", blockers.Select(b => b.Dependency.Id)));

        // Preserve manifest order (F14=A): match each blocker back to its position in the
        // game's Dependencies list so author-controlled ordering survives.
        blockers = game.Game.Dependencies
            .Select(d => blockers.FirstOrDefault(b => b.Dependency.Id == d.Id))
            .Where(b => b is not null)
            .Cast<DependencyStatus>()
            .ToList();

        var autoBlockers = blockers
            .Where(b => b.Dependency.Fix?.AutoInstall is not null)
            .ToList();

        if (autoBlockers.Count > 0)
        {
            if (host == null)
                throw new MissingRequiredDependencyException(blockers,
                    "Auto-installable dependencies are present but no dependency host was supplied.");

            var prompt = BuildDependencyPrompt(game, autoBlockers);
            var ok = await host.ConfirmDependencyInstallAsync(prompt, ct);
            if (!ok)
                throw new OperationCanceledException("User declined the dependency install.");
        }

        foreach (var b in blockers)
        {
            ct.ThrowIfCancellationRequested();
            var dep = b.Dependency;

            if (dep.Fix?.AutoInstall is not null)
            {
                var result = await _depAutoInstaller.InstallAsync(dep, game, requestingPluginId, host, ct);
                if (!result.Succeeded)
                    throw new InvalidOperationException(
                        $"Dependency '{dep.Id}' auto-install failed: {result.ErrorMessage}");
            }
            else if (!string.IsNullOrWhiteSpace(dep.Fix?.DownloadUrl))
            {
                if (host == null)
                    throw new MissingRequiredDependencyException(new[] { b },
                        $"Dependency '{dep.Id}' requires manual install and no host was supplied.");

                // The manual-download URL comes from the (unsigned) plugin index and gets opened in
                // the user's browser — enforce https so a crafted http:/file:/custom-scheme URL
                // can't drive a shell action.
                UrlValidator.RequireHttps(dep.Fix!.DownloadUrl!, $"manual dependency '{dep.Id}' download URL");

                var continueOk = await host.AwaitManualDependencyAsync(
                    new DependencyManualPrompt
                    {
                        DependencyId = dep.Id,
                        DownloadUrl = dep.Fix!.DownloadUrl!
                    }, ct);
                if (!continueOk)
                    throw new OperationCanceledException(
                        $"User cancelled while installing '{dep.Id}' manually.");
            }
            else
            {
                throw new MissingRequiredDependencyException(new[] { b },
                    $"Dependency '{dep.Id}' is missing and has no Fix configured.");
            }

            // F5=B: recheck this single dep. If still missing, warn and continue — the actual
            // mod install will surface anything truly broken.
            var recheck = await _dependencyChecker.CheckAsync(game, ct);
            var still = recheck.FirstOrDefault(s => s.Dependency.Id == dep.Id);
            if (still is { Status: not DependencyStatusKind.Installed })
            {
                _logger.Warning(
                    "Dep {DepId} still reports {Status} after install — continuing per F5=B; mod install may fail.",
                    dep.Id, still.Status);
            }
        }
    }

    private static DependencyInstallPrompt BuildDependencyPrompt(
        GameInstall game, List<DependencyStatus> blockers)
    {
        var items = new List<DependencyInstallPromptItem>();
        foreach (var b in blockers)
        {
            var auto = b.Dependency.Fix?.AutoInstall;
            if (auto is null) continue;
            items.Add(new DependencyInstallPromptItem
            {
                Dependency = b.Dependency,
                KindLabel = auto switch
                {
                    ExtractZipAutoInstall => "Extract ZIP",
                    RunInstallerAutoInstall => "Run installer",
                    CopyFileAutoInstall => "Copy file",
                    _ => auto.GetType().Name
                },
                DownloadUrl = b.Dependency.Fix?.DownloadUrl ?? "",
                NeedsAdmin = auto is RunInstallerAutoInstall ri && ri.NeedsAdmin
            });
        }
        return new DependencyInstallPrompt
        {
            ModName = game.Game.DisplayName,
            Version = "",
            Items = items
        };
    }

    /// <summary>
    /// Checks if any files this manifest would touch are already modified by another plugin.
    /// </summary>
    // Normalize a game-relative path for collision comparison: unify slash direction and case so an
    // author-written "libs/x.dll" (copyFile) and an enumerated "libs\x.dll" (copyFolder) compare equal.
    private static string NormalizeRelPath(string p) =>
        p.Replace('/', '\\').ToLowerInvariant();

    private void CheckForCollisions(Manifest manifest, string packageFilesDir, List<InstallReceipt> otherReceipts)
    {
        var otherFiles = new Dictionary<string, string>();
        foreach (var r in otherReceipts)
            foreach (var c in r.Changes)
                otherFiles[NormalizeRelPath(c.RelativePath)] = r.PluginId;

        foreach (var action in manifest.InstallActions)
        {
            var targetPaths = GetTargetPaths(action, packageFilesDir);
            foreach (var target in targetPaths)
            {
                var normalized = NormalizeRelPath(target);
                if (otherFiles.TryGetValue(normalized, out var conflictingPlugin))
                {
                    throw new InvalidOperationException(
                        $"File conflict: '{target}' is already modified by plugin '{conflictingPlugin}'. " +
                        $"Uninstall that plugin's mod first, or contact the plugin authors to resolve the conflict.");
                }
            }
        }
    }

    private static List<string> GetTargetPaths(InstallAction action, string packageFilesDir)
    {
        return action switch
        {
            CopyFileAction cf => [cf.Target],
            ReplaceFileAction rf => [rf.Target],
            CopyFolderAction cfo => EnumerateCopyFolderTargets(cfo, packageFilesDir),
            _ => []
        };
    }

    /// <summary>
    /// The game-relative target paths a <c>copyFolder</c> action will write, enumerated from the
    /// extracted source folder so they take part in cross-plugin collision detection exactly like
    /// <c>copyFile</c>/<c>replaceFile</c> targets. Without this a copyFolder could silently clobber
    /// files another plugin owns.
    /// </summary>
    private static List<string> EnumerateCopyFolderTargets(CopyFolderAction action, string packageFilesDir)
    {
        var sourceDir = Path.Combine(packageFilesDir, action.SourceDir);
        if (!Directory.Exists(sourceDir)) return [];

        var targets = new List<string>();
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativeToSource = Path.GetRelativePath(sourceDir, sourceFile);
            targets.Add(Path.Combine(action.TargetDir, relativeToSource));
        }
        return targets;
    }
}
