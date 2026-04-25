using System.IO.Compression;
using System.Text;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
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

        _engine = new InstallerEngine(
            backupManager, actionExecutor, verifier, manifestParser, zipExtractor,
            _receiptStore, dependencyChecker, logger);
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

        var receipt = await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0"), zip);

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
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0"), zip));
    }

    [Fact]
    public async Task InstallAsync_MismatchedPluginId_Throws()
    {
        var zip = CreateMod("wrong-plug", "game-1", "1.0.0",
            files: new[] { ("a.txt", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "a.txt", target = "a.txt" } });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0"), zip));
    }

    [Fact]
    public async Task InstallAsync_AlreadyInstalledByOwnPlugin_Throws()
    {
        // First install
        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("a.txt", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "a.txt", target = "a.txt" } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0"), zip);

        // Second install of the same plugin should refuse
        var zip2 = CreateMod("plug-a", "game-1", "1.1.0",
            files: new[] { ("a.txt", "y") },
            actions: new[] { (object)new { type = "copyFile", source = "a.txt", target = "a.txt" } });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.1.0"), zip2));
    }

    [Fact]
    public async Task InstallAsync_FileCollisionWithOtherPlugin_Throws()
    {
        // plug-a installs file
        var zipA = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("shared.dll", "a") },
            actions: new[] { (object)new { type = "copyFile", source = "shared.dll", target = "shared.dll" } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0"), zipA);

        // plug-b tries to touch the same file
        var zipB = CreateMod("plug-b", "game-1", "1.0.0",
            files: new[] { ("shared.dll", "b") },
            actions: new[] { (object)new { type = "copyFile", source = "shared.dll", target = "shared.dll" } });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-b"), MakeRelease("plug-b", "game-1", "1.0.0"), zipB));
        Assert.Contains("conflict", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UninstallAsync_RemovesAddedFilesAndReceipt()
    {
        var zip = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("mods/added.dll", "x") },
            actions: new[] { (object)new { type = "copyFile", source = "mods/added.dll", target = "mods/added.dll" } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0"), zip);
        Assert.True(File.Exists(Path.Combine(_gameDir, "mods", "added.dll")));

        await _engine.UninstallAsync(MakeGameInstall("game-1", "plug-a"), "plug-a");

        Assert.False(File.Exists(Path.Combine(_gameDir, "mods", "added.dll")));
        Assert.Null(await _receiptStore.LoadAsync("game-1", "plug-a"));
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
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0"), zip);
        Assert.Equal("MODDED", File.ReadAllText(Path.Combine(_gameDir, "config", "settings.cfg")));

        await _engine.UninstallAsync(MakeGameInstall("game-1", "plug-a"), "plug-a");

        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(_gameDir, "config", "settings.cfg")));
    }

    [Fact]
    public async Task UpdateAsync_ReplacesPreviousInstall()
    {
        var zip1 = CreateMod("plug-a", "game-1", "1.0.0",
            files: new[] { ("mod.dll", "v1") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });
        await _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0"), zip1);

        var zip2 = CreateMod("plug-a", "game-1", "1.1.0",
            files: new[] { ("mod.dll", "v2") },
            actions: new[] { (object)new { type = "copyFile", source = "mod.dll", target = "mod.dll" } });
        var receipt = await _engine.UpdateAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.1.0"), zip2);

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
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0"), zip));

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
            _engine.InstallAsync(gameInstall, MakeRelease("plug-a", "game-1", "1.0.0"), zip));

        // No file should have been written, no receipt stored.
        Assert.False(File.Exists(Path.Combine(_gameDir, "mods", "added.dll")));
        Assert.Null(await _receiptStore.LoadAsync("game-1", "plug-a"));
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

        var receipt = await _engine.InstallAsync(gameInstall, MakeRelease("plug-a", "game-1", "1.0.0"), zip);
        Assert.Equal("1.0.0", receipt.InstalledVersion);
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
            _engine.InstallAsync(MakeGameInstall("game-1", "plug-a"), MakeRelease("plug-a", "game-1", "1.0.0"), zipPath));
    }

    private GameInstall MakeGameInstall(string gameId, string pluginId) =>
        new()
        {
            Game = new GameDefinition { GameId = gameId, DisplayName = gameId },
            PluginId = pluginId,
            InstallPath = _gameDir,
            IsValid = true
        };

    private static ModRelease MakeRelease(string pluginId, string gameId, string version) =>
        new()
        {
            GameId = gameId,
            PluginId = pluginId,
            Version = version,
            Channel = "stable",
            PackageUrl = new Uri("https://example.com/pkg.zip"),
            Sha256 = "00"
        };

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
    }
}
