using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Installer;

public sealed record DependencyInstallResult(
    bool Succeeded,
    DependencyReceipt? Receipt,
    string? ErrorMessage,
    bool WasAlreadyInstalled = false,
    DepAcquisition? Acquisition = null);

/// <summary>
/// One dependency refcount change made during a single install attempt: either this plugin was
/// added to an existing receipt's refcount, or the dependency was freshly installed for it. The
/// engine records these so a failed mod install can release exactly what the attempt acquired.
/// </summary>
public sealed record DepAcquisition(string DependencyId, bool InstalledFresh);

/// <summary>
/// Downloads and applies a single <see cref="DependencyAutoInstall"/>. Same security model as
/// the mod ZIP: HTTPS only, mandatory SHA256 (Q2=A), zip-slip-safe extraction (for
/// <see cref="ExtractZipAutoInstall"/>), backup-on-conflict per F4/F7=A so a hand-installed
/// loader's files are restorable on rollback.
/// </summary>
public sealed class DependencyAutoInstaller
{
    private readonly HttpClient _httpClient;
    private readonly IDependencyReceiptStore _receiptStore;
    private readonly ILogger _logger;

    public DependencyAutoInstaller(
        HttpClient httpClient,
        IDependencyReceiptStore receiptStore,
        ILogger logger)
    {
        _httpClient = httpClient;
        _receiptStore = receiptStore;
        _logger = logger;
    }

    public async Task<DependencyInstallResult> InstallAsync(
        Dependency dependency,
        GameInstall game,
        string requestingPluginId,
        IDependencyHost? host,
        CancellationToken ct)
    {
        var auto = dependency.Fix?.AutoInstall;
        if (auto == null)
            throw new InvalidOperationException(
                $"Dependency '{dependency.Id}' has no AutoInstall — caller should not have routed it here.");

        var url = dependency.Fix?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                $"Dependency '{dependency.Id}' AutoInstall is set but DownloadUrl is empty.");

        UrlValidator.RequireHttps(new Uri(url), $"dependency '{dependency.Id}' download");

        var existing = await _receiptStore.LoadAsync(game.Game.GameId, dependency.Id);
        if (existing != null &&
            string.Equals(existing.Sha256, auto.Sha256, StringComparison.OrdinalIgnoreCase) &&
            existing.Kind == KindLabel(auto) &&
            DependencyFilesPresent(existing, game.InstallPath))
        {
            // Genuinely installed — same artifact, same kind, and its added files are still on
            // disk (e.g. another plugin already put this loader there). Bump the refcount and
            // short-circuit. A receipt for a DIFFERENT artifact/kind is not proof of anything:
            // reinstall the requested one instead of trusting it (audit finding 25).
            // The Acquisition token reflects EXACTLY what changed: null when the plugin was
            // already listed, so a later failure-cleanup can never strip a pre-existing refcount.
            var refcountAdded = false;
            if (!existing.DependentPluginIds.Contains(requestingPluginId))
            {
                existing.DependentPluginIds.Add(requestingPluginId);
                await _receiptStore.SaveAsync(existing);
                refcountAdded = true;
                _logger.Information("Dep {DepId} already installed; added {Plugin} to refcount",
                    dependency.Id, requestingPluginId);
            }
            return new DependencyInstallResult(true, existing, null, WasAlreadyInstalled: true,
                Acquisition: refcountAdded ? new DepAcquisition(dependency.Id, InstalledFresh: false) : null);
        }

        // Either no receipt, or a receipt we can't trust as-is (files gone after a game
        // update/repair, or a different artifact/kind than requested). We only reach InstallAsync
        // when the dependency checker already reported the dep missing, so (re)install rather
        // than trust the stale receipt. Keep any prior dependents so the refcount survives.
        // The old copy is removed INSIDE the try below, only after the replacement downloaded and
        // hash-verified — removing it up front left a stale, unretryable receipt whenever the
        // download then failed.
        if (existing != null)
            _logger.Warning("Dep {DepId} has a receipt but needs reinstalling.", dependency.Id);
        var priorDependents = existing?.DependentPluginIds ?? new List<string>();

        host?.OnDependencyStarting(dependency.Id, KindLabel(auto), dependency.Id);

        string? tempFile = null;
        // Tracked outside the try so a failure mid-extract/copy rolls back what was already written.
        var changes = new List<FileChange>();
        string? backupFolder = null;
        try
        {
            tempFile = await DownloadAsync(url, dependency.Id, ct);
            await VerifySha256Async(tempFile, auto.Sha256, dependency.Id, ct);

            // Only now — with a verified replacement in hand — remove the old copy: restore the
            // pre-dependency originals, clear the old backups, and drop the old receipt. Without
            // the restore-first step, the fresh install's backup-on-conflict would capture the OLD
            // dependency's files over the only copy of the user's originals; final removal would
            // then "restore" the old loader instead of the original game file.
            if (existing != null)
            {
                var undoFailures = RollBackDependencyChanges(existing.Changes, game.InstallPath, existing.BackupFolder);
                if (undoFailures.Count > 0)
                {
                    SafeNotifyFinished(host, dependency.Id, succeeded: false);
                    return new DependencyInstallResult(false, existing,
                        $"couldn't cleanly remove the old copy of '{dependency.Id}' before reinstalling " +
                        $"(first stuck file: {undoFailures[0]}). Close anything using the game folder and try again.");
                }
                try
                {
                    if (Directory.Exists(existing.BackupFolder))
                        Directory.Delete(existing.BackupFolder, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Couldn't clear old dep backup folder {Folder}", existing.BackupFolder);
                }
                // The originals are back and the old backups are gone — the old receipt describes
                // nothing real any more. Dropping it keeps a mid-extract failure retryable (the
                // catch below restores this attempt's own changes from its own fresh backups).
                await _receiptStore.DeleteAsync(game.Game.GameId, dependency.Id);
            }

            backupFolder = _receiptStore.GetBackupDirectory(game.Game.GameId, dependency.Id);
            Directory.CreateDirectory(backupFolder);

            string kind;
            switch (auto)
            {
                case ExtractZipAutoInstall ez:
                    kind = "extractZip";
                    ExtractZip(tempFile, ez, game.InstallPath, backupFolder, changes);
                    break;
                case RunInstallerAutoInstall ri:
                    kind = "runInstaller";
                    changes.AddRange(await RunInstallerAsync(tempFile, ri, host, ct));
                    break;
                case CopyFileAutoInstall cf:
                    kind = "copyFile";
                    CopyFile(tempFile, cf, url, game.InstallPath, backupFolder, changes);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown AutoInstall kind: {auto.GetType().Name}");
            }

            var receipt = new DependencyReceipt
            {
                GameId = game.Game.GameId,
                DependencyId = dependency.Id,
                Kind = kind,
                InstalledAt = DateTime.UtcNow,
                Sha256 = auto.Sha256,
                Changes = changes,
                BackupFolder = backupFolder,
                DependentPluginIds = priorDependents.Contains(requestingPluginId)
                    ? priorDependents
                    : priorDependents.Append(requestingPluginId).ToList()
            };
            await _receiptStore.SaveAsync(receipt);

            // Progress callbacks marshal into the UI and must never affect transactional truth —
            // the receipt above IS saved, so the acquisition must reach the caller even if the
            // notification throws.
            SafeNotifyFinished(host, dependency.Id, succeeded: true);
            return new DependencyInstallResult(true, receipt, null,
                Acquisition: new DepAcquisition(dependency.Id, InstalledFresh: true));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Dep auto-install failed: {DepId}", dependency.Id);
            // Roll back whatever this attempt already wrote so a failed dependency install doesn't
            // leave a half-installed loader behind (no receipt is written on failure).
            if (backupFolder != null && changes.Count > 0)
                RollBackDependencyChanges(changes, game.InstallPath, backupFolder);
            SafeNotifyFinished(host, dependency.Id, succeeded: false);
            return new DependencyInstallResult(false, null, ex.Message);
        }
        finally
        {
            if (tempFile != null)
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Drops <paramref name="pluginId"/> from the refcount of every auto-installed dependency for
    /// this game. When a dependency's refcount reaches zero — no installed mod needs it any more —
    /// its files are removed and its receipt deleted. Called when a mod is uninstalled. Returns the
    /// changes that could NOT be undone; on any failure the dependency's receipt is kept (with the
    /// plugin already removed from its refcount) so the evidence survives for a retry.
    /// </summary>
    public async Task<List<string>> ReleaseDependenciesForPluginAsync(GameInstall game, string pluginId, CancellationToken ct)
    {
        var failures = new List<string>();
        var receipts = await _receiptStore.LoadAllForGameAsync(game.Game.GameId);
        foreach (var receipt in receipts)
        {
            ct.ThrowIfCancellationRequested();
            failures.AddRange(await ReleasePluginFromReceiptAsync(game, receipt, pluginId));
        }
        return failures;
    }

    /// <summary>
    /// Drops one plugin from one dependency receipt. When the plugin is the SOLE dependent, the
    /// dependency's files are rolled back FIRST and the refcount removal commits only on success —
    /// a failure leaves the receipt untouched (plugin still listed) so a retry re-enters this exact
    /// path instead of skipping a zero-refcount orphan forever.
    /// </summary>
    private async Task<List<string>> ReleasePluginFromReceiptAsync(
        GameInstall game, DependencyReceipt receipt, string pluginId)
    {
        if (!receipt.DependentPluginIds.Contains(pluginId))
            return new List<string>();

        if (receipt.DependentPluginIds.Count > 1)
        {
            receipt.DependentPluginIds.Remove(pluginId);
            await _receiptStore.SaveAsync(receipt);
            _logger.Information("Dependency {DepId} still needed by {Count} mod(s) after {Plugin} released it",
                receipt.DependencyId, receipt.DependentPluginIds.Count, pluginId);
            return new List<string>();
        }

        _logger.Information("Dependency {DepId} no longer needed by any mod; removing it", receipt.DependencyId);
        var failed = RollBackDependencyChanges(receipt.Changes, game.InstallPath, receipt.BackupFolder);
        if (failed.Count == 0)
        {
            await _receiptStore.DeleteAsync(game.Game.GameId, receipt.DependencyId);
            return new List<string>();
        }

        _logger.Error("Dependency {DepId} removal could not restore {Count} file(s); receipt kept unchanged for retry",
            receipt.DependencyId, failed.Count);
        return failed.Select(f => $"{receipt.DependencyId}: {f}").ToList();
    }

    /// <summary>
    /// Releases the refcount bumps and fresh installs a single failed mod-install attempt made
    /// (audit finding 2: a mod that never got installed must not stay refcounted forever — its
    /// receipt-less uninstall could never release it). Best-effort per entry; a freshly-installed
    /// dependency whose refcount drops to zero is removed again, with the same keep-evidence rule
    /// on rollback failure.
    /// </summary>
    public async Task ReleaseAcquisitionsAsync(GameInstall game, string pluginId, IReadOnlyList<DepAcquisition> acquisitions)
    {
        for (var i = acquisitions.Count - 1; i >= 0; i--)
        {
            var acq = acquisitions[i];
            try
            {
                var receipt = await _receiptStore.LoadAsync(game.Game.GameId, acq.DependencyId);
                if (receipt == null) continue;
                var failed = await ReleasePluginFromReceiptAsync(game, receipt, pluginId);
                if (failed.Count > 0)
                {
                    _logger.Error("Releasing dep {DepId} after a failed install left {Count} file(s) unrestored; " +
                                  "its receipt was kept for retry", acq.DependencyId, failed.Count);
                }
                else
                {
                    _logger.Information("Released dep acquisition {DepId} for {Plugin} after failed install",
                        acq.DependencyId, pluginId);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Couldn't release dep acquisition {DepId} for {Plugin}", acq.DependencyId, pluginId);
            }
        }
    }

    /// <summary>
    /// Drops <paramref name="pluginId"/> from dependencies the game definition no longer declares
    /// (audit finding 24: updates never reconciled removed dependencies). Called after a
    /// successful update. Zero-refcount removal follows the same keep-evidence-on-failure rule.
    /// </summary>
    public async Task ReconcileDeclaredDependenciesAsync(GameInstall game, string pluginId, CancellationToken ct)
    {
        var declared = game.Game.Dependencies
            .Where(d => !d.IsGameInstaller)
            .Select(d => d.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var receipts = await _receiptStore.LoadAllForGameAsync(game.Game.GameId);
        foreach (var receipt in receipts)
        {
            ct.ThrowIfCancellationRequested();
            if (declared.Contains(receipt.DependencyId)) continue;
            if (!receipt.DependentPluginIds.Contains(pluginId)) continue;

            _logger.Information("Dep {DepId} is no longer declared by the game definition; dropping {Plugin} from it",
                receipt.DependencyId, pluginId);
            var failed = await ReleasePluginFromReceiptAsync(game, receipt, pluginId);
            if (failed.Count > 0)
            {
                _logger.Error("Dropping undeclared dep {DepId} left {Count} file(s) unrestored; receipt kept for retry",
                    receipt.DependencyId, failed.Count);
            }
        }
    }

    /// <summary>Progress notifications must never affect transactional outcomes — a UI-marshaled
    /// callback that throws after the receipt is saved would otherwise lose the acquisition.</summary>
    private void SafeNotifyFinished(IDependencyHost? host, string dependencyId, bool succeeded)
    {
        try { host?.OnDependencyFinished(dependencyId, succeeded); }
        catch (Exception ex) { _logger.Warning(ex, "Dependency progress callback threw for {DepId}", dependencyId); }
    }

    /// <summary>Engine-facing passthroughs so refcount reconciliation reads/writes receipts
    /// through the same store without the engine taking its own store dependency.</summary>
    public Task<DependencyReceipt?> LoadReceiptAsync(string gameId, string dependencyId) =>
        _receiptStore.LoadAsync(gameId, dependencyId);

    public Task SaveReceiptAsync(DependencyReceipt receipt) => _receiptStore.SaveAsync(receipt);

    public Task<bool> HasUnreadableDependencyReceiptsAsync(string gameId) =>
        _receiptStore.AnyUnreadableForGameAsync(gameId);

    /// <summary>
    /// Undoes the file changes from a dependency install: removes added files, restores replaced
    /// files from the dependency's backup folder. Mirrors the mod-install rollback. Returns the
    /// relative paths that could NOT be undone (a missing backup for a replaced file counts).
    /// </summary>
    private List<string> RollBackDependencyChanges(List<FileChange> changes, string gameDir, string backupFolder)
    {
        var failed = new List<string>();
        foreach (var change in Enumerable.Reverse(changes))
        {
            try
            {
                var target = Path.Combine(gameDir, change.RelativePath);
                if (change.Type == ChangeType.Added)
                {
                    if (File.Exists(target)) File.Delete(target);
                }
                else
                {
                    var backup = string.IsNullOrEmpty(change.BackupRelativePath)
                        ? null
                        : Path.Combine(backupFolder, change.BackupRelativePath);
                    if (backup != null && File.Exists(backup))
                        File.Copy(backup, target, overwrite: true);
                    else
                        failed.Add(change.RelativePath);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to roll back dependency file {Path}", change.RelativePath);
                failed.Add(change.RelativePath);
            }
        }
        return failed;
    }

    /// <summary>
    /// Is the dependency actually on disk? A receipt alone isn't proof — a game update/repair (or
    /// manual cleanup) can wipe a loader's files while the receipt lingers. We check the files the
    /// dep <em>added</em> (replaced/patched entries were originals, not the dep's own). A receipt
    /// with NO added files gives no evidence either way — and this method is only consulted when
    /// the dependency checker just said the dep is missing, so "no evidence" means reinstall
    /// (audit finding 25; this includes runInstaller receipts — rerunning the installer is the fix).
    /// </summary>
    private static bool DependencyFilesPresent(DependencyReceipt receipt, string gameDir)
    {
        var added = receipt.Changes.Where(c => c.Type == ChangeType.Added).ToList();
        if (added.Count == 0) return false;
        return added.All(c =>
        {
            var p = Path.Combine(gameDir, c.RelativePath);
            return File.Exists(p) || Directory.Exists(p);
        });
    }

    /// <summary>
    /// Runs a game-installer dependency: download (HTTPS + mandatory SHA256), then run the
    /// installer and wait for it to exit. Unlike <see cref="InstallAsync"/> this writes NO
    /// dependency receipt — a game install isn't a tracked/rolled-back change, and "is the game
    /// installed?" is answered by detection (the game's RegistryProbe), not a receipt. Throws on
    /// a download / hash / non-zero-exit failure.
    /// </summary>
    public async Task RunGameInstallerAsync(Dependency dependency, IDependencyHost? host, CancellationToken ct)
    {
        if (dependency.Fix?.AutoInstall is not RunInstallerAutoInstall ri)
            throw new InvalidOperationException(
                $"Game-installer dependency '{dependency.Id}' must use a runInstaller auto-install.");

        var url = dependency.Fix?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                $"Game-installer dependency '{dependency.Id}' has no download URL.");

        UrlValidator.RequireHttps(new Uri(url), $"game installer '{dependency.Id}' download");

        host?.OnDependencyStarting(dependency.Id, "runInstaller", dependency.Id);

        string? tempFile = null;
        try
        {
            tempFile = await DownloadAsync(url, dependency.Id, ct);
            await VerifySha256Async(tempFile, ri.Sha256, dependency.Id, ct);
            await RunInstallerAsync(tempFile, ri, host, ct, throwOnNonZeroExit: false);
            host?.OnDependencyFinished(dependency.Id, succeeded: true);
        }
        catch
        {
            host?.OnDependencyFinished(dependency.Id, succeeded: false);
            throw;
        }
        finally
        {
            if (tempFile != null)
            {
                try { File.Delete(tempFile); } catch { /* best effort; RunInstaller may have renamed it */ }
            }
        }
    }

    /// <summary>
    /// Installs a portable-app (emulator) game-installer dependency: download (HTTPS + mandatory
    /// SHA256), then extract the ZIP into <paramref name="destinationFolder"/> with the same
    /// zip-slip-safe extractor the mod install uses. Like <see cref="RunGameInstallerAsync"/> this
    /// writes NO dependency receipt — the app is tracked by detection (a <c>KnownGameOverrides</c>
    /// entry the caller writes), not a rolled-back change. Throws on a download / hash / extraction
    /// failure. See EMULATOR_INSTALL_QUESTIONS.md.
    /// </summary>
    public async Task ExtractPortableAppAsync(
        Dependency dependency, string destinationFolder, IDependencyHost? host, CancellationToken ct)
    {
        if (dependency.Fix?.AutoInstall is not ExtractAppAutoInstall app)
            throw new InvalidOperationException(
                $"Portable-app dependency '{dependency.Id}' must use an extractApp auto-install.");

        var url = dependency.Fix?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                $"Portable-app dependency '{dependency.Id}' has no download URL.");

        UrlValidator.RequireHttps(new Uri(url), $"portable app '{dependency.Id}' download");

        host?.OnDependencyStarting(dependency.Id, "extractApp", dependency.Id);

        string? tempFile = null;
        try
        {
            tempFile = await DownloadAsync(url, dependency.Id, ct);
            await VerifySha256Async(tempFile, app.Sha256, dependency.Id, ct);

            // Reuse the same zip-slip-safe extractor the main install uses (it only needs the
            // logger). No FileChange tracking: a portable app isn't a rolled-back mod change.
            var extractor = new SafeZipExtractor(_logger);
            await extractor.ExtractAsync(tempFile, destinationFolder, ct);

            host?.OnDependencyFinished(dependency.Id, succeeded: true);
            _logger.Information("Portable app {DepId} extracted to {Dir}", dependency.Id, destinationFolder);
        }
        catch
        {
            host?.OnDependencyFinished(dependency.Id, succeeded: false);
            throw;
        }
        finally
        {
            if (tempFile != null)
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
        }
    }

    private async Task<string> DownloadAsync(string url, string depId, CancellationToken ct)
    {
        // Keep the URL's file extension on the temp file. An installer in particular must keep its
        // .msi / .exe so it's launched the right way later — a .msi renamed to .exe makes Windows
        // try to exec a non-PE file and fail with "Unsupported 16-Bit Application". Harmless for
        // zip/copyFile, which open the artifact by path regardless of extension.
        var tempFile = Path.Combine(Path.GetTempPath(), "AccessibilityModManager",
            $"depdl_{depId}_{Guid.NewGuid():N}{SafeUrlExtension(url)}");
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);

        _logger.Information("Downloading dep {DepId} from {Url}", depId, url);
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var fs = File.Create(tempFile);
        await response.Content.CopyToAsync(fs, ct);
        return tempFile;
    }

    /// <summary>
    /// The file extension from a URL's path (e.g. <c>.msi</c>), or empty. Guards against junk:
    /// only short, alphanumeric extensions are kept.
    /// </summary>
    private static string SafeUrlExtension(string url)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (!string.IsNullOrEmpty(ext) && ext.Length <= 6 && ext.Skip(1).All(char.IsLetterOrDigit))
                return ext.ToLowerInvariant();
        }
        catch { /* not a parseable URL extension — fall through */ }
        return string.Empty;
    }

    private async Task VerifySha256Async(string filePath, string expected, string depId, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, ct);
        var actual = Convert.ToHexStringLower(hashBytes);
        if (!string.Equals(actual, expected.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Dependency '{depId}' SHA256 mismatch. Expected {expected}, got {actual}. Aborting.");
        }
        _logger.Information("Dep {DepId} SHA256 verified: {Hash}", depId, actual);
    }

    private void ExtractZip(
        string zipPath, ExtractZipAutoInstall action, string gameDir, string backupFolder,
        List<FileChange> changes)
    {
        var targetDir = ResolveTargetDir(gameDir, action.TargetDir);
        Directory.CreateDirectory(targetDir);

        var fullGameDir = Path.GetFullPath(gameDir);
        var blocklist = action.Blocklist ?? new List<string>();

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            if (IsBlocked(entry.FullName, blocklist)) continue;

            var destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!PathSafety.IsContained(targetDir, destPath))
                throw new SecurityException(
                    $"Zip slip detected in dependency: '{entry.FullName}' would extract outside '{targetDir}'.");

            // Backup-on-conflict (F7=A): if the file already exists, stash a copy before overwriting.
            var relativeToGame = Path.GetRelativePath(fullGameDir, destPath);
            FileChange change;
            if (File.Exists(destPath))
            {
                var backupRel = Path.Combine(relativeToGame);
                var backupFull = Path.Combine(backupFolder, backupRel);
                Directory.CreateDirectory(Path.GetDirectoryName(backupFull)!);
                File.Copy(destPath, backupFull, overwrite: true);
                change = new FileChange
                {
                    Type = ChangeType.Replaced,
                    RelativePath = relativeToGame,
                    BackupRelativePath = backupRel
                };
            }
            else
            {
                change = new FileChange
                {
                    Type = ChangeType.Added,
                    RelativePath = relativeToGame
                };
            }

            // Record the change BEFORE writing so a failure mid-write is still rolled back (the
            // backup, if any, was already taken above).
            changes.Add(change);

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var es = entry.Open();
            using var fs = File.Create(destPath);
            es.CopyTo(fs);
        }

        _logger.Information("Dep extract complete: {Count} files placed in {Dir}", changes.Count, targetDir);
    }

    private void CopyFile(
        string sourcePath, CopyFileAutoInstall action, string downloadUrl,
        string gameDir, string backupFolder, List<FileChange> changes)
    {
        var targetDir = ResolveTargetDir(gameDir, action.TargetDir);
        // Both name sources are untrusted (author-typed, or derived from the download URL's last
        // path segment) — either way it must be a bare file name, or it could ignore the resolved
        // target directory or address an NTFS alternate data stream.
        var fileName = PathSafety.EnsureLeafFileName(
            string.IsNullOrWhiteSpace(action.TargetFileName)
                ? Path.GetFileName(new Uri(downloadUrl).LocalPath)
                : action.TargetFileName,
            "copyFile target file name");

        var destPath = Path.GetFullPath(Path.Combine(targetDir, fileName));
        var fullGameDir = Path.GetFullPath(gameDir);
        PathSafety.EnsureContained(fullGameDir, destPath, "copyFile target");

        Directory.CreateDirectory(targetDir);

        var relativeToGame = Path.GetRelativePath(fullGameDir, destPath);
        FileChange change;
        if (File.Exists(destPath))
        {
            var backupRel = relativeToGame;
            var backupFull = Path.Combine(backupFolder, backupRel);
            Directory.CreateDirectory(Path.GetDirectoryName(backupFull)!);
            File.Copy(destPath, backupFull, overwrite: true);
            change = new FileChange
            {
                Type = ChangeType.Replaced,
                RelativePath = relativeToGame,
                BackupRelativePath = backupRel
            };
        }
        else
        {
            change = new FileChange
            {
                Type = ChangeType.Added,
                RelativePath = relativeToGame
            };
        }

        // Record the change BEFORE writing so a failure mid-write is still rolled back.
        changes.Add(change);
        File.Copy(sourcePath, destPath, overwrite: true);
        _logger.Information("Dep copyFile placed {File}", relativeToGame);
    }

    private async Task<List<FileChange>> RunInstallerAsync(
        string installerPath, RunInstallerAutoInstall action, IDependencyHost? host,
        CancellationToken ct, bool throwOnNonZeroExit = true)
    {
        var ext = Path.GetExtension(installerPath).ToLowerInvariant();

        // Extensionless download (the URL had no extension): assume a native .exe so Windows runs
        // it directly.
        if (string.IsNullOrEmpty(ext))
        {
            var renamed = installerPath + ".exe";
            File.Move(installerPath, renamed);
            installerPath = renamed;
            ext = ".exe";
        }

        var psi = new ProcessStartInfo
        {
            UseShellExecute = action.NeedsAdmin,
            CreateNoWindow = !action.NeedsAdmin,
            RedirectStandardOutput = !action.NeedsAdmin,
            RedirectStandardError = !action.NeedsAdmin
        };

        if (ext == ".msi")
        {
            // An .msi is a database, not an executable — it has to run through msiexec. Executing
            // it directly makes Windows try to load a non-PE file and fail with "Unsupported
            // 16-Bit Application".
            psi.FileName = "msiexec.exe";
            psi.ArgumentList.Add("/i");
            psi.ArgumentList.Add(installerPath);
        }
        else
        {
            psi.FileName = installerPath;
        }

        if (action.NeedsAdmin)
            psi.Verb = "runas";
        else
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        foreach (var a in action.Args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        if (!action.NeedsAdmin)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data != null) host?.OnDependencyOutputLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) host?.OnDependencyOutputLine(e.Data); };
        }

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start dependency installer process.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (action.NeedsAdmin && ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("User declined the elevation prompt for the dependency installer.", ex);
        }

        if (!action.NeedsAdmin)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            if (throwOnNonZeroExit)
                throw new InvalidOperationException(
                    $"Dependency installer exited with code {process.ExitCode}.");
            // Game-installer path: the installer is interactive and may exit non-zero on a user
            // cancel (1602) or reboot-required (3010). Don't treat that as fatal here — the caller
            // decides success by re-detecting the game.
            _logger.Warning("Installer exited with code {Code}; leaving success to detection.", process.ExitCode);
        }

        // runInstaller's outputs aren't tracked as FileChanges — the installer owns its files.
        return new List<FileChange>();
    }

    private static string ResolveTargetDir(string gameDir, string? targetDir)
    {
        // Authors write targetDir by hand; leading/trailing slashes are noise, not intent
        // ("/Updater/1.5.0/" means <game>\Updater\1.5.0 — the live PTCG failure). Absolute values
        // and ".." are rejected with a message that says what to write instead; the containment
        // check stays as defense in depth behind the normalization.
        var relative = PathSafety.NormalizeRelativeDir(targetDir, "AutoInstall targetDir");
        var resolved = relative.Length == 0
            ? Path.GetFullPath(gameDir)
            : Path.GetFullPath(Path.Combine(gameDir, relative));
        return PathSafety.EnsureContained(gameDir, resolved, "AutoInstall targetDir");
    }

    private static bool IsBlocked(string entryFullName, List<string> blocklist)
    {
        var name = entryFullName.Replace('\\', '/');
        foreach (var pattern in blocklist)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            // Simple glob: '*' matches any sequence of chars (no path-separator semantics — the
            // author can write 'docs/*' or '*.md' and we honor them as case-insensitive substrings.)
            var trimmed = pattern.Replace('\\', '/');
            if (GlobMatch(name, trimmed)) return true;
        }
        return false;
    }

    private static bool GlobMatch(string input, string pattern)
    {
        // Translate the glob to a simple regex (case-insensitive, anchored, '*' → '.*').
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => System.Text.RegularExpressions.Regex.Escape(c.ToString())
            });
        }
        sb.Append('$');
        return System.Text.RegularExpressions.Regex.IsMatch(input, sb.ToString(),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string KindLabel(DependencyAutoInstall auto) => auto switch
    {
        ExtractZipAutoInstall => "extractZip",
        RunInstallerAutoInstall => "runInstaller",
        CopyFileAutoInstall => "copyFile",
        _ => auto.GetType().Name
    };
}
