using System.IO.Compression;
using System.Security.Cryptography;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.PortableTests;

public sealed class ProtonSteamInstallTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-proton-test-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task RealInstallerAndSteamSetupRestoreTheirOwnedChanges()
    {
        if (!OperatingSystem.IsLinux()) return;
        var steamRoot = Path.Combine(root, "Steam");
        var gamePath = Path.Combine(root, "Game");
        var prefix = Path.Combine(steamRoot, "steamapps", "compatdata", "123", "pfx");
        var dotnet = Path.Combine(prefix, "drive_c", "Program Files", "dotnet", "dotnet.exe");
        var roaming = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Roaming");
        Directory.CreateDirectory(gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(dotnet)!);
        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        Directory.CreateSymbolicLink(Path.Combine(prefix, "dosdevices", "z:"), "/");
        File.WriteAllText(Path.Combine(gamePath, "Game.exe"), "game");
        File.WriteAllText(dotnet, "stub");
        var proton = Path.Combine(root, "proton");
        File.WriteAllText(proton, "#!/bin/sh\nprintf 'Microsoft.NETCore.App 9.0.20 [C:\\\\dotnet]\\nMicrosoft.WindowsDesktop.App 9.0.20 [C:\\\\dotnet]\\n'\n");
        File.SetUnixFileMode(proton, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var wrapper = Path.Combine(root, "steam-proton-launch");
        File.WriteAllText(wrapper, "wrapper");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var configPath = Path.Combine(root, "localconfig.vdf");
        const string config = "\"UserLocalConfigStore\"\n{\n\"Software\" { \"Valve\" { \"Steam\" { \"apps\"\n{\n\"123\"\n{\n\"LaunchOptions\" \"PROTON_LOG=1 %command%\"\n\"OtherSetting\" \"keep\"\n}\n}\n} } }\n}\n";
        File.WriteAllText(configPath, config);

        var packagePath = Path.Combine(root, "mod.zip");
        using (var stream = File.Create(packagePath))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "manifest.json", """
                {"gameId":"example","pluginId":"amethyst","modVersion":"1.0.0",
                 "targetPlatform":"proton",
                 "protonLaunch":{"steamAppId":"123","gameDisplayName":"Example Game",
                     "gameExecutable":"Game.exe",
                     "launcherPath":"Launcher.exe","bridgeDirectory":"prism-wine",
                     "windowsDesktopRuntimeVersion":"9.0.20",
                     "reloadedRoot":"Reloaded-II","reloadedModId":"DSTS.Accessibility"},
                 "installActions":[
                     {"type":"copyFile","source":"Launcher.exe","target":"Launcher.exe"},
                     {"type":"copyFolder","sourceDir":"prism-wine","targetDir":"prism-wine"},
                     {"type":"copyFolder","sourceDir":"Reloaded-II","targetDir":"Reloaded-II"}],
                 "dependencies":[{"id":"loader","type":"framework",
                     "check":{"filePath":"Reloaded-II\\Loader\\X64\\Reloaded.Mod.Loader.dll"}}],
                 "verify":[{"type":"fileExists","path":"Launcher.exe"}]}
                """);
            Write(zip, "files/Launcher.exe", "launcher");
            Write(zip, "files/prism-wine/prism_orca_bridge.dll.so", "bridge");
            Write(zip, "files/Reloaded-II/Loader/X64/Reloaded.Mod.Loader.dll", "loader");
        }

        var definition = new GameDefinition
        {
            GameId = "example", DisplayName = "Example", SteamAppId = "123", ExeName = "Game.exe"
        };
        var game = new GameInstall
        {
            Game = definition, PluginId = "amethyst", InstallPath = gamePath,
            SteamRootPath = steamRoot, ProtonPrefixPath = prefix, IsValid = true
        };
        var release = new ModRelease
        {
            GameId = "example", PluginId = "amethyst", Version = "1.0.0", Channel = "stable",
            TargetPlatform = ReleaseTarget.Proton,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(packagePath)))
        };
        var local = await LocalProtonPackage.OpenAsync(packagePath, logger);
        Assert.Equal("Example Game", local.Game.DisplayName);
        Assert.Equal("loader", Assert.Single(local.Game.Dependencies).Id);
        Assert.Equal(release.Sha256, local.Release.Sha256);
        var backup = new BackupManager(logger);
        var receipts = new ReceiptStore(logger, Path.Combine(root, "receipts"));
        var dependencyReceipts = new DependencyReceiptStore(logger, Path.Combine(root, "dep-receipts"));
        var engine = new InstallerEngine(backup, new InstallActionExecutor(backup, logger),
            new InstallVerifier(logger), new ManifestParser(logger), new SafeZipExtractor(logger),
            receipts, new DependencyChecker(logger), new LifecycleScriptRunner(logger),
            new DependencyAutoInstaller(new HttpClient(), dependencyReceipts, logger),
            new GameVerifier(logger), logger);
        var setupRoot = Path.Combine(root, "setups");
        var coordinator = new ProtonSteamInstallCoordinator(
            engine, receipts, new ProtonPackageInspector(logger),
            new ProtonWindowsDesktopRuntime(new HttpClient(), Path.Combine(root, "runtime-cache")),
            new ProtonVisualCppRuntime(new HttpClient(), Path.Combine(root, "runtime-cache")),
            new SteamLocalConfigStore(configPath, () => false), setupRoot, wrapper, proton, logger);

        await coordinator.InstallAsync(game, release, packagePath);

        Assert.True(File.Exists(Path.Combine(gamePath, "Launcher.exe")));
        Assert.Contains("steam-proton-launch", SteamLocalConfigEditor.Read(File.ReadAllText(configPath), "123"));
        Assert.NotNull(await receipts.LoadAsync("example", "amethyst"));
        var pointer = Path.Combine(roaming, "Reloaded-Mod-Loader-II", "ReloadedII.json");
        var appConfig = Path.Combine(gamePath, "Reloaded-II", "Apps", "game.exe", "AppConfig.json");
        Assert.True(File.Exists(pointer));
        Assert.True(File.Exists(appConfig));

        await coordinator.UninstallAsync(game, "amethyst");

        Assert.False(File.Exists(Path.Combine(gamePath, "Launcher.exe")));
        Assert.Equal(config, File.ReadAllText(configPath));
        Assert.Null(await receipts.LoadAsync("example", "amethyst"));
        Assert.False(File.Exists(pointer));
        Assert.False(File.Exists(appConfig));
        Assert.False(File.Exists(Path.Combine(setupRoot, "amethyst", "example.json")));
    }

    [Fact]
    public async Task DirectLoaderInstallsWithoutLauncherOrBridgeAndRestoresSteam()
    {
        if (!OperatingSystem.IsLinux()) return;
        var steamRoot = Path.Combine(root, "Steam");
        var gamePath = Path.Combine(root, "DirectGame");
        var prefix = Path.Combine(steamRoot, "steamapps", "compatdata", "124", "pfx");
        Directory.CreateDirectory(gamePath);
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(gamePath, "Direct.exe"), "game");
        var wrapper = Path.Combine(root, "steam-proton-launch");
        File.WriteAllText(wrapper, "wrapper");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var proton = Path.Combine(root, "proton");
        File.WriteAllText(proton, "proton");
        var configPath = Path.Combine(root, "direct-localconfig.vdf");
        const string config = "\"UserLocalConfigStore\"\n{\n\"Software\" { \"Valve\" { \"Steam\" { \"apps\"\n{\n\"124\"\n{\n\"LaunchOptions\" \"PROTON_LOG=1 %command%\"\n}\n}\n} } }\n}\n";
        File.WriteAllText(configPath, config);
        var packagePath = Path.Combine(root, "direct-mod.zip");
        using (var stream = File.Create(packagePath))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "manifest.json", """
                {"gameId":"direct","pluginId":"amethyst","modVersion":"1.0.0",
                 "targetPlatform":"proton",
                 "protonLaunch":{"steamAppId":"124","gameDisplayName":"Direct Game",
                    "gameExecutable":"Direct.exe","launchMode":"direct",
                    "wineDllOverrides":["winhttp.dll=n,b"]},
                 "installActions":[{"type":"copyFile","source":"winhttp.dll","target":"winhttp.dll"}],
                 "verify":[{"type":"fileExists","path":"winhttp.dll"}]}
                """);
            Write(zip, "files/winhttp.dll", "loader");
        }
        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "direct", DisplayName = "Direct Game", SteamAppId = "124", ExeName = "Direct.exe"
            },
            PluginId = "amethyst", InstallPath = gamePath, SteamRootPath = steamRoot,
            ProtonPrefixPath = prefix, IsValid = true
        };
        var release = new ModRelease
        {
            GameId = "direct", PluginId = "amethyst", Version = "1.0.0", Channel = "stable",
            TargetPlatform = ReleaseTarget.Proton,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(packagePath)))
        };
        var backup = new BackupManager(logger);
        var receipts = new ReceiptStore(logger, Path.Combine(root, "direct-receipts"));
        var engine = new InstallerEngine(backup, new InstallActionExecutor(backup, logger),
            new InstallVerifier(logger), new ManifestParser(logger), new SafeZipExtractor(logger),
            receipts, new DependencyChecker(logger), new LifecycleScriptRunner(logger),
            new DependencyAutoInstaller(new HttpClient(),
                new DependencyReceiptStore(logger, Path.Combine(root, "direct-dep-receipts")), logger),
            new GameVerifier(logger), logger);
        var coordinator = new ProtonSteamInstallCoordinator(engine, receipts,
            new ProtonPackageInspector(logger),
            new ProtonWindowsDesktopRuntime(new HttpClient(), Path.Combine(root, "runtime-cache")),
            new ProtonVisualCppRuntime(new HttpClient(), Path.Combine(root, "runtime-cache")),
            new SteamLocalConfigStore(configPath, () => false), Path.Combine(root, "direct-setups"),
            wrapper, proton, logger);

        await coordinator.InstallAsync(game, release, packagePath);

        Assert.True(File.Exists(Path.Combine(gamePath, "winhttp.dll")));
        var installed = SteamLocalConfigEditor.Read(File.ReadAllText(configPath), "124");
        Assert.Contains("--amm-v1 direct", installed);
        Assert.Contains("winhttp.dll=n,b", installed);
        Assert.DoesNotContain("Reloaded-II", installed);

        var updatePath = Path.Combine(root, "direct-mod-update.zip");
        using (var stream = File.Create(updatePath))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "manifest.json", """
                {"gameId":"direct","pluginId":"amethyst","modVersion":"1.1.0",
                 "targetPlatform":"proton",
                 "protonLaunch":{"steamAppId":"124","gameDisplayName":"Direct Game",
                    "gameExecutable":"Direct.exe","launchMode":"direct",
                    "wineDllOverrides":["winhttp.dll=n,b"]},
                 "installActions":[{"type":"copyFile","source":"winhttp.dll","target":"winhttp.dll"}],
                 "verify":[{"type":"fileExists","path":"winhttp.dll"}]}
                """);
            Write(zip, "files/winhttp.dll", "updated loader");
        }
        var update = new ModRelease
        {
            GameId = "direct", PluginId = "amethyst", Version = "1.1.0", Channel = "stable",
            TargetPlatform = ReleaseTarget.Proton,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(updatePath)))
        };
        await coordinator.UpdateAsync(game, update, updatePath);
        Assert.Equal("updated loader", File.ReadAllText(Path.Combine(gamePath, "winhttp.dll")));
        Assert.Equal(installed, SteamLocalConfigEditor.Read(File.ReadAllText(configPath), "124"));

        await coordinator.UninstallAsync(game, "amethyst");

        Assert.False(File.Exists(Path.Combine(gamePath, "winhttp.dll")));
        Assert.Equal(config, File.ReadAllText(configPath));
    }

    [Fact]
    public void ReloadedSetupPreservesAnExistingDifferentLoaderConfiguration()
    {
        if (!OperatingSystem.IsLinux()) return;
        var gamePath = Path.Combine(root, "Game");
        var prefix = Path.Combine(root, "compatdata", "123", "pfx");
        var roaming = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Roaming");
        var pointer = Path.Combine(roaming, "Reloaded-Mod-Loader-II", "ReloadedII.json");
        Directory.CreateDirectory(Path.Combine(gamePath, "Reloaded-II"));
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        Directory.CreateSymbolicLink(Path.Combine(prefix, "dosdevices", "z:"), "/");
        File.WriteAllText(pointer, "another loader");
        File.WriteAllText(Path.Combine(gamePath, "Game.exe"), "game");
        var game = new GameInstall
        {
            Game = new GameDefinition { GameId = "example", DisplayName = "Example", ExeName = "Game.exe" },
            PluginId = "amethyst", InstallPath = gamePath, ProtonPrefixPath = prefix
        };
        var launch = new ProtonLaunchConfig
        {
            SteamAppId = "123", GameDisplayName = "Example Game", GameExecutable = "Game.exe",
            LauncherPath = "Launcher.exe", BridgeDirectory = "prism-wine",
            ReloadedRoot = "Reloaded-II", ReloadedModId = "DSTS.Accessibility"
        };

        Assert.Throws<InvalidOperationException>(() => ProtonReloadedConfig.Plan(game, launch));
        Assert.Equal("another loader", File.ReadAllText(pointer));
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
