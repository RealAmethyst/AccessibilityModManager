using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Installer;

/// <summary>
/// Wave-2 audit coverage: transactional dependency refcounts (finding 2), rollback failure
/// reporting (4), update script-cache restore (5), missing-dep abort (26), the per-game mutation
/// lock (27), path re-verification (29), backup cleanup (30), and the package version cross-check
/// (31).
/// </summary>
public class EngineIntegrityTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _gameDir;
    private readonly InMemoryReceiptStore _receiptStore = new();
    private readonly InMemoryDepStore _depStore;

    public EngineIntegrityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_integrity_" + Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(_gameDir);
        _depStore = new InMemoryDepStore(Path.Combine(_tempRoot, "depbackup"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------- finding 2: refcounts

    [Fact]
    public async Task Install_DepAlreadyInstalledByOtherMod_AddsThisPluginToItsRefcount()
    {
        // MelonLoader-style dep is present (check file on disk) and owned by plug-a's receipt.
        File.WriteAllText(Path.Combine(_gameDir, "version.dll"), "loader");
        await _depStore.SaveAsync(MakeDepReceipt("melonloader", "plug-a"));

        var engine = MakeEngine(new HttpClient(new RefuseHandler()));
        var zip = CreateMod("plug-b", "game-x", "1.0.0");

        await engine.InstallAsync(MakeGame("plug-b", withDep: true), MakeRelease("plug-b", "1.0.0"), zip);

        var receipt = await _depStore.LoadAsync("game-x", "melonloader");
        Assert.NotNull(receipt);
        Assert.Contains("plug-a", receipt!.DependentPluginIds);
        Assert.Contains("plug-b", receipt.DependentPluginIds);
    }

    [Fact]
    public async Task Install_FailsAfterDepRefcountBump_ReleasesTheBump()
    {
        File.WriteAllText(Path.Combine(_gameDir, "version.dll"), "loader");
        await _depStore.SaveAsync(MakeDepReceipt("melonloader", "plug-a"));

        var engine = MakeEngine(new HttpClient(new RefuseHandler()));
        // Manifest references a missing source file, so the install fails after dep resolution.
        var zip = CreateMod("plug-b", "game-x", "1.0.0",
            actions: new[] { (object)new { type = "copyFile", source = "missing.dll", target = "mods/missing.dll" } });

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            engine.InstallAsync(MakeGame("plug-b", withDep: true), MakeRelease("plug-b", "1.0.0"), zip));

        var receipt = await _depStore.LoadAsync("game-x", "melonloader");
        Assert.NotNull(receipt);
        Assert.Contains("plug-a", receipt!.DependentPluginIds);
        Assert.DoesNotContain("plug-b", receipt.DependentPluginIds);
    }

    [Fact]
    public async Task Install_FreshDepInstalled_ThenModInstallFails_RemovesTheDepAgain()
    {
        // Dep is missing; its ZIP provides the check file, so the dep install succeeds — then the
        // mod install fails and the freshly-installed dep must be rolled back and de-receipted.
        var depZip = MakeZip(("version.dll", "loader-bits"));
        var engine = MakeEngine(new HttpClient(new ServeFileHandler(depZip)));

        var zip = CreateMod("plug-b", "game-x", "1.0.0",
            actions: new[] { (object)new { type = "copyFile", source = "missing.dll", target = "mods/missing.dll" } });

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            engine.InstallAsync(MakeGame("plug-b", withDep: true, depSha: Sha(depZip)),
                MakeRelease("plug-b", "1.0.0"), zip, dependencyHost: new AcceptingDepHost()));

        Assert.Null(await _depStore.LoadAsync("game-x", "melonloader"));
        Assert.False(File.Exists(Path.Combine(_gameDir, "version.dll")),
            "freshly-installed dep files should be rolled back when the mod install fails");
    }

    // ---------------------------------------------------------------- finding 26: abort on still-missing

    [Fact]
    public async Task Install_RequiredDepStillMissingAfterItsInstall_Aborts()
    {
        // The dep ZIP does NOT contain the check file, so the post-install recheck still says
        // missing — the old F5=B behavior warned and continued; now it must abort.
        var depZip = MakeZip(("other.dll", "not-the-loader"));
        var engine = MakeEngine(new HttpClient(new ServeFileHandler(depZip)));
        var zip = CreateMod("plug-b", "game-x", "1.0.0");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.InstallAsync(MakeGame("plug-b", withDep: true, depSha: Sha(depZip)),
                MakeRelease("plug-b", "1.0.0"), zip, dependencyHost: new AcceptingDepHost()));

        Assert.Contains("still reports", ex.Message);
        // The failed acquisition was released: receipt gone, extracted file rolled back.
        Assert.Null(await _depStore.LoadAsync("game-x", "melonloader"));
        Assert.False(File.Exists(Path.Combine(_gameDir, "other.dll")));
        Assert.Null(await _receiptStore.LoadAsync("game-x", "plug-b"));
    }

    // ---------------------------------------------------------------- finding 31: version cross-check

    [Fact]
    public async Task Install_PackageVersionMismatch_Throws()
    {
        var engine = MakeEngine(new HttpClient(new RefuseHandler()));
        var zip = CreateMod("plug-b", "game-x", "9.9.9"); // manifest says 9.9.9

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.InstallAsync(MakeGame("plug-b"), MakeRelease("plug-b", "1.0.0"), zip));

        Assert.Contains("version mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_gameDir, "mods", "mod.dll")));
    }

    // ---------------------------------------------------------------- finding 29: re-verify path

    [Fact]
    public async Task Install_GameFolderGone_ThrowsClearMessage()
    {
        var engine = MakeEngine(new HttpClient(new RefuseHandler()));
        var zip = CreateMod("plug-b", "game-x", "1.0.0");
        var ghost = new GameInstall
        {
            Game = new GameDefinition { GameId = "game-x", DisplayName = "Game X" },
            PluginId = "plug-b",
            InstallPath = Path.Combine(_tempRoot, "vanished"),
            IsValid = true
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.InstallAsync(ghost, MakeRelease("plug-b", "1.0.0"), zip));
        Assert.Contains("no longer exists", ex.Message);
    }

    // ---------------------------------------------------------------- finding 27: per-game lock

    [Fact]
    public async Task Install_SecondMutationOnSameGameWhileFirstRuns_FailsFast()
    {
        var engine = MakeEngine(new HttpClient(new RefuseHandler()));
        var host = new BlockingScriptHost();
        var zip = CreateModWithScript("plug-b", "game-x", "1.0.0");

        var installTask = engine.InstallAsync(MakeGame("plug-b"), MakeRelease("plug-b", "1.0.0"), zip, host);
        await host.ConsentRequested.Task; // the install now holds the game lock

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.UninstallAsync(MakeGame("plug-b"), "plug-b"));
        Assert.Contains("already running", ex.Message);

        host.ConsentResult.SetResult(false); // decline → install cancels and releases the lock
        await Assert.ThrowsAsync<OperationCanceledException>(() => installTask);

        // Lock released — the same mutation now proceeds (no receipt, so it's a no-op uninstall).
        await engine.UninstallAsync(MakeGame("plug-b"), "plug-b");
    }

    // ---------------------------------------------------------------- finding 4: rollback failures abort

    [Fact]
    public async Task Uninstall_BackupMissing_ThrowsAndKeepsReceipt()
    {
        var engine = MakeEngine(new HttpClient(new RefuseHandler()));
        Directory.CreateDirectory(Path.Combine(_gameDir, "config"));
        File.WriteAllText(Path.Combine(_gameDir, "config", "settings.cfg"), "ORIGINAL");

        var zip = CreateMod("plug-b", "game-x", "1.0.0",
            files: new[] { ("settings.cfg", "MODDED") },
            actions: new[] { (object)new { type = "replaceFile", source = "settings.cfg", target = "config/settings.cfg" } });
        var receipt = await engine.InstallAsync(MakeGame("plug-b"), MakeRelease("plug-b", "1.0.0"), zip);

        // Sabotage: the backup of the original disappears (disk cleanup, user deletion...).
        Directory.Delete(receipt.BackupFolder, recursive: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.UninstallAsync(MakeGame("plug-b"), "plug-b"));
        Assert.Contains("could not restore", ex.Message);

        // Fail closed: receipt kept, the modded file untouched — evidence survives for a retry.
        Assert.NotNull(await _receiptStore.LoadAsync("game-x", "plug-b"));
        Assert.Equal("MODDED", File.ReadAllText(Path.Combine(_gameDir, "config", "settings.cfg")));
    }

    // ---------------------------------------------------------------- finding 30: backup cleanup

    [Fact]
    public async Task Uninstall_Clean_RemovesTheBackupTree()
    {
        var engine = MakeEngine(new HttpClient(new RefuseHandler()));
        Directory.CreateDirectory(Path.Combine(_gameDir, "config"));
        File.WriteAllText(Path.Combine(_gameDir, "config", "settings.cfg"), "ORIGINAL");

        var zip = CreateMod("plug-b", "game-x", "1.0.0",
            files: new[] { ("settings.cfg", "MODDED") },
            actions: new[] { (object)new { type = "replaceFile", source = "settings.cfg", target = "config/settings.cfg" } });
        await engine.InstallAsync(MakeGame("plug-b"), MakeRelease("plug-b", "1.0.0"), zip);
        Assert.True(Directory.Exists(Path.Combine(_gameDir, "modmanager_backups")));

        await engine.UninstallAsync(MakeGame("plug-b"), "plug-b");

        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(_gameDir, "config", "settings.cfg")));
        Assert.False(Directory.Exists(Path.Combine(_gameDir, "modmanager_backups")),
            "a fully-verified uninstall should clean up the backup tree");
    }

    // ---------------------------------------------------------------- finding 5: script cache restore

    [Fact]
    public async Task Update_Fails_RestoresTheCachedUninstallScript()
    {
        var engine = MakeEngine(new HttpClient(new RefuseHandler()));
        var host = new AcceptingScriptHost();

        var zipV1 = CreateModWithPostUninstall("plug-b", "game-x", "1.0.0");
        await engine.InstallAsync(MakeGame("plug-b"), MakeRelease("plug-b", "1.0.0"), zipV1, host);

        var cachedScript = Path.Combine(
            _receiptStore.GetReceiptDirectory("game-x", "plug-b"), "scripts", "cleanup.cmd");
        Assert.True(File.Exists(cachedScript));

        // v2 fails verification → the old version (files, receipt, AND its script cache) returns.
        var zipV2 = CreateModWithPostUninstall("plug-b", "game-x", "1.1.0",
            verify: new[] { (object)new { type = "fileExists", path = "never-there.dll" } });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.UpdateAsync(MakeGame("plug-b"), MakeRelease("plug-b", "1.1.0"), zipV2, host));

        Assert.True(File.Exists(cachedScript),
            "the failed update must restore the old version's cached post-uninstall script");
        var restored = await _receiptStore.LoadAsync("game-x", "plug-b");
        Assert.Equal("1.0.0", restored!.InstalledVersion);
    }

    // ---------------------------------------------------------------- finding 24/4: dep release keeps evidence

    [Fact]
    public async Task ReleaseDependencies_RestoreFails_KeepsReceiptRetryable()
    {
        var logger = TestLogger.Create();
        var installer = new DependencyAutoInstaller(new HttpClient(new RefuseHandler()), _depStore, logger);

        // Refcount 1, one Replaced change whose backup is missing → release must fail and keep
        // the receipt UNCHANGED (plugin still listed), so a retry re-enters the removal path
        // instead of skipping a zero-refcount orphan forever.
        var receipt = MakeDepReceipt("melonloader", "plug-b");
        receipt.Changes.Clear(); // this dep REPLACED the file — the default Added entry would contradict it
        receipt.Changes.Add(new FileChange
        {
            Type = ChangeType.Replaced,
            RelativePath = "version.dll",
            BackupRelativePath = "version.dll"
        });
        await _depStore.SaveAsync(receipt);

        var failures = await installer.ReleaseDependenciesForPluginAsync(
            MakeGame("plug-b"), "plug-b", CancellationToken.None);

        Assert.NotEmpty(failures);
        var kept = await _depStore.LoadAsync("game-x", "melonloader");
        Assert.NotNull(kept);
        Assert.Contains("plug-b", kept!.DependentPluginIds);

        // Repair the missing backup and retry — the release must now complete and clean up.
        var backupFile = Path.Combine(kept.BackupFolder, "version.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        File.WriteAllText(backupFile, "ORIGINAL");

        var retryFailures = await installer.ReleaseDependenciesForPluginAsync(
            MakeGame("plug-b"), "plug-b", CancellationToken.None);

        Assert.Empty(retryFailures);
        Assert.Null(await _depStore.LoadAsync("game-x", "melonloader"));
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(_gameDir, "version.dll")));
    }

    [Fact]
    public async Task Install_DuplicateTargetsInOneManifest_UninstallRestoresTrueOriginal()
    {
        // Two actions write the same file. The second's backup-on-conflict sees the FIRST
        // action's output — first-backup-wins must keep the user's original as the baseline.
        var engine = MakeEngine(new HttpClient(new RefuseHandler()));
        File.WriteAllText(Path.Combine(_gameDir, "x.dll"), "ORIGINAL");

        var zip = CreateMod("plug-b", "game-x", "1.0.0",
            files: new[] { ("a.dll", "AAA"), ("b.dll", "BBB") },
            actions: new object[]
            {
                new { type = "copyFile", source = "a.dll", target = "x.dll" },
                new { type = "copyFile", source = "b.dll", target = "x.dll" }
            });
        await engine.InstallAsync(MakeGame("plug-b"), MakeRelease("plug-b", "1.0.0"), zip);
        Assert.Equal("BBB", File.ReadAllText(Path.Combine(_gameDir, "x.dll")));

        await engine.UninstallAsync(MakeGame("plug-b"), "plug-b");
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(_gameDir, "x.dll")));
    }

    [Fact]
    public async Task DepReinstall_PreservesTheOriginalRollbackBaseline()
    {
        // v1 replaces the user's original; v2 (different SHA) forces a reinstall. The reinstall
        // must NOT capture v1's files as "the backup" — after every mod lets go, the file on disk
        // must be the true original, not the old dependency version.
        var logger = TestLogger.Create();
        File.WriteAllText(Path.Combine(_gameDir, "version.dll"), "ORIGINAL");

        var v1Zip = MakeZip(("version.dll", "V1"));
        var installerV1 = new DependencyAutoInstaller(new HttpClient(new ServeFileHandler(v1Zip)), _depStore, logger);
        var r1 = await installerV1.InstallAsync(MakeDep(Sha(v1Zip)), MakeGame("plug-b", withDep: true, depSha: Sha(v1Zip)),
            "plug-b", host: null, ct: CancellationToken.None);
        Assert.True(r1.Succeeded, r1.ErrorMessage);
        Assert.Equal("V1", File.ReadAllText(Path.Combine(_gameDir, "version.dll")));

        var v2Zip = MakeZip(("version.dll", "V2-different"));
        var installerV2 = new DependencyAutoInstaller(new HttpClient(new ServeFileHandler(v2Zip)), _depStore, logger);
        var r2 = await installerV2.InstallAsync(MakeDep(Sha(v2Zip)), MakeGame("plug-b", withDep: true, depSha: Sha(v2Zip)),
            "plug-b", host: null, ct: CancellationToken.None);
        Assert.True(r2.Succeeded, r2.ErrorMessage);
        Assert.Equal("V2-different", File.ReadAllText(Path.Combine(_gameDir, "version.dll")));

        var failures = await installerV2.ReleaseDependenciesForPluginAsync(
            MakeGame("plug-b"), "plug-b", CancellationToken.None);

        Assert.Empty(failures);
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(_gameDir, "version.dll")));
    }

    [Fact]
    public async Task DepReinstall_DownloadHashFails_LeavesOldDepIntactAndRetryable()
    {
        // The old copy must be removed only AFTER the replacement is downloaded and verified —
        // a bad download must leave the installed v1 (receipt, files, backups) fully intact.
        var logger = TestLogger.Create();
        File.WriteAllText(Path.Combine(_gameDir, "version.dll"), "ORIGINAL");

        var v1Zip = MakeZip(("version.dll", "V1"));
        var installerV1 = new DependencyAutoInstaller(new HttpClient(new ServeFileHandler(v1Zip)), _depStore, logger);
        var r1 = await installerV1.InstallAsync(MakeDep(Sha(v1Zip)), MakeGame("plug-b", withDep: true, depSha: Sha(v1Zip)),
            "plug-b", host: null, ct: CancellationToken.None);
        Assert.True(r1.Succeeded, r1.ErrorMessage);

        // v2 attempt: served bytes won't match the declared hash.
        var v2Zip = MakeZip(("version.dll", "V2"));
        var badSha = new string('0', 64);
        var installerV2 = new DependencyAutoInstaller(new HttpClient(new ServeFileHandler(v2Zip)), _depStore, logger);
        var r2 = await installerV2.InstallAsync(MakeDep(badSha), MakeGame("plug-b", withDep: true, depSha: badSha),
            "plug-b", host: null, ct: CancellationToken.None);

        Assert.False(r2.Succeeded);
        Assert.Equal("V1", File.ReadAllText(Path.Combine(_gameDir, "version.dll")));
        var receipt = await _depStore.LoadAsync("game-x", "melonloader");
        Assert.NotNull(receipt);
        Assert.Equal(Sha(v1Zip), receipt!.Sha256);

        // And v1 still releases back to the true original.
        var failures = await installerV1.ReleaseDependenciesForPluginAsync(
            MakeGame("plug-b"), "plug-b", CancellationToken.None);
        Assert.Empty(failures);
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(_gameDir, "version.dll")));
    }

    [Fact]
    public async Task Uninstall_RetryAfterFailure_DoesNotRerunPostUninstallScript()
    {
        var engine = MakeEngine(new HttpClient(new RefuseHandler()));
        Directory.CreateDirectory(Path.Combine(_gameDir, "config"));
        File.WriteAllText(Path.Combine(_gameDir, "config", "settings.cfg"), "ORIGINAL");
        var host = new AcceptingScriptHost();

        var zip = CreateModWithPostUninstall("plug-b", "game-x", "1.0.0",
            files: new[] { ("settings.cfg", "MODDED") },
            actions: new object[] { new { type = "replaceFile", source = "settings.cfg", target = "config/settings.cfg" } });
        var receipt = await engine.InstallAsync(MakeGame("plug-b"), MakeRelease("plug-b", "1.0.0"), zip, host);

        // Sabotage rollback so the uninstall fails AFTER the cleanup script ran.
        Directory.Delete(receipt.BackupFolder, recursive: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.UninstallAsync(MakeGame("plug-b"), "plug-b", host));
        Assert.Equal(1, host.UninstallConfirmCount);
        Assert.Equal(1, host.StartingHooks.Count(h => h == "Post-uninstall"));

        // Retry (still failing): the author's cleanup script must NOT run — or re-prompt — again.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.UninstallAsync(MakeGame("plug-b"), "plug-b", host));
        Assert.Equal(1, host.UninstallConfirmCount);
        Assert.Equal(1, host.StartingHooks.Count(h => h == "Post-uninstall"));
    }

    [Fact]
    public async Task Install_CorruptDependencyReceipt_FailsClosedBeforeAnyMutation()
    {
        _depStore.Unreadable = true;
        var engine = MakeEngine(new HttpClient(new RefuseHandler()));
        var zip = CreateMod("plug-b", "game-x", "1.0.0");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.InstallAsync(MakeGame("plug-b"), MakeRelease("plug-b", "1.0.0"), zip));

        Assert.Contains("could not be read", ex.Message);
        Assert.False(File.Exists(Path.Combine(_gameDir, "mods", "mod.dll")));
    }

    // ---------------------------------------------------------------- harness

    private InstallerEngine MakeEngine(HttpClient depHttpClient)
    {
        var logger = TestLogger.Create();
        var backupManager = new BackupManager(logger);
        return new InstallerEngine(
            backupManager,
            new InstallActionExecutor(backupManager, logger),
            new InstallVerifier(logger),
            new ManifestParser(logger),
            new SafeZipExtractor(logger),
            _receiptStore,
            new DependencyChecker(logger),
            new LifecycleScriptRunner(logger),
            new DependencyAutoInstaller(depHttpClient, _depStore, logger),
            new GameVerifier(logger),
            logger);
    }

    private GameInstall MakeGame(string pluginId, bool withDep = false, string depSha = "deadbeef")
    {
        var deps = new List<Dependency>();
        if (withDep)
        {
            deps.Add(new Dependency
            {
                Id = "melonloader",
                Type = "framework",
                Required = true,
                Check = new DependencyCheck { FilePath = "version.dll" },
                Fix = new DependencyFix
                {
                    DownloadUrl = "https://example.invalid/loader.zip",
                    AutoInstall = new ExtractZipAutoInstall { Sha256 = depSha }
                }
            });
        }
        return new GameInstall
        {
            Game = new GameDefinition { GameId = "game-x", DisplayName = "Game X", Dependencies = deps },
            PluginId = pluginId,
            InstallPath = _gameDir,
            IsValid = true
        };
    }

    private static ModRelease MakeRelease(string pluginId, string version) => new()
    {
        GameId = "game-x",
        PluginId = pluginId,
        Version = version,
        Channel = "stable",
        PackageUrl = new Uri("https://example.com/pkg.zip"),
        Sha256 = "00"
    };

    private static Dependency MakeDep(string sha) => new()
    {
        Id = "melonloader",
        Type = "framework",
        Required = true,
        Check = new DependencyCheck { FilePath = "version.dll" },
        Fix = new DependencyFix
        {
            DownloadUrl = "https://example.invalid/loader.zip",
            AutoInstall = new ExtractZipAutoInstall { Sha256 = sha }
        }
    };

    private DependencyReceipt MakeDepReceipt(string depId, params string[] dependents) => new()
    {
        GameId = "game-x",
        DependencyId = depId,
        Kind = "extractZip",
        InstalledAt = DateTime.UtcNow,
        Sha256 = "deadbeef",
        Changes = new List<FileChange>
        {
            new() { Type = ChangeType.Added, RelativePath = "version.dll" }
        },
        BackupFolder = _depStore.GetBackupDirectory("game-x", depId),
        DependentPluginIds = dependents.ToList()
    };

    private string CreateMod(
        string pluginId, string gameId, string modVersion,
        (string relPath, string content)[]? files = null,
        object[]? actions = null,
        object[]? verify = null)
    {
        files ??= new[] { ("mod.dll", "modbytes") };
        actions ??= new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mods/mod.dll" } };

        var zipPath = Path.Combine(_tempRoot, $"pkg_{Guid.NewGuid():N}.zip");
        using var stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var manifest = new
        {
            gameId,
            pluginId,
            modVersion,
            installActions = actions,
            verify = verify ?? Array.Empty<object>()
        };
        AddEntry(archive, "manifest.json", JsonSerializer.Serialize(manifest));
        foreach (var (rel, content) in files)
            AddEntry(archive, $"files/{rel}", content);
        return zipPath;
    }

    private string CreateModWithScript(string pluginId, string gameId, string modVersion)
    {
        var zipPath = Path.Combine(_tempRoot, $"pkg_{Guid.NewGuid():N}.zip");
        using var stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var manifest = new Dictionary<string, object?>
        {
            ["gameId"] = gameId,
            ["pluginId"] = pluginId,
            ["modVersion"] = modVersion,
            ["installActions"] = new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mods/mod.dll" } },
            ["verify"] = Array.Empty<object>(),
            ["preInstall"] = new
            {
                executable = "pre.cmd",
                needsAdmin = false,
                failureFatal = true,
                what = "x",
                why = "x",
                modifies = "x",
                runOnUpdate = false,
                runFromGameFolder = false
            }
        };
        AddEntry(archive, "manifest.json", JsonSerializer.Serialize(manifest));
        AddEntry(archive, "pre.cmd", "@exit /b 0\r\n");
        AddEntry(archive, "files/mod.dll", "modbytes");
        return zipPath;
    }

    private string CreateModWithPostUninstall(
        string pluginId, string gameId, string modVersion, object[]? verify = null,
        (string relPath, string content)[]? files = null, object[]? actions = null)
    {
        files ??= new[] { ("mod.dll", "modbytes") };
        actions ??= new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mods/mod.dll" } };

        var zipPath = Path.Combine(_tempRoot, $"pkg_{Guid.NewGuid():N}.zip");
        using var stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var manifest = new Dictionary<string, object?>
        {
            ["gameId"] = gameId,
            ["pluginId"] = pluginId,
            ["modVersion"] = modVersion,
            ["installActions"] = actions,
            ["verify"] = verify ?? Array.Empty<object>(),
            ["postUninstall"] = new
            {
                executable = "cleanup.cmd",
                needsAdmin = false,
                failureFatal = false,
                what = "cleanup",
                why = "cleanup",
                modifies = "nothing",
                runOnUpdate = false,
                runFromGameFolder = false
            }
        };
        AddEntry(archive, "manifest.json", JsonSerializer.Serialize(manifest));
        AddEntry(archive, "cleanup.cmd", "@exit /b 0\r\n");
        foreach (var (rel, content) in files)
            AddEntry(archive, $"files/{rel}", content);
        return zipPath;
    }

    private static void AddEntry(ZipArchive archive, string path, string contents)
    {
        var e = archive.CreateEntry(path);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        w.Write(contents);
    }

    private string MakeZip(params (string EntryName, string Content)[] entries)
    {
        var zipPath = Path.Combine(_tempRoot, $"dep_{Guid.NewGuid():N}.zip");
        using var fs = File.Create(zipPath);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
            AddEntry(archive, name, content);
        return zipPath;
    }

    private static string Sha(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
    }

    private sealed class AcceptingScriptHost : IScriptHost
    {
        public int UninstallConfirmCount { get; private set; }
        public List<string> StartingHooks { get; } = new();

        public Task<bool> ConfirmInstallScriptsAsync(LifecycleScriptPrompt prompt, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> ConfirmUninstallScriptAsync(LifecycleScriptPrompt prompt, CancellationToken ct)
        {
            UninstallConfirmCount++;
            return Task.FromResult(true);
        }
        public void OnScriptStarting(string hookLabel, string scriptName) => StartingHooks.Add(hookLabel);
        public void OnScriptOutputLine(string line) { }
        public void OnScriptFinished(int exitCode, bool succeeded) { }
    }

    private sealed class BlockingScriptHost : IScriptHost
    {
        public TaskCompletionSource ConsentRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ConsentResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ConfirmInstallScriptsAsync(LifecycleScriptPrompt prompt, CancellationToken ct)
        {
            ConsentRequested.TrySetResult();
            return ConsentResult.Task;
        }

        public Task<bool> ConfirmUninstallScriptAsync(LifecycleScriptPrompt prompt, CancellationToken ct) => Task.FromResult(true);
        public void OnScriptStarting(string hookLabel, string scriptName) { }
        public void OnScriptOutputLine(string line) { }
        public void OnScriptFinished(int exitCode, bool succeeded) { }
    }

    private sealed class AcceptingDepHost : IDependencyHost
    {
        public Task<bool> ConfirmDependencyInstallAsync(DependencyInstallPrompt prompt, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> AwaitManualDependencyAsync(DependencyManualPrompt prompt, CancellationToken ct) => Task.FromResult(true);
        public void OnDependencyStarting(string dependencyId, string kind, string displayName) { }
        public void OnDependencyOutputLine(string line) { }
        public void OnDependencyFinished(string dependencyId, bool succeeded) { }
    }

    private sealed class RefuseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Test should not have reached the network.");
    }

    private sealed class ServeFileHandler : HttpMessageHandler
    {
        private readonly string _filePath;
        public ServeFileHandler(string filePath) { _filePath = filePath; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(File.ReadAllBytes(_filePath))
            };
            return Task.FromResult(resp);
        }
    }

    private sealed class InMemoryReceiptStore : IReceiptStore
    {
        private readonly Dictionary<(string game, string plug), InstallReceipt> _store = new();
        private readonly string _dirRoot = Path.Combine(Path.GetTempPath(), "amm-test-receipts-integrity", Guid.NewGuid().ToString("N"));

        public Task<InstallReceipt?> LoadAsync(string gameId, string pluginId) =>
            Task.FromResult(_store.TryGetValue((gameId, pluginId), out var r) ? r : null);

        public Task SaveAsync(InstallReceipt receipt)
        {
            _store[(receipt.GameId, receipt.PluginId)] = receipt;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string gameId, string pluginId)
        {
            _store.Remove((gameId, pluginId));
            return Task.CompletedTask;
        }

        public Task<List<InstallReceipt>> LoadAllForGameAsync(string gameId) =>
            Task.FromResult(_store.Where(kv => kv.Key.game == gameId).Select(kv => kv.Value).ToList());

        public Task<List<string>> UnreadablePluginIdsForGameAsync(string gameId) =>
            Task.FromResult(new List<string>());

        public string GetReceiptDirectory(string gameId, string pluginId) =>
            Path.Combine(_dirRoot, pluginId, gameId);
    }

    private sealed class InMemoryDepStore : IDependencyReceiptStore
    {
        private readonly Dictionary<(string game, string dep), DependencyReceipt> _store = new();
        private readonly string _backupRoot;

        public InMemoryDepStore(string backupRoot) { _backupRoot = backupRoot; }

        public Task<DependencyReceipt?> LoadAsync(string gameId, string dependencyId) =>
            Task.FromResult(_store.TryGetValue((gameId, dependencyId), out var r) ? r : null);

        public Task SaveAsync(DependencyReceipt receipt)
        {
            _store[(receipt.GameId, receipt.DependencyId)] = receipt;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string gameId, string dependencyId)
        {
            _store.Remove((gameId, dependencyId));
            return Task.CompletedTask;
        }

        public Task<List<DependencyReceipt>> LoadAllForGameAsync(string gameId) =>
            Task.FromResult(_store.Where(kv => kv.Key.game == gameId).Select(kv => kv.Value).ToList());

        public bool Unreadable { get; set; }
        public Task<bool> AnyUnreadableForGameAsync(string gameId) => Task.FromResult(Unreadable);

        public string GetBackupDirectory(string gameId, string dependencyId) =>
            Path.Combine(_backupRoot, gameId, dependencyId);
    }
}
