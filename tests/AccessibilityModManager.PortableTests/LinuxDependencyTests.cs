using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.PortableTests;

public sealed class LinuxDependencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-linux-deps-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task WindowsStyleFrameworkCheckWorksInProtonGameFolder()
    {
        if (!OperatingSystem.IsLinux()) return;
        var gameDir = Path.Combine(root, "game");
        var loader = Path.Combine(gameDir, "MelonLoader", "net6", "MelonLoader.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(loader)!);
        File.WriteAllText(loader, "loader");
        var game = CreateGame(new Dependency
        {
            Id = "melonloader", Type = "framework",
            Check = new DependencyCheck { FilePath = @"MelonLoader\net6\MelonLoader.dll" }
        });
        LinuxDependencySupport.EnsureSupported(game);

        var status = Assert.Single(await new DependencyChecker(logger).CheckAsync(CreateInstall(game, gameDir)));
        Assert.Equal(Core.Interfaces.DependencyStatusKind.Installed, status.Status);
    }

    [Fact]
    public void WindowsInstallerFailsBeforeAnyDownloadOrFileChange()
    {
        if (!OperatingSystem.IsLinux()) return;
        var game = CreateGame(new Dependency
        {
            Id = "old-loader", Type = "framework",
            Check = new DependencyCheck { FilePath = "version.dll" },
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.invalid/installer.exe",
                AutoInstall = new RunInstallerAutoInstall { Sha256 = new string('0', 64) }
            }
        });

        Assert.Contains("old-loader", Assert.Throws<PlatformNotSupportedException>(
            () => LinuxDependencySupport.EnsureSupported(game)).Message);
    }

    [Fact]
    public void ExplicitWindowsInstallerDoesNotBlockASeparateProtonDependency()
    {
        if (!OperatingSystem.IsLinux()) return;
        var windows = new Dependency
        {
            Id = "windows-installer", Type = "system",
            TargetPlatforms = [ReleaseTarget.Windows],
            Check = new DependencyCheck { RegistryKey = @"SOFTWARE\Example" },
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.invalid/setup.exe",
                AutoInstall = new RunInstallerAutoInstall { Sha256 = new string('0', 64) }
            }
        };
        var proton = new Dependency
        {
            Id = "proton-loader", Type = "framework",
            TargetPlatforms = [ReleaseTarget.Proton],
            Check = new DependencyCheck { FilePath = "BepInEx/core/BepInEx.dll" }
        };
        var game = new GameDefinition
        {
            GameId = "game", DisplayName = "Game", Dependencies = [windows, proton]
        };

        LinuxDependencySupport.EnsureSupported(game);
        Assert.Equal("proton-loader",
            Assert.Single(DependencyTargeting.ForTarget(game.Dependencies, ReleaseTarget.Proton)).Id);
        Assert.Equal("windows-installer",
            Assert.Single(DependencyTargeting.ForTarget(game.Dependencies, ReleaseTarget.Windows)).Id);
    }

    [Fact]
    public void LegacyWindowsFrameworkCanBeCheckedUnderProton()
    {
        var dependency = new Dependency
        {
            Id = "portable-loader", Type = "framework",
            Check = new DependencyCheck { FilePath = @"BepInEx\core\BepInEx.dll" }
        };

        Assert.True(DependencyTargeting.AppliesTo(dependency, ReleaseTarget.Windows));
        Assert.True(DependencyTargeting.AppliesTo(dependency, ReleaseTarget.Proton));
        Assert.False(DependencyTargeting.AppliesTo(dependency, ReleaseTarget.Linux));
    }

    [Fact]
    public async Task ExistingZipDependencyInstallsAndReleasesOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        var gameDir = Path.Combine(root, "game");
        Directory.CreateDirectory(gameDir);
        var zip = Path.Combine(root, "loader.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry(@"BepInEx\core\BepInEx.dll").Open()))
            writer.Write("loader");
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip)));
        var dependency = new Dependency
        {
            Id = "bepinex", Type = "framework",
            Check = new DependencyCheck { FilePath = @"BepInEx\core\BepInEx.dll" },
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.invalid/loader.zip",
                AutoInstall = new ExtractZipAutoInstall { Sha256 = sha }
            }
        };
        var game = CreateGame(dependency);
        LinuxDependencySupport.EnsureSupported(game);
        var install = CreateInstall(game, gameDir);
        var receipts = new DependencyReceiptStore(logger, Path.Combine(root, "receipts"));
        using var client = new HttpClient(new ZipHandler(zip));
        var installer = new DependencyAutoInstaller(client, receipts, logger);

        var result = await installer.InstallAsync(dependency, install, "mod", null, CancellationToken.None);
        Assert.True(result.Succeeded, result.ErrorMessage);
        var loader = Path.Combine(gameDir, "BepInEx", "core", "BepInEx.dll");
        Assert.Equal("loader", File.ReadAllText(loader));
        var status = Assert.Single(await new DependencyChecker(logger).CheckAsync(install));
        Assert.Equal(Core.Interfaces.DependencyStatusKind.Installed, status.Status);

        Assert.Empty(await installer.ReleaseDependenciesForPluginAsync(install, "mod", CancellationToken.None));
        Assert.False(File.Exists(loader));
    }

    private static GameDefinition CreateGame(Dependency dependency) => new()
    {
        GameId = "game", DisplayName = "Game", Dependencies = [dependency]
    };

    private static GameInstall CreateInstall(GameDefinition game, string path) => new()
    {
        Game = game, PluginId = "mod", InstallPath = path
    };

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private sealed class ZipHandler(string zipPath) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(File.ReadAllBytes(zipPath))
            });
    }
}
