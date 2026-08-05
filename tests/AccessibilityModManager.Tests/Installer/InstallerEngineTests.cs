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
/// End-to-end tests for the transactional install flow: extract → manifest → backup → apply →
/// verify → receipt, plus rollback and collision detection.
/// </summary>
public class InstallerEngineTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _gameDir;
    private readonly string _receiptsRoot;
    private readonly InstallerEngine _engine;
    private readonly InMemoryReceiptStore _receiptStore;

    public InstallerEngineTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_engine_" + Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_tempRoot, "game");
        _receiptsRoot = Path.Combine(_tempRoot, "receipts");
        Directory.CreateDirectory(_gameDir);
        Directory.CreateDirectory(_receiptsRoot);

        var logger = TestLogger.Create();
        var backupManager = new BackupManager(logger);
        var actionExecutor = new InstallActionExecutor(backupManager, logger);
        var verifier = new InstallVerifier(logger);
        var manifestParser = new ManifestParser(logger);
        var zipExtractor = new SafeZipExtractor(logger);
        _receiptStore = new InMemoryReceiptStore();
        // Real DependencyChecker; tests with deps pass a present file path so it reports Installed.
        var dependencyChecker = new DependencyChecker(logger);
        var scriptRunner = new LifecycleScriptRunner(logger);
        // No tests currently exercise auto-install, so the in-memory dep receipt store is
        // unused by the current suite — but the engine constructor requires it.
        var depReceiptStore = new InMemoryDependencyReceiptStore();
        var depAutoInstaller = new DependencyAutoInstaller(new System.Net.Http.HttpClient(), depReceiptStore, logger);

        _engine = new InstallerEngine(
            backupManager, actionExecutor, verifier, manifestParser, zipExtractor,
            _receiptStore, dependencyChecker, scriptRunner, depAutoInstaller,
            new GameVerifier(logger), logger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task InstallAsync_HappyPath_AppliesActionsAndWritesReceipt()
    {
        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("mods/mod.dll", "modbytes") },
            actions: new[] { (object)new { type = "copyFile", source = "mods/mod.dll", target = "mods/mod.dll" } });

        var receipt = await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zip), zip);

        Assert.Equal("plug-a", receipt.PluginId);
        Assert.Equal("game-1", receipt.GameId);
        Assert.Equal("1.0.0", receipt.InstalledVersion);
        Assert.Single(receipt.Changes);
        Assert.Equal(ChangeType.Added, receipt.Changes[0].Type);
        Assert.True(File.Exists(Path.Combine(_gameDir, "mods", "mod.dll")));
        Assert.NotNull(await _receiptStore.LoadAsync("game-1", "plug-a"));
    }

    [Fact]
    public async Task InstallAsync_MismatchedGameId_Throws()
    {
        var zip = CreateMod("plug-a", "wrong-game", "1.0.0",
            files: new[] { ("a.txt", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "a.txt", target = "a.txt" } });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zip), zip));
    }

    [Fact]
    public async Task InstallAsync_MismatchedPluginId_Throws()
    {
        var zip = CreateMod("wrong-plug", "game-1", "1.0.0",
            files: new[] { ("a.txt", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "a.txt", target = "a.txt" } });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zip), zip));
    }

    [Fact]
    public async Task InstallAsync_AlreadyInstalledByOwnPlugin_Throws()
    {
        // First install
        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("a.txt", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "a.txt", target = "a.txt" } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zip), zip);

        // Second install of the same plugin should refuse
        var zip2 = CreateMod("plug-a", "game-1", "1.1.0",
            files: new[] { ("a.txt", "y") },
            actions: new[] { (object)new { type = "copyFile", source = "a.txt", target = "a.txt" } });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.1.0", zip2), zip2));
    }

    [Fact]
    public async Task InstallAsync_FileCollisionWithOtherPlugin_Throws()
    {
        // plug-a installs file
        var zipA = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("shared.dll", "a") },
            actions: new[] { (object)new { type = "copyFile", source = "shared.dll", target = "shared.dll" } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zipA), zipA);

        // plug-b tries to touch the same file
        var zipB = CreateMod("plug-b", "game-1", "1.0.0",
            files: new[] { ("shared.dll", "b") },
            actions: new[] { (object)new { type = "copyFile", source = "shared.dll", target = "shared.dll" } });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-b"), MakeRelease("plug-b", "game-1", "1.0.0", zipB), zipB));
        Assert.Contains("conflict", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_CopyFolderCollisionWithOtherPlugin_Throws()
    {
        // plug-a owns a game file via copyFile (author-written forward slash target).
        var zipA = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("shared.dll", "a") },
            actions: new[] { (object)new { type = "copyFile", source = "shared.dll", target = "libs/shared.dll" } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zipA), zipA);

        // plug-b writes the SAME game file via copyFolder — previously slipped past collision
        // detection entirely (copyFolder targets weren't enumerated).
        var zipB = CreateMod("plug-b", "game-1", "1.0.0",
            files: new[] { ("payload/shared.dll", "b") },
            actions: new[] { (object)new { type = "copyFolder", sourceDir = "payload", targetDir = "libs" } });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-b"), MakeRelease("plug-b", "game-1", "1.0.0", zipB), zipB));
        Assert.Contains("conflict", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_SecondActionFails_RollsBackFirstActionAndWritesNoReceipt()
    {
        // Two actions: the first copies a real file, the second references a missing package source
        // and throws mid-install. The first file must be rolled back — previously it was orphaned
        // in the game folder because the receipt wasn't built yet, so rollback was skipped.
        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("good.dll", "x") },
            actions: new[]
            {
                (object)new { type = "copyFile", source = "good.dll", target = "mods/good.dll" },
                (object)new { type = "copyFile", source = "missing.dll", target = "mods/missing.dll" }
            });

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zip), zip));

        Assert.False(File.Exists(Path.Combine(_gameDir, "mods", "good.dll")));
        Assert.Null(await _receiptStore.LoadAsync("game-1", "plug-a"));
    }

    [Fact]
    public async Task UninstallAsync_RemovesAddedFilesAndReceipt()
    {
        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("mods/added.dll", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "mods/added.dll", target = "mods/added.dll" } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zip), zip);
        Assert.True(File.Exists(Path.Combine(_gameDir, "mods", "added.dll")));

        await _engine.UninstallAsync(MakeGameInstall("game-1", "plug-a"), "plug-a");

        Assert.False(File.Exists(Path.Combine(_gameDir, "mods", "added.dll")));
        Assert.Null(await _receiptStore.LoadAsync("game-1", "plug-a"));
    }

    [Fact]
    public async Task UninstallAsync_WhenInstallPathIsJunction_DoesNotDestroyRealGame()
    {
        // The catastrophic scenario for an AsciiPathShim game (e.g. Pokémon TCG Live): the
        // install path is an NTFS junction pointing into the real game folder. Uninstall must
        // remove only the mod's own files and must NEVER recursively delete the install root
        // through the junction.
        var realGame = Path.Combine(_tempRoot, "real-game");
        Directory.CreateDirectory(realGame);
        var precious = Path.Combine(realGame, "Pokemon TCG Live.exe");
        File.WriteAllText(precious, "PRECIOUS GAME BINARY");

        var junction = Path.Combine(_tempRoot, "PokemonTCGLive");
        await new AsciiPathShimService(TestLogger.Create()).CreateJunctionAsync(junction, realGame);

        var gameInstall = new GameInstall
        {
            Game = new GameDefinition { GameId = "game-1", DisplayName = "Game 1" },
            PluginId = "plug-a",
            InstallPath = junction,
            IsValid = true
        };

        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("Mods/mod.dll", "modbytes") },
            actions: new[] { (object)new { type = "copyFile", source = "Mods/mod.dll", target = "Mods/mod.dll" } });

        await _engine.InstallAsync(gameInstall, MakeRelease("plug-a", "game-1", "1.0.0", zip), zip);
        Assert.True(File.Exists(Path.Combine(junction, "Mods", "mod.dll")));
        Assert.True(File.Exists(precious));

        await _engine.UninstallAsync(gameInstall, "plug-a");

        // Mod file removed, but the junction and the real game binary both survive.
        Assert.False(File.Exists(Path.Combine(junction, "Mods", "mod.dll")));
        Assert.True(Directory.Exists(junction));
        Assert.True(File.Exists(precious));
        Assert.Equal("PRECIOUS GAME BINARY", File.ReadAllText(precious));
    }

    [Fact]
    public async Task UninstallAsync_RestoresReplacedFiles()
    {
        // Pre-existing original
        Directory.CreateDirectory(Path.Combine(_gameDir, "config"));
        File.WriteAllText(Path.Combine(_gameDir, "config", "settings.cfg"), "ORIGINAL");

        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("settings.cfg", "MODDED") },
            actions: new[] { (object)new { type = "replaceFile", source = "settings.cfg", target = "config/settings.cfg", backup = true } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zip), zip);
        Assert.Equal("MODDED", File.ReadAllText(Path.Combine(_gameDir, "config", "settings.cfg")));

        await _engine.UninstallAsync(MakeGameInstall("game-1", "plug-a"), "plug-a");

        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(_gameDir, "config", "settings.cfg")));
    }

    [Fact]
    public async Task ReplaceFileWithBackupFalse_StillRestoresOriginalOnUninstall()
    {
        // Even when the manifest sets backup=false, an existing file must be backed up so uninstall
        // can restore it — a non-restorable replacement would silently destroy the original.
        Directory.CreateDirectory(Path.Combine(_gameDir, "config"));
        File.WriteAllText(Path.Combine(_gameDir, "config", "settings.cfg"), "ORIGINAL");

        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("settings.cfg", "MODDED") },
            actions: new[] { (object)new { type = "replaceFile", source = "settings.cfg", target = "config/settings.cfg", backup = false } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zip), zip);
        Assert.Equal("MODDED", File.ReadAllText(Path.Combine(_gameDir, "config", "settings.cfg")));

        await _engine.UninstallAsync(MakeGameInstall("game-1", "plug-a"), "plug-a");

        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(_gameDir, "config", "settings.cfg")));
    }

    [Fact]
    public async Task UpdateAsync_NewVersionFailsVerification_RestoresPreviousVersion()
    {
        // v1 installs cleanly.
        var zip1 = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("mod.dll", "V1") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zip1), zip1);
        Assert.Equal("V1", File.ReadAllText(Path.Combine(_gameDir, "mod.dll")));

        // v2 fails post-install verification, so the update must roll the game back to v1 rather than
        // leaving the user with nothing installed.
        var zip2 = CreateMod("plug-a", "game-1", "1.1.0",
            files: new[] { ("mod.dll", "V2") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            verify: new[] { (object)new { type = "fileExists", path = "should-not-exist.dll" } });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.UpdateAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.1.0", zip2), zip2));

        // Previous version restored: file content back to V1 and the v1 receipt is present again.
        Assert.Equal("V1", File.ReadAllText(Path.Combine(_gameDir, "mod.dll")));
        var receipt = await _receiptStore.LoadAsync("game-1", "plug-a");
        Assert.NotNull(receipt);
        Assert.Equal("1.0.0", receipt!.InstalledVersion);
    }

    [Fact]
    public async Task UpdateAsync_ChangedScript_ReConfirms()
    {
        var host = new RecordingScriptHost { ConfirmInstallResult = true };

        var zipV1 = CreateModWithScripts("plug-a", "game-1", "1.0.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", "x", "x", "x"),
            postInstall: null, postUninstall: null,
            files: new[] { ("mod.dll", "v1") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.0.0", zipV1), zipV1, host);
        Assert.Equal(1, host.InstallConfirmCount);

        // v2's pre-install script has DIFFERENT contents — the user must be re-warned.
        var zipV2 = CreateModWithScripts("plug-a", "game-1", "1.1.0",
            preInstall: ("pre.cmd", "@echo changed behaviour\r\n@exit /b 0\r\n", "x", "x", "x"),
            postInstall: null, postUninstall: null,
            files: new[] { ("mod.dll", "v2") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });
        await _engine.UpdateAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.1.0", zipV2), zipV2, host);

        Assert.Equal(2, host.InstallConfirmCount);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotRunOldPostUninstallScript()
    {
        // On update the mod is replaced, not removed — the old version's post-uninstall (removal
        // cleanup) must NOT run. Its side effects wouldn't be undone if the update then failed.
        var host = new RecordingScriptHost { ConfirmInstallResult = true, ConfirmUninstallResult = true };

        var zipV1 = CreateModWithScripts("plug-a", "game-1", "1.0.0",
            preInstall: null, postInstall: null,
            postUninstall: ("cleanup.cmd", "@exit /b 0\r\n", "cleanup", "cleanup", "cleans"),
            files: new[] { ("mod.dll", "v1") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.0.0", zipV1), zipV1, host);

        var zipV2 = CreateModWithScripts("plug-a", "game-1", "1.1.0",
            preInstall: null, postInstall: null,
            postUninstall: ("cleanup.cmd", "@exit /b 0\r\n", "cleanup", "cleanup", "cleans"),
            files: new[] { ("mod.dll", "v2") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });
        await _engine.UpdateAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.1.0", zipV2), zipV2, host);

        Assert.DoesNotContain("Post-uninstall", host.StartingHooks);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(_gameDir, "mod.dll")));
    }

    [Fact]
    public async Task UninstallAsync_TamperedCachedPostUninstallScript_IsRefused()
    {
        var zip = CreateModWithScripts("plug-tamper", "game-tamper", "1.0.0",
            preInstall: null, postInstall: null,
            postUninstall: ("cleanup.cmd", "@echo cleanup\r\n@exit /b 0\r\n", "cleanup", "cleanup", "removes files"),
            files: new[] { ("mod.dll", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });

        var gi = MakeGameInstall("game-tamper", "plug-tamper");
        await _engine.InstallAsync(gi, MakeRelease("plug-tamper", "game-tamper", "1.0.0", zip), zip,
            new RecordingScriptHost { ConfirmInstallResult = true });

        // Swap the cached post-uninstall script on disk for a different one.
        var cachedScript = Path.Combine(Path.GetTempPath(), "amm-test-receipts",
            "plug-tamper", "game-tamper", "scripts", "cleanup.cmd");
        Assert.True(File.Exists(cachedScript));
        File.WriteAllText(cachedScript, "@echo TAMPERED\r\n@exit /b 0\r\n");

        var uninstallHost = new RecordingScriptHost { ConfirmUninstallResult = true };
        await _engine.UninstallAsync(gi, "plug-tamper", uninstallHost);

        // The tampered script must not run — no post-uninstall hook started, and no consent prompt.
        Assert.DoesNotContain("Post-uninstall", uninstallHost.StartingHooks);
        Assert.Equal(0, uninstallHost.UninstallConfirmCount);
    }

    [Fact]
    public async Task UpdateAsync_ReplacesPreviousInstall()
    {
        var zip1 = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("mod.dll", "v1") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zip1), zip1);

        var zip2 = CreateMod("plug-a", "game-1", "1.1.0",
            files: new[] { ("mod.dll", "v2") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });
        var receipt = await _engine.UpdateAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.1.0", zip2), zip2);

        Assert.Equal("1.1.0", receipt.InstalledVersion);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(_gameDir, "mod.dll")));
    }

    [Fact]
    public async Task InstallAsync_VerificationFails_RollsBackAndThrows()
    {
        // Manifest verifies a file that won't exist after install
        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("mod.dll", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            verify: new[] { (object)new { type = "fileExists", path = "should-not-exist.dll" } });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zip), zip));

        // Rollback should have removed the added file and not saved a receipt
        Assert.False(File.Exists(Path.Combine(_gameDir, "mod.dll")));
        Assert.Null(await _receiptStore.LoadAsync("game-1", "plug-a"));
    }

    [Fact]
    public async Task InstallAsync_MissingRequiredDependency_ThrowsAndDoesNotTouchFiles()
    {
        // Game declares a required framework dependency that doesn't exist in the game dir.
        var gameInstall = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "game-1",
                DisplayName = "Game 1",
                Dependencies = new List<Dependency>
                {
                    new()
                    {
                        Id = "missing-framework",
                        Type = "framework",
                        Required = true,
                        Check = new DependencyCheck { FilePath = "missing-thing.dll" }
                    }
                }
            },
            PluginId = "plug-a",
            InstallPath = _gameDir,
            IsValid = true
        };

        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("mods/added.dll", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "mods/added.dll", target = "mods/added.dll" } });

        await Assert.ThrowsAsync<MissingRequiredDependencyException>(() =>
            _engine.InstallAsync(gameInstall, MakeRelease("plug-a", "game-1", "1.0.0", zip), zip));

        // No file should have been written, no receipt stored.
        Assert.False(File.Exists(Path.Combine(_gameDir, "mods", "added.dll")));
        Assert.Null(await _receiptStore.LoadAsync("game-1", "plug-a"));
    }

    [Fact]
    public async Task InstallAsync_GameInstallerDependency_IsIgnoredByDepResolution()
    {
        // A game-installer dependency is handled by the manager's pre-install step, not the
        // engine — by install time the game is already there. The engine must skip it entirely,
        // even though it's Required and has no Check (so the checker would call it Missing).
        // Without the exclusion this throws MissingRequiredDependencyException (no host passed).
        var gameInstall = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "game-1",
                DisplayName = "Game 1",
                Dependencies = new List<Dependency>
                {
                    new()
                    {
                        Id = "the-game-itself",
                        Type = "system",
                        Required = true,
                        IsGameInstaller = true,
                        Fix = new DependencyFix
                        {
                            DownloadUrl = "https://example.invalid/installer.msi",
                            AutoInstall = new RunInstallerAutoInstall { Sha256 = "deadbeef" }
                        }
                    }
                }
            },
            PluginId = "plug-a",
            InstallPath = _gameDir,
            IsValid = true
        };

        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("mods/added.dll", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "mods/added.dll", target = "mods/added.dll" } });

        var receipt = await _engine.InstallAsync(gameInstall, MakeRelease("plug-a", "game-1", "1.0.0", zip), zip);

        Assert.Equal("1.0.0", receipt.InstalledVersion);
        Assert.True(File.Exists(Path.Combine(_gameDir, "mods", "added.dll")));
    }

    [Fact]
    public async Task InstallAsync_OptionalDependencyMissing_DoesNotBlock()
    {
        var gameInstall = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "game-1",
                DisplayName = "Game 1",
                Dependencies = new List<Dependency>
                {
                    new()
                    {
                        Id = "optional-framework",
                        Type = "framework",
                        Required = false,
                        Check = new DependencyCheck { FilePath = "missing-optional.dll" }
                    }
                }
            },
            PluginId = "plug-a",
            InstallPath = _gameDir,
            IsValid = true
        };

        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("mods/added.dll", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "mods/added.dll", target = "mods/added.dll" } });

        var receipt = await _engine.InstallAsync(gameInstall, MakeRelease("plug-a", "game-1", "1.0.0", zip), zip);
        Assert.Equal("1.0.0", receipt.InstalledVersion);
    }

    [Fact]
    public async Task InstallAsync_WithLifecycleScripts_ConfirmsAndRunsThem()
    {
        var zip = CreateModWithScripts(
            "plug-a", "game-1", "1.0.0",
            preInstall: ("pre.cmd", "@echo pre-install ran\r\n@exit /b 0\r\n", "Initialize", "Ensures order", "writes pre-install marker"),
            postInstall: ("post.cmd", "@echo post-install ran\r\n@exit /b 0\r\n", "Finalize", "Final touches", "writes post-install marker"),
            postUninstall: null,
            files: new[] { ("mod.dll", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });

        var host = new RecordingScriptHost { ConfirmInstallResult = true };
        var receipt = await _engine.InstallAsync(
            MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.0.0", zip), zip, host);

        Assert.Equal(1, host.InstallConfirmCount);
        Assert.Equal(2, host.StartingHooks.Count); // pre-install + post-install
        Assert.Contains("Pre-install", host.StartingHooks);
        Assert.Contains("Post-install", host.StartingHooks);
        Assert.NotNull(receipt);
    }

    [Fact]
    public async Task InstallAsync_UserDeclinesScripts_AbortsBeforeAnyChange()
    {
        var zip = CreateModWithScripts(
            "plug-a", "game-1", "1.0.0",
            preInstall: ("pre.cmd", "@echo should-not-run\r\n@exit /b 0\r\n", "noop", "noop", "nothing"),
            postInstall: null,
            postUninstall: null,
            files: new[] { ("mod.dll", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });

        var host = new RecordingScriptHost { ConfirmInstallResult = false };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
                MakeRelease("plug-a", "game-1", "1.0.0", zip), zip, host));

        Assert.Empty(host.StartingHooks); // no script was run
        Assert.False(File.Exists(Path.Combine(_gameDir, "mod.dll")));
        Assert.Null(await _receiptStore.LoadAsync("game-1", "plug-a"));
    }

    [Fact]
    public async Task InstallAsync_FatalPreInstallScriptFailure_AbortsAndRollsBack()
    {
        // Pre-install runs first (after backup). When it exits non-zero with FailureFatal=true,
        // the install must throw and no files should be written.
        var zip = CreateModWithScripts(
            "plug-a", "game-1", "1.0.0",
            preInstall: ("boom.cmd", "@exit /b 9\r\n", "fail", "fail", "nothing"),
            postInstall: null,
            postUninstall: null,
            files: new[] { ("mod.dll", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });

        var host = new RecordingScriptHost { ConfirmInstallResult = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
                MakeRelease("plug-a", "game-1", "1.0.0", zip), zip, host));

        Assert.False(File.Exists(Path.Combine(_gameDir, "mod.dll")));
        Assert.Null(await _receiptStore.LoadAsync("game-1", "plug-a"));
    }

    [Fact]
    public async Task UninstallAsync_RunsCachedPostUninstallScript()
    {
        // Install a mod with a post-uninstall script, then uninstall and verify the host saw
        // the cached script run.
        var zip = CreateModWithScripts(
            "plug-a", "game-1", "1.0.0",
            preInstall: null,
            postInstall: null,
            postUninstall: ("cleanup.cmd", "@echo cleanup\r\n@exit /b 0\r\n", "cleanup", "cleanup", "removes cached files"),
            files: new[] { ("mod.dll", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });

        var installHost = new RecordingScriptHost { ConfirmInstallResult = true };
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.0.0", zip), zip, installHost);

        var uninstallHost = new RecordingScriptHost { ConfirmUninstallResult = true };
        await _engine.UninstallAsync(MakeGameInstall("game-1", "plug-a"), "plug-a", uninstallHost);

        Assert.Equal(1, uninstallHost.UninstallConfirmCount);
        Assert.Contains("Post-uninstall", uninstallHost.StartingHooks);
        Assert.False(File.Exists(Path.Combine(_gameDir, "mod.dll")));
        Assert.Null(await _receiptStore.LoadAsync("game-1", "plug-a"));
    }

    [Fact]
    public async Task UpdateAsync_DoesNotReConfirmScripts()
    {
        // Q9=A: scripts on the new version inherit the consent the user gave for the first
        // install. Update path should NOT call ConfirmInstallScriptsAsync again.
        var zipV1 = CreateModWithScripts(
            "plug-a", "game-1", "1.0.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", "x", "x", "x"),
            postInstall: null,
            postUninstall: null,
            files: new[] { ("mod.dll", "v1") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });

        var host = new RecordingScriptHost { ConfirmInstallResult = true };
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.0.0", zipV1), zipV1, host);
        Assert.Equal(1, host.InstallConfirmCount);

        var zipV2 = CreateModWithScripts(
            "plug-a", "game-1", "1.1.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", "x", "x", "x"),
            postInstall: null,
            postUninstall: null,
            files: new[] { ("mod.dll", "v2") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });

        await _engine.UpdateAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.1.0", zipV2), zipV2, host);

        // Still only one install-confirm — the update path skipped re-asking.
        Assert.Equal(1, host.InstallConfirmCount);
    }

    [Fact]
    public async Task UpdateAsync_SkipsPreInstallScript_WhenRunOnUpdateIsFalse()
    {
        // Default behavior: pre/post-install scripts are install-only. The update path skips
        // them so a registry-write style script doesn't re-fire on every version bump.
        var zipV1 = CreateModWithScripts(
            "plug-a", "game-1", "1.0.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", "x", "x", "x"),
            postInstall: null,
            postUninstall: null,
            files: new[] { ("mod.dll", "v1") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });

        var host = new RecordingScriptHost { ConfirmInstallResult = true };
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.0.0", zipV1), zipV1, host);
        var hooksAfterInstall = host.StartingHooks.Count(h => h == "Pre-install");
        Assert.Equal(1, hooksAfterInstall);

        var zipV2 = CreateModWithScripts(
            "plug-a", "game-1", "1.1.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", "x", "x", "x"),
            postInstall: null,
            postUninstall: null,
            files: new[] { ("mod.dll", "v2") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });

        await _engine.UpdateAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.1.0", zipV2), zipV2, host);

        // Still only the original first-install pre-install run — update path didn't fire it.
        Assert.Equal(1, host.StartingHooks.Count(h => h == "Pre-install"));
    }

    [Fact]
    public async Task UpdateAsync_RunsPreInstallScript_WhenRunOnUpdateIsTrue()
    {
        // Opt-in: when the script declares runOnUpdate=true the engine fires it on update too.
        var zipV1 = CreateModWithScripts(
            "plug-a", "game-1", "1.0.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", "x", "x", "x"),
            postInstall: null,
            postUninstall: null,
            files: new[] { ("mod.dll", "v1") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            preInstallRunOnUpdate: true);

        var host = new RecordingScriptHost { ConfirmInstallResult = true };
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.0.0", zipV1), zipV1, host);

        var zipV2 = CreateModWithScripts(
            "plug-a", "game-1", "1.1.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", "x", "x", "x"),
            postInstall: null,
            postUninstall: null,
            files: new[] { ("mod.dll", "v2") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            preInstallRunOnUpdate: true);

        await _engine.UpdateAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.1.0", zipV2), zipV2, host);

        // Both install and update fired the pre-install hook.
        Assert.Equal(2, host.StartingHooks.Count(h => h == "Pre-install"));
    }

    [Fact]
    public async Task UpdateAsync_ReAsksConsent_WhenOnlyTheScriptDescriptionChanged()
    {
        // Audit finding 42. The What / Why / Modifies lines ARE the consent — they're the entire
        // basis on which the user agreed to run author-supplied code. An update that leaves the
        // script byte-identical but rewrites its description is claiming it does something else,
        // so it has to ask again.
        var zipV1 = CreateModWithScripts(
            "plug-a", "game-1", "1.0.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", "Registers the mod", "The game needs it", "Nothing outside the game folder"),
            postInstall: null,
            postUninstall: null,
            files: new[] { ("mod.dll", "v1") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            preInstallRunOnUpdate: true);

        var host = new RecordingScriptHost { ConfirmInstallResult = true };
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.0.0", zipV1), zipV1, host);
        Assert.Equal(1, host.InstallConfirmCount);

        // Same script bytes, same flags — only the description changed.
        var zipV2 = CreateModWithScripts(
            "plug-a", "game-1", "1.1.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", "Deletes your save files", "Because", "Everything"),
            postInstall: null,
            postUninstall: null,
            files: new[] { ("mod.dll", "v2") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            preInstallRunOnUpdate: true);

        await _engine.UpdateAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.1.0", zipV2), zipV2, host);

        Assert.Equal(2, host.InstallConfirmCount);
    }

    [Theory]
    [InlineData("CHANGED", "The game needs it", "Nothing outside")]
    [InlineData("Registers the mod", "CHANGED", "Nothing outside")]
    [InlineData("Registers the mod", "The game needs it", "CHANGED")]
    public async Task UpdateAsync_ReAsksConsent_WhenAnySingleDescriptionFieldChanges(
        string what, string why, string modifies)
    {
        // Each field on its own, so a fingerprint that happened to cover only one of the three
        // can't pass by covering the others.
        var zipV1 = CreateModWithScripts(
            "plug-a", "game-1", "1.0.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", "Registers the mod", "The game needs it", "Nothing outside"),
            postInstall: null, postUninstall: null,
            files: new[] { ("mod.dll", "v1") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            preInstallRunOnUpdate: true);

        var host = new RecordingScriptHost { ConfirmInstallResult = true };
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.0.0", zipV1), zipV1, host);

        var zipV2 = CreateModWithScripts(
            "plug-a", "game-1", "1.1.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", what, why, modifies),
            postInstall: null, postUninstall: null,
            files: new[] { ("mod.dll", "v2") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            preInstallRunOnUpdate: true);

        await _engine.UpdateAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.1.0", zipV2), zipV2, host);

        Assert.Equal(2, host.InstallConfirmCount);
    }

    [Fact]
    public async Task UpdateAsync_ReAsksConsent_WhenDescriptionsAreShuffledAcrossFields()
    {
        // Delimiter collision: with the fields simply concatenated, moving text across a field
        // boundary produced identical bytes, so a rewritten warning slipped through unasked. The
        // lengths are what make these two distinguishable.
        var zipV1 = CreateModWithScripts(
            "plug-a", "game-1", "1.0.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", "a|why=b", "c", "m"),
            postInstall: null, postUninstall: null,
            files: new[] { ("mod.dll", "v1") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            preInstallRunOnUpdate: true);

        var host = new RecordingScriptHost { ConfirmInstallResult = true };
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.0.0", zipV1), zipV1, host);

        var zipV2 = CreateModWithScripts(
            "plug-a", "game-1", "1.1.0",
            preInstall: ("pre.cmd", "@exit /b 0\r\n", "a", "b|why=c", "m"),
            postInstall: null, postUninstall: null,
            files: new[] { ("mod.dll", "v2") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            preInstallRunOnUpdate: true);

        await _engine.UpdateAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.1.0", zipV2), zipV2, host);

        Assert.Equal(2, host.InstallConfirmCount);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotReAskConsent_WhenTheScriptIsUnchanged()
    {
        // The other half of finding 42: an unchanged script must NOT nag on every update.
        var script = ("pre.cmd", "@exit /b 0\r\n", "Registers the mod", "The game needs it", "Nothing outside");
        var zipV1 = CreateModWithScripts(
            "plug-a", "game-1", "1.0.0",
            preInstall: script, postInstall: null, postUninstall: null,
            files: new[] { ("mod.dll", "v1") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            preInstallRunOnUpdate: true);

        var host = new RecordingScriptHost { ConfirmInstallResult = true };
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.0.0", zipV1), zipV1, host);

        var zipV2 = CreateModWithScripts(
            "plug-a", "game-1", "1.1.0",
            preInstall: script, postInstall: null, postUninstall: null,
            files: new[] { ("mod.dll", "v2") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            preInstallRunOnUpdate: true);

        await _engine.UpdateAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.1.0", zipV2), zipV2, host);

        Assert.Equal(1, host.InstallConfirmCount);
    }

    [Fact]
    public async Task InstallAsync_PostInstallRunFromGameFolder_RunsFromGameFolderAndCleansUp()
    {
        // RunFromGameFolder=true means the manager copies the script into the game folder
        // before running so the script's own location is the game folder. Default cleanup
        // removes the file after — InstallToGameFolder=false on this script.
        // The script is a .cmd that writes a marker file with the contents of CD (i.e. the
        // working directory). We assert the marker was written into the game folder, which
        // proves the script ran from there.
        var markerName = "ran-from.txt";
        var script = $"@echo off\r\necho %CD%>{markerName}\r\nexit /b 0\r\n";

        var zip = CreateModWithScripts(
            "plug-a", "game-1", "1.0.0",
            preInstall: null,
            postInstall: ("post.cmd", script, "Marker writer", "Verifies run-from-game-folder", "writes ran-from.txt to game folder"),
            postUninstall: null,
            files: new[] { ("mod.dll", "data") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } },
            postInstallRunFromGameFolder: true);

        var host = new RecordingScriptHost { ConfirmInstallResult = true };
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"),
            MakeRelease("plug-a", "game-1", "1.0.0", zip), zip, host);

        // Marker must exist in the game folder, proving the script's CWD was the game folder.
        var markerPath = Path.Combine(_gameDir, markerName);
        Assert.True(File.Exists(markerPath), $"Expected marker at {markerPath}");
        var markerContent = (await File.ReadAllTextAsync(markerPath)).Trim();
        Assert.Equal(_gameDir.TrimEnd('\\'), markerContent.TrimEnd('\\'), ignoreCase: true);

        // Default cleanup: the script copy in the game folder must be removed after the run
        // since InstallToGameFolder is false.
        Assert.False(File.Exists(Path.Combine(_gameDir, "post.cmd")),
            "RunFromGameFolder + InstallToGameFolder=false should clean up the script copy");
    }

    [Fact]
    public async Task InstallAsync_MissingManifest_Throws()
    {
        var zipPath = Path.Combine(_tempRoot, $"nomanifest_{Guid.NewGuid():N}.zip");
        using (var stream = File.Create(zipPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var e = archive.CreateEntry("files/something.txt");
            using var w = new StreamWriter(e.Open());
            w.Write("data");
        }

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0", zipPath), zipPath));
    }

    private GameInstall MakeGameInstall(string gameId, string pluginId) =>
        new()
        {
            Game = new GameDefinition { GameId = gameId, DisplayName = gameId },
            PluginId = pluginId,
            InstallPath = _gameDir,
            IsValid = true
        };

    private static ModRelease MakeRelease(string pluginId, string gameId, string version, string zipPath) =>
        new()
        {
            GameId = gameId,
            PluginId = pluginId,
            Version = version,
            Channel = "stable",
            PackageUrl = new Uri("https://example.com/pkg.zip"),
            Sha256 = ComputeZipSha(zipPath)
        };

    private static string ComputeZipSha(string zipPath)
    {
        using var fs = File.OpenRead(zipPath);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
    }

    /// <summary>
    /// Creates a mod ZIP with manifest.json + files/ folder. <paramref name="files"/> are placed under files/.
    /// </summary>
    private string CreateMod(
        string pluginId,
        string gameId,
        string modVersion,
        (string relPath, string content)[] files,
        object[] actions,
        object[]? verify = null)
    {
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
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest);

        var manifestEntry = archive.CreateEntry("manifest.json");
        using (var w = new StreamWriter(manifestEntry.Open(), Encoding.UTF8))
            w.Write(manifestJson);

        foreach (var (rel, content) in files)
        {
            var e = archive.CreateEntry($"files/{rel}");
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }

        return zipPath;
    }

    /// <summary>
    /// Creates a mod ZIP with manifest.json that includes lifecycle scripts. Each provided
    /// script tuple is (filename, contents, what, why, modifies). Scripts are placed at the ZIP
    /// root (not under files/) so the staging-dir validation passes. Always uses .cmd payloads
    /// so the test can run on any Windows machine without PowerShell policy quirks.
    /// </summary>
    private string CreateModWithScripts(
        string pluginId,
        string gameId,
        string modVersion,
        (string fileName, string contents, string what, string why, string modifies)? preInstall,
        (string fileName, string contents, string what, string why, string modifies)? postInstall,
        (string fileName, string contents, string what, string why, string modifies)? postUninstall,
        (string relPath, string content)[] files,
        object[] actions,
        bool preInstallRunOnUpdate = false,
        bool postInstallRunOnUpdate = false,
        bool preInstallRunFromGameFolder = false,
        bool postInstallRunFromGameFolder = false)
    {
        var zipPath = Path.Combine(_tempRoot, $"pkg_{Guid.NewGuid():N}.zip");
        using var stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        // Manifest as a Dictionary so we can use camelCase keys with the polymorphic JSON
        // discriminator the parser expects.
        var manifestObj = new Dictionary<string, object?>
        {
            ["gameId"] = gameId,
            ["pluginId"] = pluginId,
            ["modVersion"] = modVersion,
            ["installActions"] = actions,
            ["verify"] = Array.Empty<object>(),
        };
        if (preInstall is { } pre)
        {
            manifestObj["preInstall"] = MakeScriptObj(pre.fileName, pre.what, pre.why, pre.modifies,
                preInstallRunOnUpdate, preInstallRunFromGameFolder);
            AddZipEntry(archive, pre.fileName, pre.contents);
        }
        if (postInstall is { } post)
        {
            manifestObj["postInstall"] = MakeScriptObj(post.fileName, post.what, post.why, post.modifies,
                postInstallRunOnUpdate, postInstallRunFromGameFolder);
            AddZipEntry(archive, post.fileName, post.contents);
        }
        if (postUninstall is { } postUn)
        {
            manifestObj["postUninstall"] = MakeScriptObj(postUn.fileName, postUn.what, postUn.why, postUn.modifies);
            AddZipEntry(archive, postUn.fileName, postUn.contents);
        }

        var manifestJson = JsonSerializer.Serialize(manifestObj);
        var manifestEntry = archive.CreateEntry("manifest.json");
        using (var w = new StreamWriter(manifestEntry.Open(), Encoding.UTF8))
            w.Write(manifestJson);

        foreach (var (rel, content) in files)
        {
            var e = archive.CreateEntry($"files/{rel}");
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }
        return zipPath;
    }

    private static object MakeScriptObj(
        string executable, string what, string why, string modifies,
        bool runOnUpdate = false, bool runFromGameFolder = false) =>
        new
        {
            executable,
            needsAdmin = false,
            failureFatal = true,
            what,
            why,
            modifies,
            runOnUpdate,
            runFromGameFolder
        };

    private static void AddZipEntry(ZipArchive archive, string path, string contents)
    {
        var e = archive.CreateEntry(path);
        // UTF-8 without BOM — cmd.exe reads scripts byte-for-byte and treats a BOM as part of
        // the first command name, breaking single-line scripts.
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        w.Write(contents);
    }

    /// <summary>
    /// Test double for IScriptHost: tracks every callback so a test can assert which hooks ran
    /// and how many times the user was asked to confirm.
    /// </summary>
    private sealed class RecordingScriptHost : IScriptHost
    {
        public bool ConfirmInstallResult { get; set; } = true;
        public bool ConfirmUninstallResult { get; set; } = true;
        public int InstallConfirmCount { get; private set; }
        public int UninstallConfirmCount { get; private set; }
        public List<string> StartingHooks { get; } = new();
        public List<string> OutputLines { get; } = new();
        public List<(int code, bool succeeded)> Finishes { get; } = new();

        public Task<bool> ConfirmInstallScriptsAsync(LifecycleScriptPrompt prompt, CancellationToken ct)
        {
            InstallConfirmCount++;
            return Task.FromResult(ConfirmInstallResult);
        }

        public Task<bool> ConfirmUninstallScriptAsync(LifecycleScriptPrompt prompt, CancellationToken ct)
        {
            UninstallConfirmCount++;
            return Task.FromResult(ConfirmUninstallResult);
        }

        public void OnScriptStarting(string hookLabel, string scriptName) => StartingHooks.Add(hookLabel);
        public void OnScriptOutputLine(string line) => OutputLines.Add(line);
        public void OnScriptFinished(int exitCode, bool succeeded) => Finishes.Add((exitCode, succeeded));
    }

    /// <summary>
    /// In-memory IReceiptStore so tests don't write to LocalAppData.
    /// </summary>
    private sealed class InMemoryReceiptStore : IReceiptStore
    {
        private readonly Dictionary<(string game, string plug), InstallReceipt> _store = new();

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

        public Task<List<InstallReceipt>> LoadAllForGameAsync(string gameId)
        {
            var list = _store
                .Where(kv => kv.Key.game == gameId)
                .Select(kv => kv.Value)
                .ToList();
            return Task.FromResult(list);
        }

        public Task<List<string>> UnreadablePluginIdsForGameAsync(string gameId) =>
            Task.FromResult(new List<string>());

        public Task<List<string>> InstalledPluginIdsAsync() =>
            Task.FromResult(new List<string>());

        public string GetReceiptDirectory(string gameId, string pluginId) =>
            Path.Combine(Path.GetTempPath(), "amm-test-receipts", pluginId, gameId);
    }

    /// <summary>
    /// In-memory dep receipt store. The current test suite doesn't exercise auto-install, so
    /// this is just satisfying the engine constructor.
    /// </summary>
    private sealed class InMemoryDependencyReceiptStore : IDependencyReceiptStore
    {
        private readonly Dictionary<(string game, string dep), DependencyReceipt> _store = new();

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

        public Task<bool> AnyUnreadableForGameAsync(string gameId) => Task.FromResult(false);

        public string GetBackupDirectory(string gameId, string dependencyId) =>
            Path.Combine(Path.GetTempPath(), "amm-test-depreceipts", gameId, dependencyId, "backup");
    }
}
