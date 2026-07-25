using System.Collections.Concurrent;
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
/// On any fatal failure: automatic rollback, and dependency refcounts acquired for the attempt
/// are released again.
/// Every public mutation (install / update / uninstall) holds a per-game lock — in-process and
/// cross-process — so two operations can never interleave their collision checks and writes.
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
    private readonly IGameVerifier _gameVerifier;
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
        IGameVerifier gameVerifier,
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
        _gameVerifier = gameVerifier;
        _logger = logger;
    }

    // ---------------------------------------------------------------- per-game mutation lock

    private const string GameBusyMessage =
        "Another install, update, or uninstall for this game is already running. Let it finish, then try again.";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InProcessGameLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string LocksRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager", "locks");

    private sealed class GameMutationLock : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly FileStream _lockFile;
        public GameMutationLock(SemaphoreSlim semaphore, FileStream lockFile)
        {
            _semaphore = semaphore;
            _lockFile = lockFile;
        }
        public void Dispose()
        {
            try { _lockFile.Dispose(); } catch { /* DeleteOnClose best effort */ }
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Serializes mutations per game folder. The in-process semaphore stops a second command in
    /// this app (Install on one mod, Uninstall on another — the progress dialog is not modal);
    /// the exclusively-opened lock file stops a second copy of the manager. Contention fails
    /// fast with a clear message instead of queueing — an invisible queue behind a modal-less
    /// dialog would be confusing to wait on.
    /// </summary>
    private static GameMutationLock AcquireGameLock(GameInstall game)
    {
        // Resolve the final path so aliases of the same physical folder (the ASCII junction vs
        // the real install, mapped drives) converge on one lock key. Lexical normalization alone
        // would hand two manager instances two different "exclusive" locks for the same game.
        var lockTarget = game.InstallPath;
        try
        {
            lockTarget = Directory.ResolveLinkTarget(game.InstallPath, returnFinalTarget: true)?.FullName
                         ?? game.InstallPath;
        }
        catch
        {
            // Not resolvable (missing, access denied) — the preflight will produce the real error.
        }
        // Resolved reparse targets can carry the NT namespace prefix (\??\ or \\?\) — the same
        // normalization AsciiPathShimService does. Without stripping it, the junction-resolved
        // key and the direct real-path key would STILL differ and the alias fix would be moot.
        if (lockTarget.StartsWith(@"\??\", StringComparison.Ordinal) ||
            lockTarget.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            lockTarget = lockTarget[4..];
        }
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lockTarget)).ToLowerInvariant();
        var semaphore = InProcessGameLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!semaphore.Wait(0))
            throw new InvalidOperationException(GameBusyMessage);

        try
        {
            Directory.CreateDirectory(LocksRoot);
            var lockName = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".lock";
            var lockFile = new FileStream(
                Path.Combine(LocksRoot, lockName),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            return new GameMutationLock(semaphore, lockFile);
        }
        catch (IOException)
        {
            semaphore.Release();
            throw new InvalidOperationException(
                GameBusyMessage + " It may be running in another copy of the manager.");
        }
        catch
        {
            semaphore.Release();
            throw;
        }
    }

    // ---------------------------------------------------------------- preflight guards

    /// <summary>
    /// The game folder must still be there — and for install/update, still verify as the game —
    /// right before we mutate. Detection ran earlier; the folder can have moved or been gutted
    /// since, and blindly recreating the stale path would install into a ghost folder.
    /// </summary>
    private void EnsureGamePresentForMutation(GameInstall game, bool fullVerify)
    {
        if (!Directory.Exists(game.InstallPath))
            throw new InvalidOperationException(
                $"The game folder '{game.InstallPath}' no longer exists. Refresh the games list and try again.");

        if (fullVerify && !_gameVerifier.VerifyInstallPath(game.Game, game.InstallPath))
            throw new InvalidOperationException(
                $"The folder '{game.InstallPath}' no longer looks like a valid {game.Game.DisplayName} install. " +
                "Refresh the games list and try again.");
    }

    /// <summary>
    /// Fail closed when any receipt for this game exists on disk but can't be trusted. Treating
    /// an unreadable receipt as "no mod installed" would drop its files out of collision
    /// ownership and let this install overwrite them.
    /// </summary>
    private async Task EnsureNoUnreadableReceiptsAsync(GameInstall game)
    {
        var unreadable = await _receiptStore.UnreadablePluginIdsForGameAsync(game.Game.GameId);
        if (unreadable.Count > 0)
        {
            throw new InvalidOperationException(
                $"An install record for this game could not be read (plugin: {string.Join(", ", unreadable)}). " +
                "Continuing could overwrite files owned by another mod, so nothing was changed. " +
                "The unreadable record was preserved with a '.corrupt' copy next to it.");
        }

        // Same rule for shared-dependency records: resolving deps against a partial view could
        // drop existing dependents or overwrite the corrupt receipt's rollback evidence.
        if (await _depAutoInstaller.HasUnreadableDependencyReceiptsAsync(game.Game.GameId))
        {
            throw new InvalidOperationException(
                "A shared-dependency record for this game could not be read, so nothing was changed. " +
                "The unreadable record was preserved with a '.corrupt' copy next to it.");
        }
    }

    // ---------------------------------------------------------------- public API

    public async Task<InstallReceipt> InstallAsync(
        GameInstall game, ModRelease release, string packageZipPath,
        IScriptHost? scriptHost = null, IDependencyHost? dependencyHost = null,
        CancellationToken ct = default)
    {
        using var gameLock = AcquireGameLock(game);
        _logger.Information("Starting install: {PluginId}/{GameId} v{Version}", release.PluginId, release.GameId, release.Version);

        EnsureGamePresentForMutation(game, fullVerify: true);
        await EnsureNoUnreadableReceiptsAsync(game);

        // Cheap preflight BEFORE any dependency is installed or refcounted — a duplicate install
        // must not leave a dependency acquired for a mod that never got installed.
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

        // Q8 redesign: install missing required deps as a step in this flow. Every refcount bump
        // and fresh install is recorded so a downstream failure can release it again.
        var acquisitions = await ResolveDependenciesAsync(game, release.PluginId, dependencyHost, ct);

        try
        {
            // Q9=A: warn the user about lifecycle scripts on first install (no existing receipt).
            return await ExecuteTransactionalInstall(
                game, release, packageZipPath, otherReceipts,
                scriptHost, scriptsAlreadyConfirmed: false, previousScriptsFingerprint: null, ct);
        }
        catch
        {
            // The mod did not install — undo this attempt's dependency acquisitions so refcounts
            // only ever reflect mods that are actually installed. EXCEPT when a partial rollback
            // forced a recovery receipt to be saved: then the mod's files ARE partly present and
            // owned, and its eventual uninstall must still release the deps. Uses no cancellation
            // token: cleanup must run even when the failure IS a cancellation.
            if (await _receiptStore.LoadAsync(game.Game.GameId, release.PluginId) == null)
                await _depAutoInstaller.ReleaseAcquisitionsAsync(game, release.PluginId, acquisitions);
            throw;
        }
    }

    public async Task<InstallReceipt> UpdateAsync(
        GameInstall game, ModRelease release, string packageZipPath,
        IScriptHost? scriptHost = null, IDependencyHost? dependencyHost = null,
        CancellationToken ct = default)
    {
        using var gameLock = AcquireGameLock(game);
        _logger.Information("Starting update: {PluginId}/{GameId} to v{Version}", release.PluginId, release.GameId, release.Version);

        EnsureGamePresentForMutation(game, fullVerify: true);
        await EnsureNoUnreadableReceiptsAsync(game);

        var oldReceipt = await _receiptStore.LoadAsync(game.Game.GameId, release.PluginId);
        if (oldReceipt == null)
        {
            // Nothing installed for this plugin yet — treat as a first install (warns about scripts).
            var acquisitionsFresh = await ResolveDependenciesAsync(game, release.PluginId, dependencyHost, ct);
            try
            {
                var freshOthers = (await _receiptStore.LoadAllForGameAsync(game.Game.GameId))
                    .Where(r => r.PluginId != release.PluginId).ToList();
                return await ExecuteTransactionalInstall(
                    game, release, packageZipPath, freshOthers,
                    scriptHost, scriptsAlreadyConfirmed: false, previousScriptsFingerprint: null, ct);
            }
            catch
            {
                if (await _receiptStore.LoadAsync(game.Game.GameId, release.PluginId) == null)
                    await _depAutoInstaller.ReleaseAcquisitionsAsync(game, release.PluginId, acquisitionsFresh);
                throw;
            }
        }

        // The plugin's mod stays installed whether this update succeeds or is rolled back, so
        // dependency refcounts acquired here are deliberately NOT released on failure — the
        // restored old version needs the same (per-game) dependencies.
        await ResolveDependenciesAsync(game, release.PluginId, dependencyHost, ct);

        // Atomic update: snapshot the old version's installed files AND its cached scripts, so if
        // the new install fails we can put everything back instead of leaving the user with
        // nothing (or with a mod whose uninstall script silently vanished).
        var snapshotDir = Path.Combine(Path.GetTempPath(), "AccessibilityModManager", $"updatebak_{Guid.NewGuid():N}");
        var scriptSnapshotDir = snapshotDir + "_scripts";
        var preserveSnapshots = false;
        try
        {
            SnapshotInstalledFiles(oldReceipt, game.InstallPath, snapshotDir);
            SnapshotDirectory(GetScriptCacheDir(game.Game.GameId, release.PluginId), scriptSnapshotDir);

            // Internal uninstall: keep shared dependencies (the new version needs them), skip the
            // removal-cleanup script (the mod is being replaced, not removed), and keep the old
            // backup folder — if the new install fails, the restored old receipt still needs it.
            await UninstallCoreAsync(game, release.PluginId, scriptHost,
                releaseDependencies: false, runPostUninstall: false, deleteBackups: false, ct);

            var otherReceipts = (await _receiptStore.LoadAllForGameAsync(game.Game.GameId))
                .Where(r => r.PluginId != release.PluginId).ToList();

            // Re-warn about scripts only if they changed since the user last agreed (see the consent
            // logic in ExecuteTransactionalInstall) — a new or modified script needs a fresh notice.
            var receipt = await ExecuteTransactionalInstall(
                game, release, packageZipPath, otherReceipts,
                scriptHost, scriptsAlreadyConfirmed: true,
                previousScriptsFingerprint: oldReceipt.ScriptsFingerprint, ct);

            // The update committed. The old backup folder is now redundant (the new receipt's
            // backups captured the same originals the internal uninstall restored), and shared
            // dependencies the game definition no longer declares can drop this plugin. Both are
            // best-effort — the update itself already succeeded.
            TryDeleteBackupFolder(game, oldReceipt.BackupFolder);
            try
            {
                await _depAutoInstaller.ReconcileDeclaredDependenciesAsync(game, release.PluginId, ct);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Post-update dependency reconciliation failed for {PluginId}/{GameId}",
                    release.PluginId, game.Game.GameId);
            }

            return receipt;
        }
        catch (Exception)
        {
            var restored = false;
            try
            {
                RestoreSnapshot(snapshotDir, game.InstallPath);
                RestoreDirectory(scriptSnapshotDir, GetScriptCacheDir(game.Game.GameId, release.PluginId));

                // If the failed transaction saved a recovery receipt (its own rollback couldn't
                // remove some new-version files), fold those changes into the restored old receipt
                // instead of overwriting them — leftover new-version files must stay owned and
                // uninstallable, not become invisible debris.
                var recovery = await _receiptStore.LoadAsync(game.Game.GameId, release.PluginId);
                if (recovery != null && !ReferenceEquals(recovery.Changes, oldReceipt.Changes))
                {
                    var known = oldReceipt.Changes
                        .Select(c => c.RelativePath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var change in recovery.Changes.Where(c => !known.Contains(c.RelativePath)))
                        oldReceipt.Changes.Add(change);
                }

                await _receiptStore.SaveAsync(oldReceipt);
                restored = true;
                _logger.Warning("Update of {PluginId}/{GameId} failed; restored the previous version",
                    release.PluginId, game.Game.GameId);
            }
            catch (Exception restoreEx)
            {
                // The snapshots are now the ONLY copy of the old version — they must survive this
                // failure, and the user must hear about the recovery problem, not just the
                // original update error.
                _logger.Error(restoreEx,
                    "Update failed AND restoring the previous version failed. The old version's files are " +
                    "preserved at {Snapshot} (scripts at {ScriptSnapshot}) for manual repair.",
                    snapshotDir, scriptSnapshotDir);
            }

            preserveSnapshots = !restored;
            throw;
        }
        finally
        {
            if (!preserveSnapshots)
            {
                try { if (Directory.Exists(snapshotDir)) Directory.Delete(snapshotDir, recursive: true); } catch { }
                try { if (Directory.Exists(scriptSnapshotDir)) Directory.Delete(scriptSnapshotDir, recursive: true); } catch { }
            }
        }
    }

    public async Task UninstallAsync(GameInstall game, string pluginId, IScriptHost? scriptHost = null, CancellationToken ct = default)
    {
        using var gameLock = AcquireGameLock(game);
        EnsureGamePresentForMutation(game, fullVerify: false);
        await UninstallCoreAsync(game, pluginId, scriptHost,
            releaseDependencies: true, runPostUninstall: true, deleteBackups: true, ct);
    }

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
    /// <param name="deleteBackups">
    /// When true (a real uninstall whose rollback fully verified), delete the receipt's backup
    /// folder — the originals are back in place, so it has nothing left to protect. The update
    /// path passes false: if the new install fails, the restored old receipt still points at it.
    /// </param>
    private async Task UninstallCoreAsync(GameInstall game, string pluginId, IScriptHost? scriptHost,
        bool releaseDependencies, bool runPostUninstall, bool deleteBackups, CancellationToken ct)
    {
        _logger.Information("Starting uninstall: {PluginId}/{GameId}", pluginId, game.Game.GameId);

        var receipt = await _receiptStore.LoadAsync(game.Game.GameId, pluginId);
        if (receipt == null)
        {
            // Fail closed if a receipt file exists but couldn't be trusted — "nothing to
            // uninstall" would be a lie and would strand the mod's files forever.
            var unreadable = await _receiptStore.UnreadablePluginIdsForGameAsync(game.Game.GameId);
            if (unreadable.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The install record for this mod exists but could not be read, so the manager can't " +
                    "safely undo the install. The unreadable record was preserved with a '.corrupt' copy next to it.");
            }

            _logger.Warning("No receipt found for {PluginId}/{GameId}, nothing to uninstall", pluginId, game.Game.GameId);
            return;
        }

        // Run post-uninstall script first (best-effort: failures don't block the uninstall).
        // The ran-flag is persisted immediately so a retry after a downstream failure (rollback,
        // dependency release) never runs the author's cleanup script — and its side effects —
        // a second time.
        if (runPostUninstall && !receipt.PostUninstallScriptRan &&
            receipt.PostUninstall is not null && !string.IsNullOrEmpty(receipt.CachedPostUninstallExecutable))
        {
            await TryRunPostUninstall(game, receipt, scriptHost, ct);
            receipt.PostUninstallScriptRan = true;
            try { await _receiptStore.SaveAsync(receipt); }
            catch (Exception ex) { _logger.Warning(ex, "Couldn't persist the post-uninstall ran-flag"); }
        }

        // The consent prompt and script above can take a while — make sure the game folder is
        // still there right before files start moving.
        EnsureGamePresentForMutation(game, fullVerify: false);

        var report = await RollbackAsync(game, receipt, ct);
        if (!report.AllRestored)
        {
            throw new InvalidOperationException(
                $"Uninstall could not restore {report.FailedPaths.Count} file(s) — first: '{report.FailedPaths[0]}'. " +
                "The mod's records and backups were kept so you can retry. Close the game (or anything else " +
                "using its folder) and try again.");
        }

        if (releaseDependencies)
        {
            // Same fail-closed rule for dependency receipts: releasing refcounts against a partial
            // view could remove a loader another mod still needs.
            if (await _depAutoInstaller.HasUnreadableDependencyReceiptsAsync(game.Game.GameId))
            {
                throw new InvalidOperationException(
                    "A shared-dependency record for this game could not be read, so the uninstall stopped before " +
                    "touching shared loaders. The mod's own files were restored; its record was kept so you can retry. " +
                    "The unreadable record was preserved with a '.corrupt' copy next to it.");
            }

            var failures = await _depAutoInstaller.ReleaseDependenciesForPluginAsync(game, pluginId, ct);
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The mod's files were removed, but {failures.Count} shared dependency change(s) could not be " +
                    $"released (first: {failures[0]}). The mod's record was kept so you can retry the uninstall.");
            }
        }

        await _receiptStore.DeleteAsync(game.Game.GameId, pluginId);

        // F4 addendum: delete the cached executable after the script ran (or after we
        // attempted to). The receipt itself is gone; the script binary should follow.
        TryDeleteCachedScripts(game.Game.GameId, pluginId);

        // Every restore verified clean, so the backup folder has nothing left to protect
        // (decision on audit finding 30). Kept on any failure above — we never get here then.
        if (deleteBackups)
            TryDeleteBackupFolder(game, receipt.BackupFolder);

        _logger.Information("Uninstall complete: {PluginId}/{GameId}", pluginId, game.Game.GameId);
    }

    public async Task<RollbackReport> RollbackAsync(GameInstall game, InstallReceipt receipt, CancellationToken ct = default)
    {
        _logger.Information("Rolling back: {PluginId}/{GameId} v{Version}",
            receipt.PluginId, receipt.GameId, receipt.InstalledVersion);

        var failed = new List<string>();
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
                        // A replaced file with no recorded backup, or whose backup is gone, cannot
                        // be restored — that is a failure the caller must see, not a shrug.
                        if (string.IsNullOrEmpty(change.BackupRelativePath) ||
                            !_backupManager.RestoreFile(game.InstallPath, change.RelativePath,
                                receipt.BackupFolder, change.BackupRelativePath))
                        {
                            failed.Add(change.RelativePath);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to rollback change: {Type} {Path}", change.Type, change.RelativePath);
                failed.Add(change.RelativePath);
            }
        }

        if (failed.Count == 0)
            _logger.Information("Rollback complete for {PluginId}/{GameId}", receipt.PluginId, receipt.GameId);
        else
            _logger.Error("Rollback for {PluginId}/{GameId} could not restore {Count} file(s): {Files}",
                receipt.PluginId, receipt.GameId, failed.Count, string.Join(", ", failed));

        return new RollbackReport(failed);
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
        // files already applied. The executor journals every file into this list BEFORE writing it,
        // so even a failure halfway through one copyFolder leaves nothing unjournaled.
        string? backupFolder = null;
        var allChanges = new List<FileChange>();

        var stagedPackagePath = tempDir + "_pkg.zip";
        try
        {
            // The SHA256 gate is intrinsic to the engine, not a caller courtesy: whatever
            // produced this file (download, author-server stream, hand-picked local copy), it
            // must match the release the user chose. The package is hashed WHILE being copied
            // into a private staging file held with an exclusive handle, and extraction reads
            // from that SAME handle — no path is ever re-opened after verification.
            await using (var staged = await HashAndStagePackageAsync(packageZipPath, stagedPackagePath, release, ct))
            {
                await _zipExtractor.ExtractAsync(staged, tempDir, ct, sourceLabel: stagedPackagePath);
            }

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

            // The package must actually BE the version the user picked — a wrongly-uploaded old
            // ZIP must not install and get recorded under the new version's number.
            if (!string.Equals(manifest.ModVersion.Trim(), release.Version.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Package version mismatch: the package's manifest says '{manifest.ModVersion}' but the selected " +
                    $"release is '{release.Version}'. The wrong file may have been uploaded for this release.");

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

                var prompt = BuildPrompt(game, release, manifest);
                var ok = await scriptHost.ConfirmInstallScriptsAsync(prompt, ct);
                if (!ok)
                    throw new OperationCanceledException("User declined the lifecycle script warning.");
            }

            // The consent prompts and dependency work above can take minutes — re-check that the
            // game folder is still there right before the first mutation, so a game moved or
            // removed in the meantime doesn't get its dead path silently recreated.
            EnsureGamePresentForMutation(game, fullVerify: false);

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
                await RunHookOrThrow("Pre-install", manifest.PreInstall, tempDir, game,
                    backupFolder, allChanges, scriptHost, ct);
            }

            // Run install actions — every file change is journaled into allChanges before writing.
            foreach (var action in manifest.InstallActions)
            {
                ct.ThrowIfCancellationRequested();
                _actionExecutor.Execute(action, packageFilesDir, game.InstallPath, backupFolder, allChanges);
            }

            if (manifest.PostInstall is not null && (!isUpdate || manifest.PostInstall.RunOnUpdate))
            {
                await RunHookOrThrow("Post-install", manifest.PostInstall, tempDir, game,
                    backupFolder, allChanges, scriptHost, ct);
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
                throw new InvalidOperationException("Install verification failed; the changes are being rolled back.");
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
                    var report = await RollbackAsync(game, toRollBack, CancellationToken.None);
                    if (!report.AllRestored)
                    {
                        // Files this attempt wrote are still in the game folder. Persist the
                        // attempt's journal as a recovery receipt so those files stay OWNED —
                        // visible to collision checks and removable through a normal uninstall —
                        // instead of becoming untracked debris the manager has forgotten about.
                        _logger.Error(
                            "Rollback after the failed install could not restore {Count} file(s): {Files}. " +
                            "Saving a recovery receipt; the backup folder was kept at {Backup}.",
                            report.FailedPaths.Count, string.Join(", ", report.FailedPaths), backupFolder);
                        await _receiptStore.SaveAsync(toRollBack);
                    }
                    else
                    {
                        if (backupFolder != null)
                        {
                            // Everything this attempt wrote was verified undone — the backup folder
                            // created for the attempt has nothing left to protect.
                            TryDeleteBackupFolder(game, backupFolder);
                        }
                        TryDeleteCachedScripts(game.Game.GameId, release.PluginId);
                    }
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
            try { if (File.Exists(stagedPackagePath)) File.Delete(stagedPackagePath); } catch { /* best effort */ }
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
        string backupFolder,
        List<FileChange> journal,
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
            gameFolderRun = PrepareGameFolderRun(stagingScriptPath, script, game.InstallPath, hookLabel,
                backupFolder, journal);
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
    private sealed record GameFolderRunState(
        string GameFolderScriptPath, string? BackupOfPreexistingFile, FileStream? WriteGuard = null);

    /// <summary>
    /// Copies the staging-area script into the game folder so it runs with the game folder
    /// as its own location. If a file with that name already exists: for a script the author
    /// KEEPS in the game folder (<see cref="LifecycleScript.InstallToGameFolder"/>), the
    /// original goes into the REAL install transaction — backup folder + journal — so uninstall
    /// restores the true original (before this, the temp stash was discarded by the keep flag
    /// and uninstall "restored" the script over the user's file: audit finding 11, option 2).
    /// For a temporary copy, the original is stashed to a temp path and
    /// <see cref="CleanupGameFolderRun"/> restores it right after the run.
    /// </summary>
    private GameFolderRunState PrepareGameFolderRun(
        string stagingScriptPath, LifecycleScript script, string gameFolder, string hookLabel,
        string backupFolder, List<FileChange> journal)
    {
        var basename = Path.GetFileName(script.Executable);
        if (string.IsNullOrEmpty(basename))
            throw new InvalidOperationException(
                $"{hookLabel}: script executable path '{script.Executable}' has no filename — can't copy to game folder.");

        var targetPath = Path.Combine(gameFolder, basename);
        // The basename is a plain leaf, but a pre-existing FILE at the target could be a symlink
        // that redirects the copy — same reparse rule as every other game-folder write.
        PathSafety.EnsureNoReparseTraversal(gameFolder, targetPath, $"{hookLabel} script target");
        string? backupPath = null;
        if (File.Exists(targetPath))
        {
            if (script.InstallToGameFolder)
            {
                // Journal BEFORE the overwrite, same discipline as every action write: a failure
                // between the copy and the actions still rolls the original back.
                var backupRel = _backupManager.BackupFile(gameFolder, basename, backupFolder);
                journal.Add(new FileChange
                {
                    Type = ChangeType.Replaced,
                    RelativePath = basename,
                    BackupRelativePath = backupRel
                });
                _logger.Information(
                    "{Hook}: existing {Target} backed up into the install transaction (script stays in the game folder)",
                    hookLabel, targetPath);
            }
            else
            {
                backupPath = Path.Combine(Path.GetTempPath(),
                    $"amm-script-backup-{Guid.NewGuid():N}-{basename}");
                File.Copy(targetPath, backupPath, overwrite: true);
                _logger.Debug("{Hook}: stashed existing {Target} to {Backup}", hookLabel, targetPath, backupPath);
            }
        }
        else if (script.InstallToGameFolder)
        {
            // Nothing was there before, and this script is one the author keeps in the game
            // folder — so this copy CREATES a file the transaction now owns. Without the Added
            // entry, a fatal hook failure leaves the script behind, and the copyFile action the
            // manifest builder emits for it later sees a file that "already existed" and records
            // a Replaced whose backup is our own copy — so uninstall restores the script instead
            // of removing it.
            journal.Add(new FileChange
            {
                Type = ChangeType.Added,
                RelativePath = basename
            });
        }

        File.Copy(stagingScriptPath, targetPath, overwrite: true);
        _logger.Information("{Hook}: copied script to game folder for run-from-game-folder mode: {Target}",
            hookLabel, targetPath);
        return new GameFolderRunState(targetPath, backupPath);
    }

    /// <summary>
    /// <see cref="PrepareGameFolderRun"/>, but sourcing the script bytes from an already-open
    /// (verified, write-denied) stream instead of re-opening a path — used by the uninstall hook
    /// so the copy is guaranteed to be the exact bytes that were hashed and consented to.
    /// </summary>
    private async Task<GameFolderRunState> PrepareGameFolderRunFromStreamAsync(
        Stream verifiedSource, string basename, string gameFolder, string hookLabel, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(basename))
            throw new InvalidOperationException(
                $"{hookLabel}: cached script has no filename — can't copy to game folder.");

        var targetPath = Path.Combine(gameFolder, basename);
        PathSafety.EnsureNoReparseTraversal(gameFolder, targetPath, $"{hookLabel} script target");
        string? backupPath = null;
        if (File.Exists(targetPath))
        {
            backupPath = Path.Combine(Path.GetTempPath(),
                $"amm-script-backup-{Guid.NewGuid():N}-{basename}");
            File.Copy(targetPath, backupPath, overwrite: true);
            _logger.Debug("{Hook}: stashed existing {Target} to {Backup}", hookLabel, targetPath, backupPath);
        }

        // The copy's handle doubles as a write guard (FileShare.Read: the script runner can read
        // it, nothing can rewrite it before/while it runs). A failed or cancelled copy restores
        // the stash and removes the partial file instead of leaving both behind.
        var guard = new FileStream(targetPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            await verifiedSource.CopyToAsync(guard, ct);
            await guard.FlushAsync(ct);
        }
        catch
        {
            await guard.DisposeAsync();
            try { File.Delete(targetPath); } catch { /* best effort */ }
            if (backupPath != null)
            {
                try { File.Copy(backupPath, targetPath, overwrite: true); File.Delete(backupPath); }
                catch (Exception ex) { _logger.Warning(ex, "{Hook}: couldn't restore stashed file after failed copy", hookLabel); }
            }
            throw;
        }

        _logger.Information("{Hook}: copied verified script to game folder for run-from-game-folder mode: {Target}",
            hookLabel, targetPath);
        return new GameFolderRunState(targetPath, backupPath, guard);
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

        // Release the write guard before touching the file — deletes and restores below need it.
        try { state.WriteGuard?.Dispose(); } catch { /* best effort */ }

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

        // A legacy cache with NO recorded hash is refused outright: there's no way to prove it's
        // the script the user consented to (the audit closed the old run-unverified grace path).
        if (receipt.CachedPostUninstallSha256 is not { Length: > 0 } expectedSha)
        {
            _logger.Warning(
                "Cached post-uninstall script for {PluginId}/{GameId} predates hash recording — refusing to run it. Uninstall continues.",
                receipt.PluginId, receipt.GameId);
            return;
        }

        // Integrity: hold a write-denying handle on the cached script from hashing through the
        // consent prompt and the run itself. Hashing by path and re-opening later would let the
        // file be swapped while the user reads the consent dialog — the dialog describes the
        // ORIGINAL script, and only those exact bytes may run.
        FileStream scriptGuard;
        try
        {
            scriptGuard = new FileStream(receipt.CachedPostUninstallExecutable,
                FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException)
        {
            _logger.Warning("Cached post-uninstall script missing at {Path}; skipping",
                receipt.CachedPostUninstallExecutable);
            return;
        }
        catch (IOException ex)
        {
            _logger.Warning(ex, "Couldn't open the cached post-uninstall script exclusively; refusing to run it. Uninstall continues.");
            return;
        }
        await using var _ = scriptGuard;

        var actualSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(scriptGuard, ct));
        if (!string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
        {
            _logger.Error(
                "Cached post-uninstall script for {PluginId}/{GameId} does not match its recorded hash — refusing to run it. Uninstall continues.",
                receipt.PluginId, receipt.GameId);
            return;
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
        // The dialog's headline names the MOD; the plugin id is the author, not the mod
        // (finding 43). The definition's name is used only when the install was detected under
        // the SAME plugin the receipt belongs to — another plugin's definition of this game may
        // name a different mod, and a consent prompt must never carry the wrong name.
        var prompt = new LifecycleScriptPrompt
        {
            ModName = ModNameFor(game, receipt.PluginId),
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
        // cached executable under the receipt dir, not the staging dir, and the copy is made
        // from the VERIFIED held handle (a fresh by-path copy could pick up swapped bytes).
        // Cleanup always removes the in-game-folder copy: at uninstall the rollback is about to
        // run anyway, and InstallToGameFolder doesn't apply post-uninstall (the mod is going away).
        GameFolderRunState? gameFolderRun = null;
        var scriptAbsolute = receipt.CachedPostUninstallExecutable;
        if (receipt.PostUninstall.RunFromGameFolder)
        {
            scriptGuard.Position = 0;
            gameFolderRun = await PrepareGameFolderRunFromStreamAsync(
                scriptGuard, Path.GetFileName(receipt.CachedPostUninstallExecutable),
                game.InstallPath, "Post-uninstall", ct);
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

    private string GetScriptCacheDir(string gameId, string pluginId) =>
        Path.Combine(_receiptStore.GetReceiptDirectory(gameId, pluginId), "scripts");

    private (string Path, string Sha256) CachePostUninstallScript(
        LifecycleScript script, string stagingDir, string pluginId, string gameId)
    {
        var cacheDir = GetScriptCacheDir(gameId, pluginId);
        Directory.CreateDirectory(cacheDir);

        // Use the original filename so its extension survives — runner picks a runner by ext.
        var fileName = Path.GetFileName(script.Executable);
        var cachedPath = Path.Combine(cacheDir, fileName);

        // Read once, write and hash the SAME bytes — copying by path and re-reading for the hash
        // would let the recorded hash describe different bytes than the cached file.
        var sourcePath = Path.Combine(stagingDir, script.Executable);
        var scriptBytes = File.ReadAllBytes(sourcePath);
        File.WriteAllBytes(cachedPath, scriptBytes);
        var sha = Convert.ToHexStringLower(SHA256.HashData(scriptBytes));

        _logger.Information("Cached post-uninstall script to {Path}", cachedPath);
        return (cachedPath, sha);
    }

    private void TryDeleteCachedScripts(string gameId, string pluginId)
    {
        try
        {
            var cacheDir = GetScriptCacheDir(gameId, pluginId);
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

    /// <summary>
    /// Deletes a receipt's backup folder and prunes now-empty parents up to (and including)
    /// <c>modmanager_backups</c>. Only ever fires after every restore verified clean. The
    /// recursive delete is containment-checked to the manager-created backups subtree — the
    /// install root itself can be a junction and must never be recursively deleted.
    /// </summary>
    private void TryDeleteBackupFolder(GameInstall game, string backupFolder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(backupFolder) || !Directory.Exists(backupFolder))
                return;

            var backupsRoot = Path.Combine(game.InstallPath, "modmanager_backups");
            if (!PathSafety.IsContained(backupsRoot, backupFolder) ||
                PathSafety.IsContained(backupFolder, backupsRoot))
            {
                _logger.Warning("Backup folder {Folder} is not strictly under {Root}; leaving it alone",
                    backupFolder, backupsRoot);
                return;
            }

            // Physical containment before the recursive delete: if modmanager_backups (or any
            // component) was swapped for a junction, the text-contained path resolves elsewhere
            // and the delete would eat a folder outside the game. A guard throw lands in the
            // catch below — the folder is kept, which is the safe outcome.
            PathSafety.EnsureNoReparseTraversal(game.InstallPath, backupFolder, "backup folder to delete");

            Directory.Delete(backupFolder, recursive: true);

            // Prune empty parents (plugin/game levels and modmanager_backups itself) so an
            // uninstalled game folder doesn't keep an empty leftover tree.
            var current = Path.GetDirectoryName(backupFolder);
            while (current != null &&
                   PathSafety.IsContained(backupsRoot, current) &&
                   Directory.Exists(current) &&
                   !Directory.EnumerateFileSystemEntries(current).Any())
            {
                Directory.Delete(current, recursive: false);
                current = Path.GetDirectoryName(current);
            }

            _logger.Information("Removed backup folder after verified uninstall: {Folder}", backupFolder);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't remove backup folder {Folder}", backupFolder);
        }
    }

    /// <summary>
    /// Copies the package into <paramref name="stagedPath"/> while hashing the SAME bytes being
    /// copied, and returns the still-open exclusive handle (FileShare.None) positioned at zero.
    /// The caller extracts from that handle, so the verified bytes can never be swapped between
    /// verification and extraction. Throws on a hash mismatch after deleting the staged copy.
    /// </summary>
    private static async Task<FileStream> HashAndStagePackageAsync(
        string packageZipPath, string stagedPath, ModRelease release, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var staged = new FileStream(
            stagedPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true);
        try
        {
            await using (var source = new FileStream(
                packageZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await staged.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            await staged.FlushAsync(ct);

            var actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!string.Equals(actual, release.Sha256?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Package SHA256 mismatch for {release.PluginId}/{release.GameId} v{release.Version}: expected " +
                    $"{release.Sha256}, got {actual}. The file is not the published release — install aborted.");
            }

            staged.Position = 0;
            return staged;
        }
        catch
        {
            await staged.DisposeAsync();
            try { File.Delete(stagedPath); } catch { /* best effort */ }
            throw;
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
    /// A stable hash over the manifest's lifecycle scripts, or null if none are declared. Recorded
    /// in the receipt so an update can tell whether the user is looking at the same scripts they
    /// already agreed to, or a new or changed one that needs a fresh notice.
    /// <para>
    /// It covers everything the consent dialog puts in front of the user — not just the script's
    /// bytes and behaviour flags, but the What / Why / Modifies text as well (audit finding 42).
    /// Those three lines ARE the consent: an update that left the script byte-identical while
    /// rewriting its description could otherwise change what the user was told it does without
    /// ever asking them again.
    /// </para>
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
              .Append("|toGame=").Append(s.InstallToGameFolder);

            // Length-prefixed, because these three are free text the author controls. Appending
            // them with plain delimiters would let a description carrying "|why=" shuffle content
            // between fields and land on the same bytes — which is precisely the re-consent this
            // fingerprint exists to force. A length can't be forged by the content it measures.
            AppendCounted(sb, "what", s.What);
            AppendCounted(sb, "why", s.Why);
            AppendCounted(sb, "modifies", s.Modifies);
            sb.Append('\n');
        }

        static void AppendCounted(StringBuilder sb, string field, string? value)
        {
            var text = value ?? "";
            sb.Append('|').Append(field).Append(':').Append(text.Length).Append(':').Append(text);
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
            // Receipt paths are tamper-checked, but they still get the same textual + physical
            // (reparse) confinement as every other game-folder path (finding 15).
            var src = PathSafety.CombineContained(gameDir, change.RelativePath);
            PathSafety.EnsureNoReparseTraversal(gameDir, src, "update snapshot source");
            if (!File.Exists(src)) continue;
            var dest = Path.Combine(snapshotDir, change.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
        }
    }

    /// <summary>
    /// Restores files captured by <see cref="SnapshotInstalledFiles"/> back into the game folder.
    /// A guard failure here aborts the restore loudly — the caller preserves the snapshot and
    /// reports it; writing through a link that appeared mid-update would be worse.
    /// </summary>
    private static void RestoreSnapshot(string snapshotDir, string gameDir)
    {
        if (!Directory.Exists(snapshotDir)) return;
        foreach (var file in Directory.EnumerateFiles(snapshotDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(snapshotDir, file);
            var dest = PathSafety.CombineContained(gameDir, rel);
            PathSafety.EnsureNoReparseTraversal(gameDir, dest, "update restore target");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary>Copies a directory tree (used to snapshot/restore the cached-scripts folder).</summary>
    private static void SnapshotDirectory(string sourceDir, string destDir)
    {
        if (!Directory.Exists(sourceDir)) return;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void RestoreDirectory(string snapshotDir, string destDir)
    {
        if (!Directory.Exists(snapshotDir)) return;
        foreach (var file in Directory.EnumerateFiles(snapshotDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(snapshotDir, file);
            var dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static LifecycleScriptPrompt BuildPrompt(GameInstall game, ModRelease release, Manifest manifest)
    {
        var hooks = new List<LifecycleScriptHookInfo>();
        if (manifest.PreInstall is not null)
            hooks.Add(new LifecycleScriptHookInfo { HookLabel = "Pre-install", Script = manifest.PreInstall });
        if (manifest.PostInstall is not null)
            hooks.Add(new LifecycleScriptHookInfo { HookLabel = "Post-install", Script = manifest.PostInstall });
        if (manifest.PostUninstall is not null)
            hooks.Add(new LifecycleScriptHookInfo { HookLabel = "Post-uninstall", Script = manifest.PostUninstall });

        // The dialog's headline names the MOD; the plugin id is the author, not the mod (finding 43).
        return new LifecycleScriptPrompt
        {
            ModName = ModNameFor(game, release.PluginId),
            Version = release.Version,
            Author = release.PluginId,
            Hooks = hooks
        };
    }

    /// <summary>
    /// The mod's human name for consent prompts: the game definition's ModName, but only when
    /// the install was detected under the same plugin the operation belongs to — several plugins
    /// can define one game, and a prompt naming ANOTHER plugin's mod would be misleading exactly
    /// where trust is decided. Falls back to the plugin id (finding 43).
    /// </summary>
    private static string ModNameFor(GameInstall game, string operationPluginId) =>
        string.Equals(game.PluginId, operationPluginId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(game.Game.ModName)
            ? game.Game.ModName!
            : operationPluginId;

    private static string TruncateForMessage(string output, int maxChars = 2000)
    {
        if (string.IsNullOrEmpty(output)) return "(no output)";
        return output.Length <= maxChars ? output : output[..maxChars] + "\n... (truncated)";
    }

    /// <summary>
    /// Q8 redesign: dep installation is a step in the install flow. For each missing required
    /// dependency, in manifest order (F14=A):
    ///   - If the dep has an AutoInstall block, prompt the user once with the combined deps
    ///     consent dialog (F16=C step 1) then run all auto-installs in sequence.
    ///   - If the dep has only a download URL (no AutoInstall), open it in a browser and
    ///     pause for the user to install manually + click Continue (F8=C).
    /// Dependencies that are already present get this plugin added to their refcount too — a mod
    /// must be counted against every loader it needs, not only the ones it happened to install
    /// first (audit finding 2: uninstalling mod A used to remove MelonLoader from under mod B).
    /// After every dep, recheck via <see cref="IDependencyChecker"/>; a required dep still
    /// missing ABORTS the install (Amethyst reversed the old F5=B warn-and-continue in the
    /// round-2 audit, finding 26) so a mod is never recorded installed without its loader.
    /// Returns every refcount bump / fresh install this run performed so a downstream failure
    /// can release them again.
    /// </summary>
    private async Task<List<DepAcquisition>> ResolveDependenciesAsync(
        GameInstall game, string requestingPluginId, IDependencyHost? host, CancellationToken ct)
    {
        var acquisitions = new List<DepAcquisition>();

        if (game.Game.Dependencies.Count == 0)
        {
            _logger.Information("Dep resolution for {GameId}/{PluginId}: game definition declares no dependencies — skipping",
                game.Game.GameId, requestingPluginId);
            return acquisitions;
        }

        // The resolution step is atomic over its own acquisitions: if anything below throws
        // (declined consent, a failed dep install, the still-missing abort), everything THIS
        // call already acquired is released before the exception continues — the caller's
        // release only has to cover failures after resolution returned successfully.
        try
        {
            await ResolveDependenciesCoreAsync(game, requestingPluginId, host, acquisitions, ct);
            return acquisitions;
        }
        catch
        {
            await _depAutoInstaller.ReleaseAcquisitionsAsync(game, requestingPluginId, acquisitions);
            throw;
        }
    }

    private async Task ResolveDependenciesCoreAsync(
        GameInstall game, string requestingPluginId, IDependencyHost? host,
        List<DepAcquisition> acquisitions, CancellationToken ct)
    {

        // Game-installer deps (IsGameInstaller) are handled by the manager's pre-install step
        // before detection — by the time we get here the game is already installed. Drop them so
        // they never appear in the dependency consent dialog or get re-run here.
        var statuses = (await _dependencyChecker.CheckAsync(game, ct))
            .Where(s => !s.Dependency.IsGameInstaller)
            .ToList();

        // Refcount reconciliation (finding 2): a dependency that is already installed — whether
        // by another mod through the manager or by hand with a manager receipt — must still
        // count this plugin as a dependent.
        foreach (var s in statuses)
        {
            if (s.Status != DependencyStatusKind.Installed) continue;
            if (s.Dependency.Fix?.AutoInstall is null) continue;

            var receipt = await _depReceiptSafeLoadAsync(game.Game.GameId, s.Dependency.Id);
            if (receipt == null || receipt.DependentPluginIds.Contains(requestingPluginId)) continue;

            receipt.DependentPluginIds.Add(requestingPluginId);
            await _depAutoInstaller.SaveReceiptAsync(receipt);
            acquisitions.Add(new DepAcquisition(s.Dependency.Id, InstalledFresh: false));
            _logger.Information("Dep {DepId} already installed; added {Plugin} to its refcount",
                s.Dependency.Id, requestingPluginId);
        }

        var blockers = statuses
            .Where(s => s.Dependency.Required && s.Status != DependencyStatusKind.Installed)
            .ToList();
        if (blockers.Count == 0)
        {
            _logger.Information(
                "Dep resolution for {GameId}/{PluginId}: all {Count} declared dependencies report Installed.",
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

            var prompt = BuildDependencyPrompt(game, requestingPluginId, autoBlockers);
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
                // The installer reports exactly what it changed — nothing to release when the
                // plugin was already refcounted before this run.
                if (result.Acquisition != null)
                    acquisitions.Add(result.Acquisition);
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

            // Recheck this single dep. Finding 26 (F5=B reversed): a required dep still missing
            // after its install aborts the flow — the caller releases this run's acquisitions.
            var recheck = await _dependencyChecker.CheckAsync(game, ct);
            var still = recheck.FirstOrDefault(s => s.Dependency.Id == dep.Id);
            if (still is { Status: not DependencyStatusKind.Installed })
            {
                throw new InvalidOperationException(
                    $"Dependency '{dep.Id}' still reports {still.Status} after installing it — aborting so the mod " +
                    $"isn't installed without its loader. Check rule: {DescribeCheck(dep)}. If the dependency's files " +
                    "are actually in place, the check rule in the plugin index is wrong and needs fixing.");
            }
        }
    }

    // Small indirection so the reconcile loop reads receipts through the auto-installer's store
    // without the engine taking its own IDependencyReceiptStore dependency.
    private Task<DependencyReceipt?> _depReceiptSafeLoadAsync(string gameId, string depId) =>
        _depAutoInstaller.LoadReceiptAsync(gameId, depId);

    private static string DescribeCheck(Dependency dep)
    {
        if (dep.Check == null) return "(no check rule)";
        if (!string.IsNullOrEmpty(dep.Check.FilePath)) return $"file '{dep.Check.FilePath}'";
        if (!string.IsNullOrEmpty(dep.Check.RegistryKey))
            return $"registry key '{dep.Check.RegistryKey}'" +
                   (string.IsNullOrEmpty(dep.Check.RegistryValue) ? "" : $" value '{dep.Check.RegistryValue}'");
        return "(empty check rule)";
    }

    private static DependencyInstallPrompt BuildDependencyPrompt(
        GameInstall game, string requestingPluginId, List<DependencyStatus> blockers)
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
        // The headline names what needs the components: the MOD when the author named it (and
        // the install was detected under the same plugin — see ModNameFor), else the game
        // (finding 43).
        var modName = string.Equals(game.PluginId, requestingPluginId, StringComparison.Ordinal) &&
                      !string.IsNullOrWhiteSpace(game.Game.ModName)
            ? game.Game.ModName!
            : game.Game.DisplayName;
        return new DependencyInstallPrompt
        {
            ModName = modName,
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
