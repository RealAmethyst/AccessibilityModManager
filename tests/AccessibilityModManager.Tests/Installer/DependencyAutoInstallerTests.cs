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
    public async Task InstallAsync_StaleReceiptButFilesMissing_Reinstalls()
    {
        // Regression: a receipt existed but its files were wiped (e.g. a game update/repair, or a
        // manual cleanup during testing). The installer must re-extract instead of trusting the
        // stale receipt — otherwise the mod installs but the loader (MelonLoader) is silently absent.
        var zipPath = Path.Combine(_tempRoot, "loader.zip");
        using (var fs = File.Create(zipPath))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(archive.CreateEntry("MelonLoader/dep.dll").Open());
            w.Write("fresh-loader");
        }
        var sha = ComputeSha(zipPath);

        // Receipt claims MelonLoader/dep.dll was added — but the file is NOT on disk.
        await _store.SaveAsync(new DependencyReceipt
        {
            GameId = "game-1",
            DependencyId = "melonloader",
            Kind = "extractZip",
            InstalledAt = DateTime.UtcNow,
            Sha256 = sha,
            Changes = new List<FileChange>
            {
                new() { Type = ChangeType.Added, RelativePath = Path.Combine("MelonLoader", "dep.dll") }
            },
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
                AutoInstall = new ExtractZipAutoInstall { Sha256 = sha }
            }
        };

        var localInstaller = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(zipPath)), _store, TestLogger.Create());

        var result = await localInstaller.InstallAsync(dep, MakeGame(), "plug-b", host: null, ct: CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        // It actually re-extracted the missing file instead of short-circuiting on the receipt.
        Assert.Equal("fresh-loader", File.ReadAllText(Path.Combine(_gameDir, "MelonLoader", "dep.dll")));
        // Refcount preserved the prior dependent and added the new one.
        Assert.Contains("plug-a", result.Receipt!.DependentPluginIds);
        Assert.Contains("plug-b", result.Receipt.DependentPluginIds);
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

    [Fact]
    public async Task ExtractPortableAppAsync_ExtractsZipIntoDestination()
    {
        // A portable emulator ZIP with the exe at top level (F4) plus a sub-folder file.
        var zipPath = Path.Combine(_tempRoot, "emu.zip");
        using (var fs = File.Create(zipPath))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("emulator.exe").Open())) w.Write("EMU");
            using (var w2 = new StreamWriter(archive.CreateEntry("data/readme.txt").Open())) w2.Write("hi");
        }
        var sha = ComputeSha(zipPath);

        var dep = new Dependency
        {
            Id = "myemu",
            Type = "system",
            IsGameInstaller = true,
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.invalid/emu.zip",
                AutoInstall = new ExtractAppAutoInstall { Sha256 = sha }
            }
        };

        var dest = Path.Combine(_tempRoot, "install-here");
        var installer = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(zipPath)), _store, TestLogger.Create());

        await installer.ExtractPortableAppAsync(dep, dest, host: null, ct: CancellationToken.None);

        Assert.Equal("EMU", File.ReadAllText(Path.Combine(dest, "emulator.exe")));
        Assert.Equal("hi", File.ReadAllText(Path.Combine(dest, "data", "readme.txt")));
    }

    [Fact]
    public async Task ExtractPortableAppAsync_Sha256Mismatch_ThrowsAndExtractsNothing()
    {
        var zipPath = Path.Combine(_tempRoot, "emu.zip");
        using (var fs = File.Create(zipPath))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(archive.CreateEntry("emulator.exe").Open());
            w.Write("EMU");
        }

        var dep = new Dependency
        {
            Id = "myemu",
            Type = "system",
            IsGameInstaller = true,
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.invalid/emu.zip",
                AutoInstall = new ExtractAppAutoInstall
                {
                    Sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
                }
            }
        };

        var dest = Path.Combine(_tempRoot, "install-here");
        var installer = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(zipPath)), _store, TestLogger.Create());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.ExtractPortableAppAsync(dep, dest, host: null, ct: CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(dest, "emulator.exe")));
    }

    [Fact]
    public async Task ExtractPortableAppAsync_NonHttpsUrl_Rejected()
    {
        // HTTPS is a hard gate — an http:// URL must be refused before anything downloads. Uses the
        // StubHandler installer, so if the gate somehow didn't fire we'd get a "reached network"
        // error instead — either way it throws and nothing is placed.
        var dep = new Dependency
        {
            Id = "myemu",
            Type = "system",
            IsGameInstaller = true,
            Fix = new DependencyFix
            {
                DownloadUrl = "http://example.invalid/emu.zip",
                AutoInstall = new ExtractAppAutoInstall { Sha256 = "deadbeef" }
            }
        };

        var dest = Path.Combine(_tempRoot, "install-here");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _installer.ExtractPortableAppAsync(dep, dest, host: null, ct: CancellationToken.None));
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public async Task InstallAsync_TargetDirWithLeadingAndTrailingSlash_ExtractsInsideGame()
    {
        // THE live Pokemon TCG regression: an author-written targetDir of "/Updater/1.5.0/" used
        // to fail with "resolves outside the game folder" (leading slash made Path.Combine discard
        // the game dir). It must now mean <game>\Updater\1.5.0.
        var zipPath = MakeLoaderZip("MelonLoader/dep.dll", "loader-bits");
        var dep = MakeExtractZipDep(zipPath, targetDir: "/Updater/1.5.0/");

        var installer = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(zipPath)), _store, TestLogger.Create());
        var result = await installer.InstallAsync(dep, MakeGame(), "plug-a", host: null, ct: CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("loader-bits",
            File.ReadAllText(Path.Combine(_gameDir, "Updater", "1.5.0", "MelonLoader", "dep.dll")));
    }

    [Fact]
    public async Task InstallAsync_TargetDirWithTrailingSlashOnly_NoFalseZipSlip()
    {
        // Second half of the same regression: with only a trailing slash the resolved target kept
        // its separator and the zip-slip prefix check built a doubled separator that no entry could
        // match — every file failed as a false "Zip slip detected".
        var zipPath = MakeLoaderZip("MelonLoader/dep.dll", "loader-bits");
        var dep = MakeExtractZipDep(zipPath, targetDir: "Updater/1.5.0/");

        var installer = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(zipPath)), _store, TestLogger.Create());
        var result = await installer.InstallAsync(dep, MakeGame(), "plug-a", host: null, ct: CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(_gameDir, "Updater", "1.5.0", "MelonLoader", "dep.dll")));
    }

    [Fact]
    public async Task InstallAsync_AbsoluteTargetDir_FailsWithClearMessage()
    {
        var zipPath = MakeLoaderZip("a.dll", "x");
        var dep = MakeExtractZipDep(zipPath, targetDir: "C:\\evil");

        var installer = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(zipPath)), _store, TestLogger.Create());
        var result = await installer.InstallAsync(dep, MakeGame(), "plug-a", host: null, ct: CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("relative to the game folder", result.ErrorMessage);
        Assert.Null(await _store.LoadAsync("game-1", "melonloader"));
    }

    [Fact]
    public async Task InstallAsync_TraversalTargetDir_Fails()
    {
        var zipPath = MakeLoaderZip("a.dll", "x");
        var dep = MakeExtractZipDep(zipPath, targetDir: "..\\out");

        var installer = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(zipPath)), _store, TestLogger.Create());
        var result = await installer.InstallAsync(dep, MakeGame(), "plug-a", host: null, ct: CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("..", result.ErrorMessage);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "out")));
    }

    [Fact]
    public async Task InstallAsync_CopyFile_TargetDirSlashNoise_PlacesFileInsideGame()
    {
        var payloadPath = Path.Combine(_tempRoot, "tool.dll");
        File.WriteAllText(payloadPath, "tool-bits");
        var dep = new Dependency
        {
            Id = "tool",
            Type = "framework",
            Required = true,
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.invalid/tool.dll",
                AutoInstall = new CopyFileAutoInstall { Sha256 = ComputeSha(payloadPath), TargetDir = "/Tools/" }
            }
        };

        var installer = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(payloadPath)), _store, TestLogger.Create());
        var result = await installer.InstallAsync(dep, MakeGame(), "plug-a", host: null, ct: CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("tool-bits", File.ReadAllText(Path.Combine(_gameDir, "Tools", "tool.dll")));
    }

    [Fact]
    public async Task InstallAsync_CopyFile_TargetFileNameWithFolders_Fails()
    {
        var payloadPath = Path.Combine(_tempRoot, "tool.dll");
        File.WriteAllText(payloadPath, "tool-bits");
        var dep = new Dependency
        {
            Id = "tool",
            Type = "framework",
            Required = true,
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.invalid/tool.dll",
                AutoInstall = new CopyFileAutoInstall
                {
                    Sha256 = ComputeSha(payloadPath),
                    TargetFileName = "sub\\evil.dll"
                }
            }
        };

        var installer = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(payloadPath)), _store, TestLogger.Create());
        var result = await installer.InstallAsync(dep, MakeGame(), "plug-a", host: null, ct: CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("plain file name", result.ErrorMessage);
    }

    [Fact]
    public async Task InstallAsync_ZipWithTraversalEntry_FailsAndWritesNothingOutside()
    {
        // The lenient targetDir handling must not loosen real zip-slip protection on the
        // dependency extractor: an archive entry that climbs out of the target still aborts.
        var zipPath = MakeLoaderZip("../evil.txt", "escape");
        var dep = MakeExtractZipDep(zipPath, targetDir: "Loader");

        var installer = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(zipPath)), _store, TestLogger.Create());
        var result = await installer.InstallAsync(dep, MakeGame(), "plug-a", host: null, ct: CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Zip slip", result.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(_gameDir, "evil.txt")));
    }

    [Fact]
    public async Task InstallAsync_ZipWithRootedEntry_Fails()
    {
        // A rooted entry name makes Path.Combine discard the target dir entirely — containment
        // must still catch the resolved path landing outside.
        var zipPath = MakeLoaderZip("C:\\evil\\payload.txt", "escape");
        var dep = MakeExtractZipDep(zipPath, targetDir: "Loader");

        var installer = new DependencyAutoInstaller(
            new HttpClient(new ServeFileHandler(zipPath)), _store, TestLogger.Create());
        var result = await installer.InstallAsync(dep, MakeGame(), "plug-a", host: null, ct: CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Zip slip", result.ErrorMessage);
    }

    private string MakeLoaderZip(string entryName, string content)
    {
        var zipPath = Path.Combine(_tempRoot, $"loader_{Guid.NewGuid():N}.zip");
        using (var fs = File.Create(zipPath))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(archive.CreateEntry(entryName).Open());
            w.Write(content);
        }
        return zipPath;
    }

    private Dependency MakeExtractZipDep(string zipPath, string targetDir) => new()
    {
        Id = "melonloader",
        Type = "framework",
        Required = true,
        Fix = new DependencyFix
        {
            DownloadUrl = "https://example.invalid/loader.zip",
            AutoInstall = new ExtractZipAutoInstall { Sha256 = ComputeSha(zipPath), TargetDir = targetDir }
        }
    };

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
