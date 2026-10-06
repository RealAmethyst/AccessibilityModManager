using System.IO.Compression;
using AccessibilityModManager.Authoring.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.PortableTests;

public sealed class LinuxAuthoringTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-authoring-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task SharedBuilderProducesProtonPackageAcceptedByManager()
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "version.dll"), "proxy");
        var output = Path.Combine(root, "mod.zip");
        var launch = new ProtonLaunchConfig
        {
            SteamAppId = "123", GameDisplayName = "Example", GameExecutable = "Game.exe",
            LaunchMode = "direct", WineDllOverrides = ["version=n,b"]
        };
        var windowsDependency = new Dependency
        {
            Id = "windows-runtime", Type = "system", TargetPlatforms = [ReleaseTarget.Windows],
            Check = new DependencyCheck { RegistryKey = @"SOFTWARE\Example" }
        };
        var protonDependency = new Dependency
        {
            Id = "loader", Type = "framework", TargetPlatforms = [ReleaseTarget.Proton],
            Check = new DependencyCheck { FilePath = "version.dll" }
        };
        var built = await new ManifestBuilderService(logger).BuildPackageAsync(
            source, "example", "amethyst", "1.0.0", [windowsDependency, protonDependency], output,
            targetPlatform: ReleaseTarget.Proton, protonLaunch: launch);

        Assert.True(File.Exists(built.ZipPath));
        using var zip = ZipFile.OpenRead(output);
        Assert.NotNull(zip.GetEntry("files/version.dll"));
        var package = await LocalProtonPackage.OpenAsync(output, logger);
        Assert.Equal(ReleaseTarget.Proton, package.Release.TargetPlatform);
        Assert.Equal("123", package.Game.SteamAppId);
        Assert.Equal("loader", Assert.Single(package.Game.Dependencies).Id);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
