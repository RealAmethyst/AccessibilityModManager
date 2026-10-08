using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.PortableTests;

public sealed class PortableTarTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-tar-test-" + Guid.NewGuid().ToString("N"));
    private readonly Serilog.ILogger logger = new LoggerConfiguration().CreateLogger();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Portable_tar_installs_root_or_wrapped_layout_with_hash_and_executable_modes(bool wrapper, bool badHash)
    {
        if (!OperatingSystem.IsLinux()) return;
        var bytes = Archive(wrapper ? "BizHawk/" : "./");
        var dependency = Dependency(bytes, badHash);
        var game = Game(dependency);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "save.dat"), "keep my save");
        File.WriteAllText(Path.Combine(root, "data.txt"), "previous emulator file");
        using var http = new HttpClient(new Download(bytes));
        var installer = new NativeGameInstaller(new DependencyAutoInstaller(http,
            new DependencyReceiptStore(logger, Path.Combine(root, "receipts")), logger),
            new GameDependencyReceiptStore(Path.Combine(root, "game-receipts")));
        if (badHash)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(game, dependency, root, null));
            Assert.False(Directory.Exists(Path.Combine(root, "pokemon")));
            Assert.Equal("previous emulator file", File.ReadAllText(Path.Combine(root, "data.txt")));
            Assert.False(File.Exists(Path.Combine(root, "EmuHawkMono.sh")));
        }
        else
        {
            var installed = await installer.InstallAsync(game, dependency, root, null);
            Assert.Equal(root, installed);
            Assert.False(Directory.Exists(Path.Combine(root, "pokemon")));
            Assert.False(Directory.Exists(Path.Combine(root, "BizHawk")));
            Assert.True(new GameVerifier(logger).VerifyInstallPath(game, installed));
            var mode = File.GetUnixFileMode(Path.Combine(installed, "EmuHawkMono.sh"));
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
            Assert.Equal((UnixFileMode)0, mode & (UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit));
            Assert.True(File.GetUnixFileMode(Path.Combine(installed, "helper")).HasFlag(UnixFileMode.UserExecute));
            Assert.False(File.GetUnixFileMode(Path.Combine(installed, "data.txt")).HasFlag(UnixFileMode.UserExecute));
            var record = await new GameDependencyReceiptStore(Path.Combine(root, "game-receipts"))
                .LoadAsync(root, dependency.Id);
            Assert.NotNull(record);
            Assert.DoesNotContain("save.dat", record.Files.Keys);
            Assert.Contains(Directory.GetFiles(Path.Combine(root, "game-receipts"), "data.txt", SearchOption.AllDirectories),
                file => File.ReadAllText(file) == "previous emulator file");
            await installer.InstallAsync(game, dependency, root, null);
        }
        Assert.Equal("keep my save", File.ReadAllText(Path.Combine(root, "save.dat")));
        Assert.Empty(Directory.GetDirectories(root, "staging-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../escape", TarEntryType.RegularFile)]
    [InlineData("/absolute", TarEntryType.RegularFile)]
    [InlineData("folder/../escape", TarEntryType.RegularFile)]
    [InlineData("folder\\escape", TarEntryType.RegularFile)]
    [InlineData("link", TarEntryType.SymbolicLink)]
    [InlineData("hardlink", TarEntryType.HardLink)]
    [InlineData("device", TarEntryType.CharacterDevice)]
    [InlineData("fifo", TarEntryType.Fifo)]
    [InlineData("./EmuHawkMono.sh", TarEntryType.RegularFile)]
    public async Task Unsafe_entries_abort_and_remove_staging(string name, TarEntryType type)
    {
        if (!OperatingSystem.IsLinux()) return;
        var bytes = Archive("./", name, type);
        var dependency = Dependency(bytes);
        Directory.CreateDirectory(root);
        using var http = new HttpClient(new Download(bytes));
        var installer = new NativeGameInstaller(new DependencyAutoInstaller(http,
            new DependencyReceiptStore(logger, Path.Combine(root, "receipts")), logger),
            new GameDependencyReceiptStore(Path.Combine(root, "game-receipts")));
        await Assert.ThrowsAnyAsync<Exception>(() => installer.InstallAsync(Game(dependency), dependency, root, null));
        Assert.False(Directory.Exists(Path.Combine(root, "pokemon")));
        Assert.Empty(Directory.GetDirectories(root, ".amm-install-*"));
        Assert.False(File.Exists(Path.Combine(root, "escape")));
    }

    [Fact]
    public async Task Tar_limits_cancellation_and_existing_symlinks_are_enforced()
    {
        if (!OperatingSystem.IsLinux()) return;
        var bytes = Archive("./");
        using (var input = new MemoryStream(bytes))
            await Assert.ThrowsAsync<InvalidDataException>(() => new SafeTarGZipExtractor().ExtractAsync(input,
                Path.Combine(root, "limited"), maximumBytes: 1));
        using (var input = new MemoryStream(bytes))
            await Assert.ThrowsAsync<InvalidDataException>(() => new SafeTarGZipExtractor().ExtractAsync(input,
                Path.Combine(root, "entries"), maximumEntries: 1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using (var input = new MemoryStream(bytes))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SafeTarGZipExtractor().ExtractAsync(input,
                Path.Combine(root, "cancelled"), cancellation.Token));
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(root, "linked");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(Path.Combine(target, "BizHawk"), outside);
        using (var input = new MemoryStream(Archive("BizHawk/")))
            await Assert.ThrowsAnyAsync<Exception>(() => new SafeTarGZipExtractor().ExtractAsync(input, target));
        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public async Task Unsupported_or_truncated_archive_does_not_leave_an_installation()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var bytes in new[] { "not an archive"u8.ToArray(), Archive("./")[..50] })
        {
            var dep = Dependency(bytes);
            Directory.CreateDirectory(root);
            using var http = new HttpClient(new Download(bytes));
            var installer = new NativeGameInstaller(new DependencyAutoInstaller(http,
                new DependencyReceiptStore(logger, Path.Combine(root, "receipts")), logger),
            new GameDependencyReceiptStore(Path.Combine(root, "game-receipts")));
            await Assert.ThrowsAnyAsync<Exception>(() => installer.InstallAsync(Game(dep), dep, root, null));
            Assert.False(Directory.Exists(Path.Combine(root, "pokemon")));
            Assert.Empty(Directory.GetDirectories(root, ".amm-install-*"));
        }
    }

    private static Dependency Dependency(byte[] bytes, bool badHash = false) => new()
    {
        Id = "bizhawk-linux", Type = "system", IsGameInstaller = true, TargetPlatforms = ["linux"],
        Fix = new DependencyFix { DownloadUrl = "https://example.invalid/download-without-extension",
            AutoInstall = new ExtractAppAutoInstall { Sha256 = badHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)) } }
    };
    private static GameDefinition Game(Dependency dependency) => new()
    {
        GameId = "pokemon", DisplayName = "Pokemon", ExeName = "EmuHawk.exe", LinuxExeName = "EmuHawkMono.sh", Dependencies = [dependency]
    };
    private static byte[] Archive(string prefix, string? extraName = null, TarEntryType extraType = TarEntryType.RegularFile)
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, prefix));
            foreach (var name in new[] { "EmuHawkMono.sh", "EmuHawk.exe", "helper", "data.txt" })
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, prefix + name)
                {
                    DataStream = new MemoryStream("offline fixture, never executed"u8.ToArray()),
                    Mode = name is "EmuHawkMono.sh" or "helper" ? (UnixFileMode)0xFED : (UnixFileMode)0x1A4
                });
            if (extraName is not null)
            {
                var entry = new PaxTarEntry(extraType, extraName);
                if (extraType == TarEntryType.RegularFile) entry.DataStream = new MemoryStream("bad"u8.ToArray());
                if (extraType is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = "../outside";
                tar.WriteEntry(entry);
            }
        }
        return bytes.ToArray();
    }
    private sealed class Download(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
