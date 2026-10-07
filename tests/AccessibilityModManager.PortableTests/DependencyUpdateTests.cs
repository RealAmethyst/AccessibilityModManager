using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.PortableTests;

public sealed class DependencyUpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-dependency-updates-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger log = new LoggerConfiguration().CreateLogger();

    [Theory]
    [InlineData("windows")]
    [InlineData("linux")]
    public async Task Play_check_uses_receipts_without_downloading_and_url_or_hash_change_is_an_update(string target)
    {
        var first = Zip(("emulator", "first"), ("old-library", "old"));
        var second = Zip(("wrapped/emulator", "second"), ("wrapped/new-library", "new"));
        using var downloads = new Downloads { Bytes = first };
        using var http = new HttpClient(downloads);
        var store = new GameDependencyReceiptStore(Path.Combine(root, "records"));
        var service = Service(http, store);
        var one = Dep(first, target);
        var game = Game(one);
        Directory.CreateDirectory(game.InstallPath);
        Assert.True(Assert.Single(await service.FindAsync(game, target)).PreviouslyUntracked);
        Assert.Equal(0, downloads.Calls);
        await service.UpdatePortableAsync(game, one, target, null, null, default);
        Assert.Empty(await service.FindAsync(game, target));
        Assert.Equal(1, downloads.Calls);
        await File.WriteAllTextAsync(Path.Combine(game.InstallPath, "save.sav"), "precious save");
        await File.WriteAllTextAsync(Path.Combine(game.InstallPath, "mod.lua"), "installed mod");
        var mirror = Dep(first, target, url: "https://example.invalid/other-link.zip");
        Assert.Single(await service.FindAsync(Game(mirror), target));
        Assert.Equal(1, downloads.Calls);
        downloads.Bytes = second;
        var two = Dep(second, target);
        var updatedGame = Game(two);
        var pending = await service.FindAsync(updatedGame, target);
        Assert.Single(pending);
        Assert.Equal(1, downloads.Calls);
        await service.ApplyAsync(updatedGame, target, pending, null, null, default);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(game.InstallPath, "emulator")));
        Assert.Equal("precious save", await File.ReadAllTextAsync(Path.Combine(game.InstallPath, "save.sav")));
        Assert.Equal("installed mod", await File.ReadAllTextAsync(Path.Combine(game.InstallPath, "mod.lua")));
        Assert.False(File.Exists(Path.Combine(game.InstallPath, "old-library")));
        Assert.True(File.Exists(Path.Combine(game.InstallPath, "new-library")));
        Assert.Empty(await service.FindAsync(updatedGame, target));
        Assert.Equal(2, downloads.Calls);
        var backups = Directory.GetDirectories(store.DirectoryFor(game.InstallPath, two.Id), "backup-*");
        Assert.Contains(backups, path => File.Exists(Path.Combine(path, "files", "emulator")) &&
            File.ReadAllText(Path.Combine(path, "files", "emulator")) == "first");
        if (OperatingSystem.IsLinux() && target == "linux") Assert.True(File.GetUnixFileMode(Path.Combine(game.InstallPath, "emulator")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Failed_hash_or_unsafe_archive_leaves_installed_files_and_receipt_untouched()
    {
        var first = Zip(("emulator", "first"));
        using var downloads = new Downloads { Bytes = first };
        using var http = new HttpClient(downloads);
        var store = new GameDependencyReceiptStore(Path.Combine(root, "records"));
        var service = Service(http, store);
        var one = Dep(first, "linux");
        var game = Game(one);
        Directory.CreateDirectory(game.InstallPath);
        await service.UpdatePortableAsync(game, one, "linux", null, null, default);
        var receipt = await store.LoadAsync(game.InstallPath, one.Id);
        var bad = Dep(Zip(("emulator", "expected")), "linux");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.UpdatePortableAsync(Game(bad), bad, "linux", null, null, default));
        downloads.Bytes = Zip(("emulator", "bad"), ("../escape", "bad"));
        var unsafeDep = Dep(downloads.Bytes, "linux");
        await Assert.ThrowsAnyAsync<Exception>(() => service.UpdatePortableAsync(Game(unsafeDep), unsafeDep, "linux", null, null, default));
        Assert.Equal("first", File.ReadAllText(Path.Combine(game.InstallPath, "emulator")));
        Assert.Equal(receipt!.Sha256, (await store.LoadAsync(game.InstallPath, one.Id))!.Sha256);
        Assert.Empty(await service.FindAsync(game, "linux"));
        Assert.False(File.Exists(Path.Combine(root, "escape")));
    }

    [Fact]
    public async Task Platform_filter_does_not_offer_windows_emulator_on_linux_and_modified_obsolete_file_is_kept()
    {
        var first = Zip(("emulator", "first"), ("settings.cfg", "default"));
        using var downloads = new Downloads { Bytes = first };
        using var http = new HttpClient(downloads);
        var service = Service(http, new GameDependencyReceiptStore(Path.Combine(root, "records")));
        Assert.Empty(await service.FindAsync(Game(Dep(first, "windows")), "linux"));
        var one = Dep(first, "linux");
        var game = Game(one);
        Directory.CreateDirectory(game.InstallPath);
        await service.UpdatePortableAsync(game, one, "linux", null, null, default);
        await File.WriteAllTextAsync(Path.Combine(game.InstallPath, "settings.cfg"), "my settings");
        downloads.Bytes = Zip(("emulator", "second"));
        var two = Dep(downloads.Bytes, "linux");
        await service.UpdatePortableAsync(Game(two), two, "linux", null, null, default);
        Assert.Equal("my settings", File.ReadAllText(Path.Combine(game.InstallPath, "settings.cfg")));
    }

    [Fact]
    public async Task Managed_loader_updates_despite_existing_check_file_and_failed_replacement_restores_previous_loader()
    {
        var first = Zip(("loader.dll", "one"));
        using var downloads = new Downloads { Bytes = first };
        using var http = new HttpClient(downloads);
        var receipts = new DependencyReceiptStore(log, Path.Combine(root, "loaders"));
        var installer = new DependencyAutoInstaller(http, receipts, log);
        var updater = new DependencyUpdates(installer, log, new GameDependencyReceiptStore(Path.Combine(root, "records")));
        Dependency Loader(byte[] bytes, string url = "https://example.invalid/loader.zip") => new()
        {
            Id = "loader", Type = "framework", TargetPlatforms = ["linux"], Check = new() { FilePath = "loader.dll" },
            Fix = new() { DownloadUrl = url, AutoInstall = new ExtractZipAutoInstall { Sha256 = Hash(bytes) } }
        };
        var one = Loader(first);
        var game = Game(one);
        Directory.CreateDirectory(game.InstallPath);
        Assert.True((await installer.InstallAsync(one, game, "test", null, default)).Succeeded);
        Assert.Empty(await updater.FindAsync(game, "linux"));
        Assert.Single(await updater.FindAsync(Game(Loader(first, "https://example.invalid/mirror.zip")), "linux"));
        downloads.Bytes = Zip(("loader.dll", "two"));
        var two = Loader(downloads.Bytes);
        var secondGame = Game(two);
        await updater.ApplyAsync(secondGame, "linux", await updater.FindAsync(secondGame, "linux"), null, null, default);
        Assert.Equal("two", File.ReadAllText(Path.Combine(game.InstallPath, "loader.dll")));
        Assert.Empty(await updater.FindAsync(secondGame, "linux"));
        // A valid SHA with an invalid ZIP fails after the old version was removed. Restore it.
        downloads.Bytes = "invalid zip"u8.ToArray();
        var broken = Loader(downloads.Bytes);
        var brokenGame = Game(broken);
        await Assert.ThrowsAsync<IOException>(() => updater.ApplyAsync(brokenGame, "linux",
            new[] { new DependencyUpdate(broken, false) }, null, null, default));
        Assert.Equal("two", File.ReadAllText(Path.Combine(game.InstallPath, "loader.dll")));
        Assert.Equal(two.Fix!.AutoInstall!.Sha256, (await receipts.LoadAsync("test", "loader"))!.Sha256);
    }

    [Fact]
    public async Task Interrupted_portable_update_restores_prior_files_and_receipt_before_offline_play()
    {
        var bytes = Zip(("emulator", "original"));
        using var downloads = new Downloads { Bytes = bytes };
        using var http = new HttpClient(downloads);
        var store = new GameDependencyReceiptStore(Path.Combine(root, "records"));
        var service = Service(http, store);
        var dep = Dep(bytes, "linux");
        var game = Game(dep);
        Directory.CreateDirectory(game.InstallPath);
        await service.UpdatePortableAsync(game, dep, "linux", null, null, default);
        var prior = await store.LoadAsync(game.InstallPath, dep.Id);
        var state = store.DirectoryFor(game.InstallPath, dep.Id);
        Directory.CreateDirectory(Path.Combine(state, "backup-interrupted", "files"));
        File.Copy(Path.Combine(game.InstallPath, "emulator"), Path.Combine(state, "backup-interrupted", "files", "emulator"));
        await File.WriteAllTextAsync(Path.Combine(game.InstallPath, "emulator"), "partially updated");
        await File.WriteAllTextAsync(Path.Combine(game.InstallPath, "new-file"), "partial addition");
        await store.SaveAsync(prior! with { Sha256 = new string('f', 64) });
        var payload = JsonSerializer.Serialize(new
        {
            Backup = "backup-interrupted", Files = new Dictionary<string, bool> { ["emulator"] = true, ["new-file"] = false },
            PreviousReceipt = prior
        });
        var wrapped = "{\"formatVersion\":2,\"sha256\":\"" + Hash(Encoding.UTF8.GetBytes(payload)) + "\",\"payload\":" + payload + "}";
        await File.WriteAllTextAsync(Path.Combine(state, "pending.json"), wrapped);
        await service.EnsureReadyToLaunchAsync(game, "linux");
        Assert.Equal("original", File.ReadAllText(Path.Combine(game.InstallPath, "emulator")));
        Assert.False(File.Exists(Path.Combine(game.InstallPath, "new-file")));
        Assert.Equal(prior!.Sha256, (await store.LoadAsync(game.InstallPath, dep.Id))!.Sha256);
        Assert.False(File.Exists(Path.Combine(state, "pending.json")));
        Assert.Equal(1, downloads.Calls);
        Assert.Empty(await service.FindAsync(game, "linux"));
    }

    private DependencyUpdates Service(HttpClient http, GameDependencyReceiptStore records) => new(
        new DependencyAutoInstaller(http, new DependencyReceiptStore(log, Path.Combine(root, "loaders")), log), log, records);
    private GameInstall Game(Dependency dependency) => new()
    {
        Game = new GameDefinition { GameId = "test", DisplayName = "Test", LinuxExeName = "emulator", ExeName = "emulator", Dependencies = [dependency] },
        PluginId = "test", InstallPath = Path.Combine(root, "game")
    };
    private static Dependency Dep(byte[] bytes, string target, string url = "https://example.invalid/emulator.zip") => new()
    {
        Id = "emulator", Type = "system", IsGameInstaller = true, TargetPlatforms = [target],
        Fix = new() { DownloadUrl = url, AutoInstall = new ExtractAppAutoInstall { Sha256 = Hash(bytes) } }
    };
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open());
                writer.Write(file.Text);
            }
        return buffer.ToArray();
    }
    private sealed class Downloads : HttpMessageHandler
    {
        public required byte[] Bytes { get; set; }
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
