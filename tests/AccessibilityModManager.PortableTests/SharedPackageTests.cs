using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using AccessibilityModManager.Authoring.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.PortableTests;

public sealed class SharedPackageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-shared-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task Shared_package_contains_both_platforms_but_installs_only_native_dependencies()
    {
        if (!OperatingSystem.IsLinux()) return;
        var bytes = "native dependency"u8.ToArray();
        var windows = new Dependency { Id = "windows-runtime", Type = "system", TargetPlatforms = ["windows"],
            Fix = new DependencyFix { DownloadUrl = "https://example.invalid/windows.exe",
                AutoInstall = new RunInstallerAutoInstall { Sha256 = new string('0', 64) } } };
        var linux = new Dependency { Id = "linux-runtime", Type = "framework", TargetPlatforms = ["linux"],
            Check = new DependencyCheck { FilePath = "runtime.so" },
            Fix = new DependencyFix { DownloadUrl = "https://example.invalid/runtime.so",
                AutoInstall = new CopyFileAutoInstall { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) } } };
        var proton = new Dependency { Id = "proton-only", Type = "framework", TargetPlatforms = ["proton"] };
        var dependencies = new List<Dependency> { windows, linux, proton };
        var source = Path.Combine(root, "source");
        var gameDir = Path.Combine(root, "game");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(source, "mod.lua"), "shared mod");
        File.WriteAllText(Path.Combine(gameDir, "emulator"), "native game");
        var package = Path.Combine(root, "shared.zip");
        await new ManifestBuilderService(logger).BuildPackageAsync(source, "game", "author", "1", dependencies,
            package, ReleaseTarget.WindowsLinux);
        using (var archive = ZipFile.OpenRead(package))
        using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open()))
        {
            var manifest = new ManifestParser(logger).Parse(reader.ReadToEnd());
            Assert.Equal(new[] { "windows-runtime", "linux-runtime" }, manifest.Dependencies.Select(d => d.Id));
            Assert.Equal("windows-runtime", Assert.Single(DependencyTargeting.ForTarget(manifest.Dependencies, "windows")).Id);
        }
        using var handler = new Download(bytes);
        using var http = new HttpClient(handler);
        var backup = new BackupManager(logger);
        var receipts = new ReceiptStore(logger, Path.Combine(root, "receipts"));
        var installer = new InstallerEngine(backup, new InstallActionExecutor(backup, logger), new InstallVerifier(logger),
            new ManifestParser(logger), new SafeZipExtractor(logger), receipts, new DependencyChecker(logger),
            new LifecycleScriptRunner(logger), new DependencyAutoInstaller(http,
                new DependencyReceiptStore(logger, Path.Combine(root, "dependencies")), logger), new GameVerifier(logger), logger);
        var game = new GameInstall { Game = new GameDefinition { GameId = "game", DisplayName = "Game",
            LinuxExeName = "emulator", Dependencies = dependencies }, PluginId = "author", InstallPath = gameDir, IsValid = true };
        var release = new ModRelease { GameId = "game", PluginId = "author", Version = "1", Channel = "stable",
            TargetPlatform = ReleaseTarget.WindowsLinux, Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(package))) };
        await installer.InstallAsync(game, release, package, dependencyHost: new Consent());
        Assert.Equal(new[] { "/runtime.so" }, handler.Paths);
        Assert.Equal("native dependency", File.ReadAllText(Path.Combine(gameDir, "runtime.so")));
        Assert.Equal("linux", (await receipts.LoadAsync("game", "author"))!.TargetPlatform);
        await installer.UninstallAsync(game, "author");
        Assert.False(File.Exists(Path.Combine(gameDir, "mod.lua")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_game_install_verifies_before_adopting_and_preserves_executable_bits(bool wrongHash)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var archiveBytes = new MemoryStream();
        using (var zip = new ZipArchive(archiveBytes, ZipArchiveMode.Create, true))
        {
            foreach (var name in new[] { "BizHawk/EmuHawk", "BizHawk/helper" })
            {
                var entry = zip.CreateEntry(name);
                entry.ExternalAttributes = (0x8000 | 0x1ed) << 16;
                using var writer = new StreamWriter(entry.Open());
                writer.Write("disposable native executable, never run");
            }
        }
        var bytes = archiveBytes.ToArray();
        var dependency = new Dependency { Id = "bizhawk-linux", Type = "system", IsGameInstaller = true,
            TargetPlatforms = ["linux"], Fix = new DependencyFix { DownloadUrl = "https://example.invalid/bizhawk.zip",
                AutoInstall = new ExtractAppAutoInstall { Sha256 = wrongHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes)) } } };
        var game = new GameDefinition { GameId = "pokemon", DisplayName = "Pokemon", LinuxExeName = "EmuHawk", Dependencies = [dependency] };
        Directory.CreateDirectory(root);
        using var http = new HttpClient(new Download(bytes));
        var installer = new NativeGameInstaller(new DependencyAutoInstaller(http,
            new DependencyReceiptStore(logger, Path.Combine(root, "receipts")), logger),
            new GameDependencyReceiptStore(Path.Combine(root, "game-receipts")));
        if (wrongHash)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => installer.InstallAsync(game, dependency, root, null));
            Assert.False(Directory.Exists(Path.Combine(root, "pokemon")));
        }
        else
        {
            var installed = await installer.InstallAsync(game, dependency, root, null);
            Assert.Equal(root, installed);
            Assert.False(Directory.Exists(Path.Combine(root, "pokemon")));
            Assert.False(Directory.Exists(Path.Combine(root, "BizHawk")));
            Assert.True(File.GetUnixFileMode(Path.Combine(installed, "helper")).HasFlag(UnixFileMode.UserExecute));
            await installer.InstallAsync(game, dependency, root, null);
            Assert.True(File.Exists(Path.Combine(installed, "EmuHawk")));
        }
        Assert.Empty(Directory.GetDirectories(root, ".amm-install-*"));
    }

    private sealed class Download(byte[] bytes) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class Consent : IDependencyHost
    {
        public Task<bool> ConfirmDependencyInstallAsync(DependencyInstallPrompt prompt, CancellationToken ct)
        {
            Assert.Equal("linux-runtime", Assert.Single(prompt.Items).Dependency.Id);
            return Task.FromResult(true);
        }
        public Task<bool> AwaitManualDependencyAsync(DependencyManualPrompt prompt, CancellationToken ct) => Task.FromResult(false);
        public void OnDependencyStarting(string dependencyId, string kind, string displayName) { }
        public void OnDependencyOutputLine(string line) { }
        public void OnDependencyFinished(string dependencyId, bool succeeded) { }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
