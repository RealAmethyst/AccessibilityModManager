using System.IO.Compression;
using System.Net;
using System.Net.Http;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Installer;

public class DependencyAutoInstallerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _gameDir;
    private readonly InMemoryDepReceiptStore _store;
    private readonly DependencyAutoInstaller _installer;

    public DependencyAutoInstallerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_depauto_" + Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_tempRoot, "game");
        Directory.CreateDirectory(_gameDir);

        _store = new InMemoryDepReceiptStore(Path.Combine(_tempRoot, "depbackup"));
        _installer = new DependencyAutoInstaller(
            new HttpClient(new StubHandler()), _store, TestLogger.Create());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task InstallAsync_AlreadyInstalledByOtherPlugin_BumpsRefcount_NoDownload()
    {
        // Pre-seed a receipt so the installer should short-circuit and just append to refcount.
        await _store.SaveAsync(new DependencyReceipt
        {
            GameId = "game-1",
            DependencyId = "melonloader",
            Kind = "extractZip",
            InstalledAt = DateTime.UtcNow,
            Sha256 = "deadbeef",
            Changes = new List<FileChange>(),
            BackupFolder = _store.GetBackupDirectory("game-1", "melonloader"),
            DependentPluginIds = new List<string> { "plug-a" }
        });

        var dep = new Dependency
        {
            Id = "melonloader",
            Type = "framework",
            Required = true,
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.invalid/loader.zip",
                AutoInstall = new ExtractZipAutoInstall { Sha256 = "deadbeef" }
            }
        };

        var result = await _installer.InstallAsync(dep, MakeGame(), "plug-b", host: null, ct: CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Receipt);
        Assert.Contains("plug-a", result.Receipt!.DependentPluginIds);
        Assert.Contains("plug-b", result.Receipt.DependentPluginIds);
    }

    [Fact]
    public async Task InstallAsync_AlreadyInstalledBySamePlugin_DoesNotDuplicateRefcount()
    {
        await _store.SaveAsync(new DependencyReceipt
        {
            GameId = "game-1",
            DependencyId = "melonloader",
            Kind = "extractZip",
            InstalledAt = DateTime.UtcNow,
            Sha256 = "deadbeef",
            Changes = new List<FileChange>(),
            BackupFolder = _store.GetBackupDirectory("game-1", "melonloader"),
            DependentPluginIds = new List<string> { "plug-a" }
        });

        var dep = new Dependency
        {
            Id = "melonloader",
            Type = "framework",
            Required = true,
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.invalid/loader.zip",
                AutoInstall = new ExtractZipAutoInstall { Sha256 = "deadbeef" }
            }
        };

        var result = await _installer.InstallAsync(dep, MakeGame(), "plug-a", host: null, ct: CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(result.Receipt!.DependentPluginIds);
    }

    [Fact]
    public async Task InstallAsync_ExtractsZipToTargetDir_BackupsExistingFiles()
    {
        // Build a real ZIP with known content + SHA so the installer's full path runs.
        var zipPath = Path.Combine(_tempRoot, "loader.zip");
        using (var fs = File.Create(zipPath))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(archive.CreateEntry("MelonLoader/dep.dll").Open());
            w.Write("modded-loader-content");
        }
        var sha = ComputeSha(zipPath);

        // Seed a colliding file so the backup-on-conflict path runs.
        var collidingPath = Path.Combine(_gameDir, "MelonLoader", "dep.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(collidingPath)!);
        File.WriteAllText(collidingPath, "user-original");

        var fileUri = new Uri(zipPath).AbsoluteUri;
        var dep = new Dependency
        {
            Id = "melonloader",
            Type = "framework",
            Required = true,
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.invalid/loader.zip",
                AutoInstall = new ExtractZipAutoInstall { Sha256 = sha }
            }
        };

        // Re-create the installer with a handler that serves our local file as the URL response.
        var localServingInstaller = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(zipPath)),
            _store, TestLogger.Create());

        var result = await localServingInstaller.InstallAsync(
            dep, MakeGame(), "plug-a", host: null, ct: CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("modded-loader-content", File.ReadAllText(collidingPath));
        // Backup of the user's original now lives next to the receipt.
        var backedUp = Path.Combine(_store.GetBackupDirectory("game-1", "melonloader"), "MelonLoader", "dep.dll");
        Assert.True(File.Exists(backedUp));
        Assert.Equal("user-original", File.ReadAllText(backedUp));
    }

    [Fact]
    public async Task InstallAsync_Sha256Mismatch_FailsAndDoesNotWriteReceipt()
    {
        var zipPath = Path.Combine(_tempRoot, "loader.zip");
        using (var fs = File.Create(zipPath))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(archive.CreateEntry("a.dll").Open());
            w.Write("x");
        }

        var dep = new Dependency
        {
            Id = "bad-loader",
            Type = "framework",
            Required = true,
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.invalid/loader.zip",
                AutoInstall = new ExtractZipAutoInstall
                {
                    Sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
                }
            }
        };

        var localInstaller = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(zipPath)),
            _store, TestLogger.Create());

        var result = await localInstaller.InstallAsync(
            dep, MakeGame(), "plug-a", host: null, ct: CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(await _store.LoadAsync("game-1", "bad-loader"));
    }

    private GameInstall MakeGame() => new()
    {
        Game = new GameDefinition { GameId = "game-1", DisplayName = "Game 1" },
        PluginId = "plug-a",
        InstallPath = _gameDir,
        IsValid = true
    };

    private static string ComputeSha(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        var hash = System.Security.Cryptography.SHA256.HashData(fs);
        return Convert.ToHexStringLower(hash);
    }

    private sealed class InMemoryDepReceiptStore : IDependencyReceiptStore
    {
        private readonly Dictionary<(string game, string dep), DependencyReceipt> _store = new();
        private readonly string _backupRoot;

        public InMemoryDepReceiptStore(string backupRoot)
        {
            _backupRoot = backupRoot;
        }

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

        public string GetBackupDirectory(string gameId, string dependencyId) =>
            Path.Combine(_backupRoot, gameId, dependencyId);
    }

    /// <summary>
    /// Stub handler: refuses every request. Used by tests that should never hit the network
    /// (e.g. refcount short-circuit).
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Test should not have reached the network.");
    }

    /// <summary>
    /// Serves a local file for any URL. Lets the tests exercise the real download +
    /// SHA-verify + extract path with deterministic content.
    /// </summary>
    private sealed class ServeFileHandler : HttpMessageHandler
    {
        private readonly string _filePath;
        public ServeFileHandler(string filePath) { _filePath = filePath; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = File.ReadAllBytes(_filePath);
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            return Task.FromResult(resp);
        }
    }
}
