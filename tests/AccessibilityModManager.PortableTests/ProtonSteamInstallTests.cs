using System.IO.Compression;
using System.Reflection.PortableExecutable;
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
        File.WriteAllBytes(Path.Combine(gamePath, "Direct.exe"), TestPe.Game(Machine.Amd64));
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
            WriteBinary(zip, "files/winhttp.dll", TestPe.Dll(Machine.Amd64));
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

        var updatedLoader = TestPe.Build(Machine.Amd64, ("KERNEL32.dll", ["LoadLibraryW"]));
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
            WriteBinary(zip, "files/winhttp.dll", updatedLoader);
        }
        var update = new ModRelease
        {
            GameId = "direct", PluginId = "amethyst", Version = "1.1.0", Channel = "stable",
            TargetPlatform = ReleaseTarget.Proton,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(updatePath)))
        };
        // The user removed the mod's launch text; updating the mod puts it back beside theirs.
        File.WriteAllText(configPath, config.Replace("PROTON_LOG=1 %command%", "PROTON_LOG=1 %command% -novid"));
        await coordinator.UpdateAsync(game, update, updatePath);
        Assert.Equal(updatedLoader, File.ReadAllBytes(Path.Combine(gamePath, "winhttp.dll")));
        Assert.Equal(installed + " -novid", SteamLocalConfigEditor.Read(File.ReadAllText(configPath), "124"));

        await coordinator.UninstallAsync(game, "amethyst");

        Assert.False(File.Exists(Path.Combine(gamePath, "winhttp.dll")));
        Assert.Equal(config.Replace("PROTON_LOG=1 %command%", "PROTON_LOG=1 %command% -novid"),
            File.ReadAllText(configPath));
    }

    [Fact]
    public void ManagerTextIsRemovedFromEveryLaunchOptionFormat()
    {
        Assert.Equal("PROTON_LOG=1 %command% -windowed", SteamLaunchOptionsPlan.RemoveManagerText(
            "PROTON_LOG=1 '/opt/amm/steam-proton-launch' --amm-v1 direct '/games/My Game/Game.exe' " +
            "'-' '-' 'winhttp=n,b' 0 -- %command% -windowed"));
        Assert.Equal("%command%", SteamLaunchOptionsPlan.RemoveManagerText(
            "'/opt/amm/steam-proton-launch' '/games/It'\\''s/Game.exe' '/games/It'\\''s/Launcher.exe' " +
            "'/games/It'\\''s/bridge' -- %command%"));
        Assert.Equal("gamemoderun %command% -player=O'Brien", SteamLaunchOptionsPlan.RemoveManagerText(
            "'/opt/amm/steam-proton-launch' --amm-v1 replaceExecutable '/g/Game.exe' '/g/Launcher.exe' " +
            "'-' '-' 0 -- gamemoderun %command% -player=O'Brien"));
        Assert.Null(SteamLaunchOptionsPlan.RemoveManagerText("PROTON_LOG=1 %command%"));
        Assert.Null(SteamLaunchOptionsPlan.RemoveManagerText("'/opt/amm/steam-proton-launch -- %command%"));

        var plan = new SteamLaunchOptionsPlan("", "'/a/steam-proton-launch' --amm-v1 direct '/g' '-' '-' '-' 0 -- %command%")
        {
            WasPresent = false
        };
        Assert.Null(plan.UserOptions(plan.Installed));
        Assert.Null(plan.UserOptions(null));
        Assert.Equal("%command% -novid", plan.UserOptions(plan.Installed + " -novid"));
        Assert.Equal("gamemoderun %command%", plan.UserOptions("gamemoderun %command%"));
        Assert.Throws<InvalidOperationException>(() =>
            plan.UserOptions("'/a/steam-proton-launch' --amm-v1 direct %command%"));
    }

    [Fact]
    public async Task UninstallFinishesAfterSteamAndTheLauncherChangedOwnedSettings()
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = await InstallReloadedSetupAsync();
        File.WriteAllText(setup.AppConfig, "rewritten by the mod launcher");
        var edited = SteamLocalConfigEditor.Read(File.ReadAllText(setup.ConfigPath), "123") + " -windowed";
        File.WriteAllText(setup.ConfigPath, setup.Config.Replace(
            "\"PROTON_LOG=1 %command%\"", "\"" + edited.Replace("\"", "\\\"") + "\""));

        var result = await setup.Coordinator.UninstallAsync(setup.Game, "amethyst");

        Assert.Equal(LaunchOptionsOutcome.ManagerTextRemoved, result.LaunchOptions);
        Assert.Equal("PROTON_LOG=1 %command% -windowed",
            SteamLocalConfigEditor.Read(File.ReadAllText(setup.ConfigPath), "123"));
        Assert.Equal("rewritten by the mod launcher", File.ReadAllText(Assert.Single(result.RecoveredFiles)));
        // The recovery copy must not be mistaken for a damaged setup record by later scans.
        Assert.False(result.RecoveredFiles[0].StartsWith(Path.Combine(root, "setups") + "/", StringComparison.Ordinal));
        Assert.Empty(ProtonSteamSetupLookup.Find(Path.Combine(root, "setups"), "123", setup.Game.InstallPath));
        Assert.False(File.Exists(setup.AppConfig));
        Assert.False(File.Exists(setup.Pointer));
        Assert.Null(await setup.Receipts.LoadAsync("example", "amethyst"));
    }

    [Fact]
    public async Task UpdateHandsAManagerOwnedReloadedConfigBackToTheLauncher()
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = await InstallReloadedSetupAsync();
        var updatePath = Path.Combine(root, "reloaded-update.zip");
        using (var stream = File.Create(updatePath))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "manifest.json", """
                {"gameId":"example","pluginId":"amethyst","modVersion":"1.1.0",
                 "targetPlatform":"proton",
                 "protonLaunch":{"steamAppId":"123","gameDisplayName":"Example Game",
                     "gameExecutable":"Game.exe",
                     "launcherPath":"Launcher.exe","bridgeDirectory":"prism-wine",
                     "windowsDesktopRuntimeVersion":"9.0.20"},
                 "installActions":[
                     {"type":"copyFile","source":"Launcher.exe","target":"Launcher.exe"},
                     {"type":"copyFolder","sourceDir":"prism-wine","targetDir":"prism-wine"},
                     {"type":"copyFolder","sourceDir":"Reloaded-II","targetDir":"Reloaded-II"}]}
                """);
            Write(zip, "files/Launcher.exe", "launcher 1.1");
            Write(zip, "files/prism-wine/prism_orca_bridge.dll.so", "bridge");
            Write(zip, "files/Reloaded-II/Loader/X64/Reloaded.Mod.Loader.dll", "loader");
            Write(zip, "files/Reloaded-II/Apps/game.exe/AppConfig.json", "package config");
        }
        var update = new ModRelease
        {
            GameId = "example", PluginId = "amethyst", Version = "1.1.0", Channel = "stable",
            TargetPlatform = ReleaseTarget.Proton,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(updatePath)))
        };

        await setup.Coordinator.UpdateAsync(setup.Game, update, updatePath);

        Assert.Equal("package config", File.ReadAllText(setup.AppConfig));
        Assert.False(File.Exists(setup.Pointer));
        Assert.False(Directory.Exists(Path.Combine(root, "setups-recovery")));
        Assert.Contains("--amm-v1 replaceExecutable",
            SteamLocalConfigEditor.Read(File.ReadAllText(setup.ConfigPath), "123"));
        var result = await setup.Coordinator.UninstallAsync(setup.Game, "amethyst");
        Assert.Equal(LaunchOptionsOutcome.Restored, result.LaunchOptions);
        Assert.False(File.Exists(setup.AppConfig));
        Assert.Equal(setup.Config, File.ReadAllText(setup.ConfigPath));
    }

    [Fact]
    public async Task UninstallLeavesLaunchOptionsTheUserReplaced()
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = await InstallReloadedSetupAsync();
        var replaced = setup.Config.Replace("PROTON_LOG=1 %command%", "gamemoderun %command%");
        File.WriteAllText(setup.ConfigPath, replaced);

        var result = await setup.Coordinator.UninstallAsync(setup.Game, "amethyst");

        Assert.Equal(LaunchOptionsOutcome.LeftUnchanged, result.LaunchOptions);
        Assert.Equal(replaced, File.ReadAllText(setup.ConfigPath));
        Assert.Empty(result.RecoveredFiles);
        Assert.Null(await setup.Receipts.LoadAsync("example", "amethyst"));
    }

    private sealed record ReloadedSetup(ProtonSteamInstallCoordinator Coordinator, GameInstall Game,
        ReceiptStore Receipts, string ConfigPath, string Config, string AppConfig, string Pointer);

    private async Task<ReloadedSetup> InstallReloadedSetupAsync()
    {
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
        const string config = "\"UserLocalConfigStore\"\n{\n\"Software\" { \"Valve\" { \"Steam\" { \"apps\"\n{\n\"123\"\n{\n\"LaunchOptions\" \"PROTON_LOG=1 %command%\"\n}\n}\n} } }\n}\n";
        File.WriteAllText(configPath, config);
        var packagePath = Path.Combine(root, "reloaded-mod.zip");
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
                     "reloadedRoot":"Reloaded-II","reloadedModId":"Example.Mod"},
                 "installActions":[
                     {"type":"copyFile","source":"Launcher.exe","target":"Launcher.exe"},
                     {"type":"copyFolder","sourceDir":"prism-wine","targetDir":"prism-wine"},
                     {"type":"copyFolder","sourceDir":"Reloaded-II","targetDir":"Reloaded-II"}]}
                """);
            Write(zip, "files/Launcher.exe", "launcher");
            Write(zip, "files/prism-wine/prism_orca_bridge.dll.so", "bridge");
            Write(zip, "files/Reloaded-II/Loader/X64/Reloaded.Mod.Loader.dll", "loader");
        }
        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "example", DisplayName = "Example", SteamAppId = "123", ExeName = "Game.exe"
            },
            PluginId = "amethyst", InstallPath = gamePath,
            SteamRootPath = steamRoot, ProtonPrefixPath = prefix, IsValid = true
        };
        var release = new ModRelease
        {
            GameId = "example", PluginId = "amethyst", Version = "1.0.0", Channel = "stable",
            TargetPlatform = ReleaseTarget.Proton,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(packagePath)))
        };
        var backup = new BackupManager(logger);
        var receipts = new ReceiptStore(logger, Path.Combine(root, "receipts"));
        var engine = new InstallerEngine(backup, new InstallActionExecutor(backup, logger),
            new InstallVerifier(logger), new ManifestParser(logger), new SafeZipExtractor(logger),
            receipts, new DependencyChecker(logger), new LifecycleScriptRunner(logger),
            new DependencyAutoInstaller(new HttpClient(),
                new DependencyReceiptStore(logger, Path.Combine(root, "dep-receipts")), logger),
            new GameVerifier(logger), logger);
        var coordinator = new ProtonSteamInstallCoordinator(
            engine, receipts, new ProtonPackageInspector(logger),
            new ProtonWindowsDesktopRuntime(new HttpClient(), Path.Combine(root, "runtime-cache")),
            new ProtonVisualCppRuntime(new HttpClient(), Path.Combine(root, "runtime-cache")),
            new SteamLocalConfigStore(configPath, () => false), Path.Combine(root, "setups"),
            wrapper, proton, logger);
        await coordinator.InstallAsync(game, release, packagePath);
        var appConfig = Path.Combine(gamePath, "Reloaded-II", "Apps", "game.exe", "AppConfig.json");
        var pointer = Path.Combine(roaming, "Reloaded-Mod-Loader-II", "ReloadedII.json");
        Assert.True(File.Exists(appConfig));
        return new ReloadedSetup(coordinator, game, receipts, configPath, config, appConfig, pointer);
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

    private static void WriteBinary(ZipArchive zip, string name, byte[] content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(content);
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
