using AccessibilityModManager.Authoring.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.PortableTests;

public sealed class NativeLinuxPackageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-native-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger logger = new LoggerConfiguration().CreateLogger();

    [Theory]
    [InlineData(ReleaseTarget.Linux)]
    [InlineData(ReleaseTarget.WindowsLinux)]
    public async Task BuiltNativePackageInstallsAndRestoresGameFiles(string target)
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = Path.Combine(root, "source");
        var gameDir = Path.Combine(root, "game");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "native-game"), "executable");
        File.WriteAllText(Path.Combine(source, "accessibility.dat"), "native mod");
        var package = Path.Combine(root, "native.zip");
        await new ManifestBuilderService(logger).BuildPackageAsync(source,
            "native-game", "author", "1.0.0", [], package, targetPlatform: target);
        var release = new ModRelease
        {
            GameId = "native-game", PluginId = "author", Version = "1.0.0", Channel = "stable",
            TargetPlatform = target,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(package)))
        };
        var game = new GameInstall
        {
            Game = new GameDefinition { GameId = "native-game", DisplayName = "Native Game",
                LinuxExeName = "native-game" },
            PluginId = "author", InstallPath = gameDir, IsValid = true
        };
        var backup = new BackupManager(logger);
        var receipts = new ReceiptStore(logger, Path.Combine(root, "receipts"));
        var installer = new InstallerEngine(backup, new InstallActionExecutor(backup, logger),
            new InstallVerifier(logger), new ManifestParser(logger), new SafeZipExtractor(logger),
            receipts, new DependencyChecker(logger), new LifecycleScriptRunner(logger),
            new DependencyAutoInstaller(new HttpClient(),
                new DependencyReceiptStore(logger, Path.Combine(root, "dep-receipts")), logger),
            new GameVerifier(logger), logger);

        await installer.InstallAsync(game, release, package);

        Assert.Equal("native mod", File.ReadAllText(Path.Combine(gameDir, "accessibility.dat")));
        Assert.Equal(ReleaseTarget.Linux, (await receipts.LoadAsync("native-game", "author"))?.TargetPlatform);

        await installer.UninstallAsync(game, "author");

        Assert.False(File.Exists(Path.Combine(gameDir, "accessibility.dat")));
        Assert.True(File.Exists(Path.Combine(gameDir, "native-game")));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
