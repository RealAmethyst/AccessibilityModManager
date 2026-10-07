using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.PortableTests;

public sealed class LinuxApplicationInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-installer-test-" + Guid.NewGuid().ToString("N"));
    private static LinuxApplication App => LinuxApplication.Manager;
    private string Package(string version, string marker)
    {
        var path = Path.Combine(root, "source-" + version);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "release.json"), JsonSerializer.Serialize(new LinuxPackageMetadata(App.Id, version, "linux-x64")));
        foreach (var name in new[] { App.Executable, App.Executable + ".dll", "libhostfxr.so", "libcoreclr.so" })
            File.WriteAllText(Path.Combine(path, name), marker);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(path, App.Executable),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void FirstInstallAndUpgradePreserveDataAndBackUpOnlyApplication()
    {
        if (!OperatingSystem.IsLinux()) return;
        var data = Path.Combine(root, "home with spaces $test");
        var first = LinuxApplicationInstaller.Install(Package("1.19.0", "old"), App, data);
        Assert.Null(first.BackupDirectory);
        var config = Path.Combine(data, App.Id, "config.json");
        File.WriteAllText(config, "keep user settings and receipts");
        var second = LinuxApplicationInstaller.Install(Package("1.20.0", "new"), App, data);
        Assert.Equal("new", File.ReadAllText(second.Executable));
        Assert.Equal("old", File.ReadAllText(Path.Combine(second.BackupDirectory!, App.Executable)));
        Assert.Equal("keep user settings and receipts", File.ReadAllText(config));
        Assert.True((File.GetUnixFileMode(second.Executable) & UnixFileMode.UserExecute) != 0);
        var desktop = File.ReadAllText(Path.Combine(data, "applications", App.DesktopFile));
        Assert.Contains("Name=Accessibility Mod Manager", desktop);
        Assert.Contains("\\\\$test", desktop);
        Assert.Contains("Terminal=false", desktop);
    }

    [Fact]
    public void FailedShortcutReplacementRollsBackApplication()
    {
        if (!OperatingSystem.IsLinux()) return;
        var data = Path.Combine(root, "data");
        var first = LinuxApplicationInstaller.Install(Package("1.19.0", "old"), App, data);
        var shortcut = Path.Combine(data, "applications", App.DesktopFile);
        File.Delete(shortcut);
        Directory.CreateDirectory(shortcut); // Cannot replace a directory with a shortcut file.
        Assert.ThrowsAny<IOException>(() => LinuxApplicationInstaller.Install(Package("1.20.0", "new"), App, data));
        Assert.Equal("old", File.ReadAllText(first.Executable));
        Assert.Empty(Directory.GetDirectories(Path.Combine(data, App.Id), "*.new-*"));
    }

    [Fact]
    public void WrongProductAndSymlinksDoNotReplaceInstalledFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        var data = Path.Combine(root, "data");
        var first = LinuxApplicationInstaller.Install(Package("1.19.0", "old"), App, data);
        var source = Package("1.20.0", "new");
        File.CreateSymbolicLink(Path.Combine(source, "outside"), first.Executable);
        Assert.Throws<InvalidDataException>(() => LinuxApplicationInstaller.Install(source, App, data));
        Assert.Throws<InvalidDataException>(() => LinuxApplicationInstaller.Install(source, LinuxApplication.Author, data));
        Assert.Equal("old", File.ReadAllText(first.Executable));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("traversal")]
    [InlineData("symlink")]
    [InlineData("wrong-version")]
    [InlineData("duplicate")]
    public async Task UpdateArchiveValidatesContentsBeforeInstallation(string scenario)
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = Package(scenario == "wrong-version" ? "1.18.4" : "1.19.0", "package");
        var archive = Path.Combine(root, "update.tar.gz");
        const string folder = "AccessibilityModManager-1.19.0-linux-x64";
        await using (var output = File.Create(archive))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        await using (var writer = new TarWriter(gzip))
        {
            foreach (var file in Directory.GetFiles(source))
                await writer.WriteEntryAsync(file, folder + "/" + Path.GetFileName(file));
            if (scenario == "traversal")
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "../outside") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("bad")) });
            if (scenario == "symlink")
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, folder + "/link") { LinkName = "/etc/passwd" });
            if (scenario == "duplicate")
                await writer.WriteEntryAsync(Path.Combine(source, App.Executable), folder + "/" + App.Executable);
        }
        var info = new UpdateInfo(new Version(1, 19, 0), "v1.19.0", "test", null, new Uri("https://example.org/test"), "", null,
            new Uri("https://example.org"), "linux-x64");
        if (scenario == "valid")
        {
            var extracted = await LinuxApplicationInstaller.ExtractUpdateAsync(archive, info);
            Assert.Equal("package", File.ReadAllText(Path.Combine(extracted, App.Executable)));
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(() => LinuxApplicationInstaller.ExtractUpdateAsync(archive, info));
            Assert.False(Directory.Exists(Path.Combine(root, "unpacked")));
            Assert.False(File.Exists(Path.Combine(root, "outside")));
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
