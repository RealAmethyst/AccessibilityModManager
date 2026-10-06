using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.PortableTests;

public sealed class ProtonWindowsPackageAdapterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-adapter-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger logger = new LoggerConfiguration().CreateLogger();

    [Theory]
    [InlineData("AMM_DSTS_PUBLIC_ZIP_1_0", "1.0",
        "19f0cd67dd44a42811fad2805ad06dce654a56e9278702252e8d27c32a8822b7")]
    [InlineData("AMM_DSTS_PUBLIC_ZIP_1_1", "1.1",
        "d8acd2db93f0f7ec0c17b91ffaab6da46d655b5269b8f1ed4ab1a0565f51ebe3")]
    public async Task PublishedReloadedReleaseReplacesIfeoScriptAndRestoresOfflineSteamSetup(
        string packageVariable, string version, string packageSha256)
    {
        if (!OperatingSystem.IsLinux()) return;
        var publicZip = Environment.GetEnvironmentVariable(packageVariable);
        var gameExecutable = Environment.GetEnvironmentVariable("AMM_DSTS_GAME_EXE");
        if (publicZip is null || gameExecutable is null) return;

        var gamePath = Path.Combine(root, "published-game");
        var steamRoot = Path.Combine(root, "published-steam");
        var prefix = Path.Combine(steamRoot, "steamapps", "compatdata", "1984270", "pfx");
        var roaming = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Roaming");
        var dotnet = Path.Combine(prefix, "drive_c", "Program Files", "dotnet", "dotnet.exe");
        Directory.CreateDirectory(gamePath);
        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        Directory.CreateDirectory(Path.GetDirectoryName(dotnet)!);
        Directory.CreateSymbolicLink(Path.Combine(prefix, "dosdevices", "z:"), "/");
        File.Copy(gameExecutable, Path.Combine(gamePath, "Digimon Story Time Stranger.exe"));
        File.WriteAllText(dotnet, "offline runtime fixture");
        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "dsts", DisplayName = "Digimon Story Time Stranger",
                ExeName = "Digimon Story Time Stranger.exe", SteamAppId = "1984270"
            },
            PluginId = "amethyst", InstallPath = gamePath, SteamRootPath = steamRoot,
            ProtonPrefixPath = prefix, IsValid = true
        };
        var release = new ModRelease
        {
            GameId = "dsts", PluginId = "amethyst", Version = version, Channel = "stable",
            TargetPlatform = ReleaseTarget.Windows, Sha256 = packageSha256
        };
        var adapted = await new ProtonWindowsPackageAdapter(logger).PrepareAsync(game, release, publicZip);
        using (adapted)
        {
            var local = await LocalProtonPackage.OpenAsync(adapted.Path, logger);
            Assert.Equal("Reloaded II", adapted.Loader.Loader);
            Assert.Null(local.Manifest.PostInstall);
            Assert.Equal("replaceExecutable", local.Manifest.ProtonLaunch!.LaunchMode);
            Assert.Equal("DSTS_Launcher.exe", local.Manifest.ProtonLaunch.LauncherPath);
            Assert.Equal("DSTS.Accessibility", local.Manifest.ProtonLaunch.ReloadedModId);
            using (var archive = ZipFile.OpenRead(adapted.Path))
            {
                Assert.Null(archive.GetEntry(
                    "files/Reloaded-II/Apps/digimon story time stranger.exe/AppConfig.json"));
                using var prism = archive.GetEntry(
                    "files/Reloaded-II/Mods/DSTS.Accessibility/prism.dll")!.Open();
                Assert.Equal("7c7d09c8c7306e8e1a0c46d603ccb2a391c194c77843400c11b746e7dd56a467",
                    Convert.ToHexStringLower(SHA256.HashData(prism)));
            }

            var proton = Path.Combine(root, "published-proton");
            File.WriteAllText(proton,
                "#!/bin/sh\nprintf 'Microsoft.NETCore.App 9.0.20 [C:\\\\dotnet]\\nMicrosoft.WindowsDesktop.App 9.0.20 [C:\\\\dotnet]\\n'\n");
            File.SetUnixFileMode(proton, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var wrapper = Path.Combine(root, "published-wrapper");
            File.WriteAllText(wrapper, "offline wrapper fixture");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var configPath = Path.Combine(root, "published-localconfig.vdf");
            const string config = "\"UserLocalConfigStore\"\n{\n\"Software\" { \"Valve\" { \"Steam\" { \"apps\"\n{\n\"1984270\"\n{\n\"LaunchOptions\" \"PROTON_LOG=1 %command%\"\n}\n}\n} } }\n}\n";
            File.WriteAllText(configPath, config);
            var backup = new BackupManager(logger);
            var receipts = new ReceiptStore(logger, Path.Combine(root, "published-receipts"));
            var engine = new InstallerEngine(backup, new InstallActionExecutor(backup, logger),
                new InstallVerifier(logger), new ManifestParser(logger), new SafeZipExtractor(logger),
                receipts, new DependencyChecker(logger), new LifecycleScriptRunner(logger),
                new DependencyAutoInstaller(new HttpClient(),
                    new DependencyReceiptStore(logger, Path.Combine(root, "published-dep-receipts")), logger),
                new GameVerifier(logger), logger);
            var coordinator = new ProtonSteamInstallCoordinator(engine, receipts,
                new ProtonPackageInspector(logger),
                new ProtonWindowsDesktopRuntime(new HttpClient(), Path.Combine(root, "published-runtime-cache")),
                new ProtonVisualCppRuntime(new HttpClient(), Path.Combine(root, "published-runtime-cache")),
                new SteamLocalConfigStore(configPath, () => false), Path.Combine(root, "published-setups"),
                wrapper, proton, logger);

            await coordinator.InstallAsync(game, adapted.Release, adapted.Path);
            Assert.NotNull(await receipts.LoadAsync("dsts", "amethyst"));
            Assert.Contains("replaceExecutable", File.ReadAllText(configPath));
            var appConfig = Path.Combine(gamePath, "Reloaded-II", "Apps",
                "digimon story time stranger.exe", "AppConfig.json");
            Assert.Contains("Z:", File.ReadAllText(appConfig));
            await coordinator.UninstallAsync(game, "amethyst");
            Assert.Equal(config, File.ReadAllText(configPath));
            Assert.False(File.Exists(appConfig));
            Assert.Null(await receipts.LoadAsync("dsts", "amethyst"));
        }
    }

    [Fact]
    public async Task BundledBepInExWindowsReleaseBecomesValidatedProtonPackage()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (game, release, packagePath) = CreatePackage(includePrism: false);

        using var adapted = await new ProtonWindowsPackageAdapter(logger)
            .PrepareAsync(game, release, packagePath);
        var local = await LocalProtonPackage.OpenAsync(adapted.Path, logger);

        Assert.Equal(ReleaseTarget.Proton, adapted.Release.TargetPlatform);
        Assert.Equal("BepInEx", adapted.Loader.Loader);
        Assert.Equal("direct", local.Manifest.ProtonLaunch!.LaunchMode);
        Assert.Equal("winhttp.dll=n,b", Assert.Single(local.Manifest.ProtonLaunch.WineDllOverrides));
        Assert.Equal(release.Version, adapted.Release.Version);
        Assert.Equal(release.Sha256,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(packagePath))));
    }

    [Fact]
    public async Task WindowsPrismPackageNeedsMatchingWineBridgeInsteadOfSilentConversion()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (game, release, packagePath) = CreatePackage(includePrism: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtonWindowsPackageAdapter(logger).PrepareAsync(game, release, packagePath));
        Assert.Contains("Prism", error.Message);
    }

    [Fact]
    public async Task UnknownWindowsLifecycleScriptIsNeverRunOrDiscarded()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (game, originalRelease, packagePath) = CreatePackage(includePrism: false);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            archive.GetEntry("manifest.json")!.Delete();
            Write(archive, "manifest.json", """
                {"gameId":"game","pluginId":"author","modVersion":"1.0.0",
                 "installActions":[{"type":"copyFile","source":"winhttp.dll","target":"winhttp.dll"},
                    {"type":"copyFolder","sourceDir":"BepInEx","targetDir":"BepInEx"}],
                 "postInstall":{"executable":"files/scripts/Setup.exe","needsAdmin":true,
                    "failureFatal":true,"what":"setup","why":"setup","modifies":"registry",
                    "runFromGameFolder":true}}
                """);
            Write(archive, "files/scripts/Setup.exe", "unknown installer");
        }
        var release = new ModRelease
        {
            GameId = originalRelease.GameId, PluginId = originalRelease.PluginId,
            Version = originalRelease.Version, Channel = originalRelease.Channel,
            TargetPlatform = ReleaseTarget.Windows,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(packagePath)))
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtonWindowsPackageAdapter(logger).PrepareAsync(game, release, packagePath));
        Assert.Contains("lifecycle script without a verified Proton equivalent", error.Message);
    }

    [Fact]
    public async Task UnknownTolkBuildNeedsAVerifiedOrcaBridge()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (game, release, packagePath) = CreatePackage(includePrism: false, includeTolk: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtonWindowsPackageAdapter(logger).PrepareAsync(game, release, packagePath));
        Assert.Contains("verified legacy build", error.Message);
    }

    [Fact]
    public async Task VerifiedLegacyTolkGainsPrismShimAndRestoresPreviousFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (game, release, packagePath) = CreatePackage(includePrism: false, includeTolk: true);
        var previous = Path.Combine(game.InstallPath, "Tolk.dll");
        File.WriteAllText(previous, "previous user file");
        var pe = File.ReadAllBytes(typeof(ProtonLoaderDetector).Assembly.Location);
        var windowsArchive = Path.Combine(root, "tolk-prism-windows.zip");
        using (var zip = ZipFile.Open(windowsArchive, ZipArchiveMode.Create))
        {
            WriteBinary(zip, "dynamic/release/bin/prism.dll", pe);
            WriteBinary(zip, "dynamic/release/bin/tolk.dll", pe);
            Write(zip, "NOTICE", "notice");
            Write(zip, "LICENSES/license.txt", "license");
        }
        var linuxArchive = Path.Combine(root, "tolk-prism-linux.zip");
        using (var zip = ZipFile.Open(linuxArchive, ZipArchiveMode.Create))
            foreach (var name in new[] { "prism_orca_bridge", "prism_speech_dispatcher_bridge",
                         "prism_spiel_bridge" })
            {
                WriteBinary(zip, $"dynamic/release/wine/placeholders/{name}.dll", pe);
                Write(zip, $"dynamic/release/wine/{name}.dll.so", "host");
            }
        var bridgeRelease = new PrismBridgeRelease("9.9.9", "x64",
            new Uri("https://example.test/prism-linux.zip"),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(linuxArchive))),
            new Uri("https://example.test/prism-windows.zip"),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(windowsArchive))));
        using var client = new HttpClient(new RoutingArchiveHandler(
            File.ReadAllBytes(linuxArchive), File.ReadAllBytes(windowsArchive)));
        using var adapted = await new ProtonWindowsPackageAdapter(logger, client,
            Path.Combine(root, "tolk-prism-cache"), bridgeRelease,
            Convert.ToHexStringLower(SHA256.HashData(pe)),
            Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("tolk"))),
            Convert.ToHexStringLower(SHA256.HashData(pe)))
            .PrepareAsync(game, release, packagePath);
        using (var zip = ZipFile.OpenRead(adapted.Path))
        {
            using var stream = zip.GetEntry("files/Tolk.dll")!.Open();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            Assert.Equal(pe, memory.ToArray());
        }
        var backup = new BackupManager(logger);
        var installer = new InstallerEngine(backup, new InstallActionExecutor(backup, logger),
            new InstallVerifier(logger), new ManifestParser(logger), new SafeZipExtractor(logger),
            new ReceiptStore(logger, Path.Combine(root, "tolk-receipts")),
            new DependencyChecker(logger), new LifecycleScriptRunner(logger),
            new DependencyAutoInstaller(client,
                new DependencyReceiptStore(logger, Path.Combine(root, "tolk-dep-receipts")), logger),
            new GameVerifier(logger), logger);
        await installer.InstallAsync(game, adapted.Release, adapted.Path);
        Assert.Equal(pe, File.ReadAllBytes(previous));
        Assert.Equal(pe, File.ReadAllBytes(Path.Combine(game.InstallPath, "prism.dll")));
        await installer.UninstallAsync(game, "author");
        Assert.Equal("previous user file", File.ReadAllText(previous));
        Assert.False(File.Exists(Path.Combine(game.InstallPath, "prism.dll")));
    }

    [Fact]
    public async Task ExactPinnedPrismBuildGainsMatchingWineBridge()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (game, originalRelease, packagePath) = CreatePackage(includePrism: false);
        var prism = File.ReadAllBytes(typeof(ProtonLoaderDetector).Assembly.Location);
        using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            WriteBinary(zip, "files/BepInEx/plugins/prism.dll", prism);
        var release = new ModRelease
        {
            GameId = originalRelease.GameId, PluginId = originalRelease.PluginId,
            Version = originalRelease.Version, Channel = originalRelease.Channel,
            TargetPlatform = ReleaseTarget.Windows,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(packagePath)))
        };
        var windowsArchive = Path.Combine(root, "prism-windows.zip");
        using (var zip = ZipFile.Open(windowsArchive, ZipArchiveMode.Create))
        {
            WriteBinary(zip, "dynamic/release/bin/prism.dll", prism);
            WriteBinary(zip, "dynamic/release/bin/tolk.dll", prism);
            Write(zip, "NOTICE", "notice");
            Write(zip, "LICENSES/license.txt", "license");
        }
        var linuxArchive = Path.Combine(root, "prism-linux.zip");
        using (var zip = ZipFile.Open(linuxArchive, ZipArchiveMode.Create))
            foreach (var name in new[] { "prism_orca_bridge", "prism_speech_dispatcher_bridge",
                         "prism_spiel_bridge" })
            {
                WriteBinary(zip, $"dynamic/release/wine/placeholders/{name}.dll", prism);
                Write(zip, $"dynamic/release/wine/{name}.dll.so", "host");
            }
        var bridgeRelease = new PrismBridgeRelease("9.9.9", "x64",
            new Uri("https://example.test/prism-linux.zip"),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(linuxArchive))),
            new Uri("https://example.test/prism-windows.zip"),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(windowsArchive))));
        using var client = new HttpClient(new RoutingArchiveHandler(
            File.ReadAllBytes(linuxArchive), File.ReadAllBytes(windowsArchive)));
        using var adapted = await new ProtonWindowsPackageAdapter(logger, client,
            Path.Combine(root, "prism-cache"), bridgeRelease,
            Convert.ToHexStringLower(SHA256.HashData(prism)))
            .PrepareAsync(game, release, packagePath);
        var local = await LocalProtonPackage.OpenAsync(adapted.Path, logger);
        Assert.Equal(".amm/prism-9.9.9-x64/wine", local.Manifest.ProtonLaunch!.BridgeDirectory);
        Assert.Contains(local.Manifest.InstallActions, action =>
            action is CopyFileAction copy && copy.Target == "BepInEx/plugins/prism_orca_bridge.dll");
        Assert.Contains(local.Manifest.InstallActions, action =>
            action is CopyFileAction copy && copy.Target ==
            ".amm/prism-9.9.9-x64/wine/prism_orca_bridge.dll.so");
        using var adaptedZip = ZipFile.OpenRead(adapted.Path);
        Assert.NotNull(adaptedZip.GetEntry("notices/Prism/NOTICE"));

        var backup = new BackupManager(logger);
        var receipts = new ReceiptStore(logger, Path.Combine(root, "prism-receipts"));
        var installer = new InstallerEngine(backup, new InstallActionExecutor(backup, logger),
            new InstallVerifier(logger), new ManifestParser(logger), new SafeZipExtractor(logger),
            receipts, new DependencyChecker(logger), new LifecycleScriptRunner(logger),
            new DependencyAutoInstaller(client,
                new DependencyReceiptStore(logger, Path.Combine(root, "prism-dep-receipts")), logger),
            new GameVerifier(logger), logger);
        await installer.InstallAsync(game, adapted.Release, adapted.Path);
        Assert.True(File.Exists(Path.Combine(game.InstallPath, "BepInEx", "plugins",
            "prism_orca_bridge.dll")));
        Assert.True(File.Exists(Path.Combine(game.InstallPath, ".amm", "prism-9.9.9-x64",
            "wine", "prism_orca_bridge.dll.so")));
        await installer.UninstallAsync(game, "author");
        Assert.False(File.Exists(Path.Combine(game.InstallPath, "BepInEx", "plugins",
            "prism_orca_bridge.dll")));
    }

    [Fact]
    public async Task ExistingBepInExLoaderCanBeReusedWithoutTakingOwnership()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (game, _, _) = CreatePackage(includePrism: false);
        var gameDir = game.InstallPath;
        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "core"));
        var dll = File.ReadAllBytes(Path.Combine(gameDir, "Game.exe"));
        File.WriteAllBytes(Path.Combine(gameDir, "winhttp.dll"), dll);
        File.WriteAllBytes(Path.Combine(gameDir, "BepInEx", "core", "BepInEx.dll"), dll);
        var package = Path.Combine(root, "existing-loader.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            Write(archive, "manifest.json", """
                {"gameId":"game","pluginId":"author","modVersion":"1.0.0",
                 "installActions":[{"type":"copyFile","source":"ScreenReader.dll",
                                    "target":"BepInEx/plugins/ScreenReader.dll"}]}
                """);
            Write(archive, "files/ScreenReader.dll", "mod");
        }
        var release = new ModRelease
        {
            GameId = "game", PluginId = "author", Version = "1.0.0", Channel = "stable",
            TargetPlatform = ReleaseTarget.Windows,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(package)))
        };

        using var adapted = await new ProtonWindowsPackageAdapter(logger)
            .PrepareAsync(game, release, package);
        var local = await LocalProtonPackage.OpenAsync(adapted.Path, logger);
        Assert.True(local.Manifest.ProtonLaunch!.UseInstalledWineDllProxy);
        Assert.Equal("winhttp.dll=n,b", Assert.Single(local.Manifest.ProtonLaunch.WineDllOverrides));
        Assert.DoesNotContain(local.Manifest.InstallActions, action =>
            action is CopyFileAction copy && copy.Target == "winhttp.dll");
        Assert.True(File.Exists(Path.Combine(gameDir, "winhttp.dll")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PinnedMelonLoaderDependencySuppliesProxyAfterInstall(
        bool declareDependencyInPackage)
    {
        if (!OperatingSystem.IsLinux()) return;
        var gameDir = Path.Combine(root, "dependency-game");
        var prefix = Path.Combine(root, "dependency-prefix");
        var steamRoot = Path.Combine(root, "dependency-steam");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(prefix);
        Directory.CreateDirectory(steamRoot);
        var pe = File.ReadAllBytes(typeof(ProtonLoaderDetector).Assembly.Location);
        File.WriteAllBytes(Path.Combine(gameDir, "Game.exe"), pe);

        var dependencyArchive = Path.Combine(root, "melonloader.zip");
        using (var zip = ZipFile.Open(dependencyArchive, ZipArchiveMode.Create))
        {
            WriteBinary(zip, "version.dll", pe);
            WriteBinary(zip, "MelonLoader/net6/MelonLoader.dll", pe);
        }
        var dependencyBytes = File.ReadAllBytes(dependencyArchive);
        var dependency = new Dependency
        {
            Id = "melonloader", Type = "framework",
            Check = new DependencyCheck { FilePath = "version.dll" },
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.test/MelonLoader.x64.zip",
                AutoInstall = new ExtractZipAutoInstall
                {
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(dependencyBytes))
                }
            }
        };
        var packagePath = Path.Combine(root, "dependency-windows.zip");
        using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            var manifest = """
                {"gameId":"game","pluginId":"author","modVersion":"1.0.0",
                 "dependencies":DEPENDENCIES_PLACEHOLDER,
                 "installActions":[{"type":"copyFile","source":"mod.dll","target":"Mods/mod.dll"}]}
                """;
            var dependencyJson = """
                [{"id":"melonloader","type":"framework",
                  "check":{"filePath":"version.dll"},
                  "fix":{"downloadUrl":"https://example.test/MelonLoader.x64.zip",
                    "autoInstall":{"kind":"extractZip","sha256":"SHA256_PLACEHOLDER"}}}]
                """.Replace("SHA256_PLACEHOLDER", dependency.Fix.AutoInstall!.Sha256);
            Write(zip, "manifest.json", manifest.Replace("DEPENDENCIES_PLACEHOLDER",
                declareDependencyInPackage ? dependencyJson : "[]"));
            Write(zip, "files/mod.dll", "mod");
        }
        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "game", DisplayName = "Game", SteamAppId = "123",
                ExeName = "Game.exe", Dependencies = [dependency]
            },
            PluginId = "author", InstallPath = gameDir,
            ProtonPrefixPath = prefix, SteamRootPath = steamRoot, IsValid = true
        };
        var release = new ModRelease
        {
            GameId = "game", PluginId = "author", Version = "1.0.0", Channel = "stable",
            TargetPlatform = ReleaseTarget.Windows,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(packagePath)))
        };
        using var client = new HttpClient(new ArchiveHandler(dependencyBytes));
        using var adapted = await new ProtonWindowsPackageAdapter(logger, client)
            .PrepareAsync(game, release, packagePath);
        var local = await LocalProtonPackage.OpenAsync(adapted.Path, logger);
        Assert.True(local.Manifest.ProtonLaunch!.WineDllProxyFromDependency);
        Assert.Equal("version=n,b", Assert.Single(local.Manifest.ProtonLaunch.WineDllOverrides));

        var wrapper = Path.Combine(root, "dependency-wrapper");
        File.WriteAllText(wrapper, "wrapper");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var proton = Path.Combine(root, "dependency-proton");
        File.WriteAllText(proton, "proton");
        var configPath = Path.Combine(root, "dependency-localconfig.vdf");
        const string config = "\"UserLocalConfigStore\"\n{\n\"Software\" { \"Valve\" { \"Steam\" { \"apps\"\n{\n\"123\"\n{\n\"LaunchOptions\" \"%command%\"\n}\n}\n} } }\n}\n";
        File.WriteAllText(configPath, config);
        var backup = new BackupManager(logger);
        var receipts = new ReceiptStore(logger, Path.Combine(root, "dependency-receipts"));
        var engine = new InstallerEngine(backup, new InstallActionExecutor(backup, logger),
            new InstallVerifier(logger), new ManifestParser(logger), new SafeZipExtractor(logger),
            receipts, new DependencyChecker(logger), new LifecycleScriptRunner(logger),
            new DependencyAutoInstaller(client,
                new DependencyReceiptStore(logger, Path.Combine(root, "dependency-dep-receipts")), logger),
            new GameVerifier(logger), logger);
        var coordinator = new ProtonSteamInstallCoordinator(engine, receipts,
            new ProtonPackageInspector(logger),
            new ProtonWindowsDesktopRuntime(client, Path.Combine(root, "dependency-runtime-cache")),
            new ProtonVisualCppRuntime(client, Path.Combine(root, "dependency-runtime-cache")),
            new SteamLocalConfigStore(configPath, () => false), Path.Combine(root, "dependency-setups"),
            wrapper, proton, logger);
        await coordinator.InstallAsync(game, adapted.Release, adapted.Path,
            dependencyHost: new AcceptDependencyHost());
        Assert.True(File.Exists(Path.Combine(gameDir, "version.dll")));
        Assert.Contains("version=n,b", SteamLocalConfigEditor.Read(File.ReadAllText(configPath), "123"));

        await coordinator.UninstallAsync(game, "author");
        Assert.False(File.Exists(Path.Combine(gameDir, "version.dll")));
        Assert.Equal(config, File.ReadAllText(configPath));
    }

    [Fact]
    public async Task PinnedCyberSleuthLoaderWorksWhenPackageOmitsCatalogFileCheck()
    {
        if (!OperatingSystem.IsLinux()) return;
        var gameDir = Path.Combine(root, "dscs-game");
        var appDir = Path.Combine(gameDir, "app_digister");
        var prefix = Path.Combine(root, "dscs-prefix");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(prefix);
        var pe = File.ReadAllBytes(typeof(ProtonLoaderDetector).Assembly.Location);
        File.WriteAllBytes(Path.Combine(appDir, "Digimon Story CS.exe"), pe);

        var dependencyArchive = Path.Combine(root, "dscs-loader.zip");
        using (var zip = ZipFile.Open(dependencyArchive, ZipArchiveMode.Create))
        {
            WriteBinary(zip, "app_digister/freetype.dll", pe);
            WriteBinary(zip, "app_digister/DSCSModLoader.dll", pe);
        }
        var dependencyBytes = File.ReadAllBytes(dependencyArchive);
        var dependencyHash = Convert.ToHexStringLower(SHA256.HashData(dependencyBytes));
        var dependency = new Dependency
        {
            Id = "dscs-mod-loader", Type = "framework",
            Check = new DependencyCheck { FilePath = "app_digister/DSCSModLoader.dll" },
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.test/DSCSModLoader.zip",
                AutoInstall = new ExtractZipAutoInstall { Sha256 = dependencyHash }
            }
        };
        var packagePath = Path.Combine(root, "dscs-package.zip");
        using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            Write(zip, "manifest.json", """
                {"gameId":"dscs","pluginId":"amethyst","modVersion":"1.0-beta24",
                 "dependencies":[{"id":"dscs-mod-loader","type":"framework",
                   "fix":{"downloadUrl":"https://example.test/DSCSModLoader.zip",
                     "autoInstall":{"kind":"extractZip","sha256":"HASH"}}}],
                 "installActions":[{"type":"copyFolder","sourceDir":"resources","targetDir":"resources"}]}
                """.Replace("HASH", dependencyHash));
            Write(zip, "files/resources/plugins/CyberSleuthAccessibility.dll", "mod");
        }
        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "dscs", DisplayName = "Cyber Sleuth", SteamAppId = "1042550",
                ExeName = "app_digister/Digimon Story CS.exe", Dependencies = [dependency]
            },
            PluginId = "amethyst", InstallPath = gameDir,
            ProtonPrefixPath = prefix, SteamRootPath = Path.Combine(root, "dscs-steam"), IsValid = true
        };
        var release = new ModRelease
        {
            GameId = "dscs", PluginId = "amethyst", Version = "1.0-beta24", Channel = "beta",
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(packagePath)))
        };
        using var client = new HttpClient(new ArchiveHandler(dependencyBytes));
        using var adapted = await new ProtonWindowsPackageAdapter(logger, client)
            .PrepareAsync(game, release, packagePath);
        var local = await LocalProtonPackage.OpenAsync(adapted.Path, logger);
        Assert.Equal("DSCSModLoader", adapted.Loader.Loader);
        Assert.Equal("freetype=n,b", Assert.Single(local.Manifest.ProtonLaunch!.WineDllOverrides));
        Assert.True(local.Manifest.ProtonLaunch.WineDllProxyFromDependency);
        Assert.Equal("app_digister/DSCSModLoader.dll",
            Assert.Single(local.Manifest.Dependencies).Check!.FilePath);
    }

    [Fact]
    public async Task PublishedCyberSleuthReleaseUsesOrcaCapablePrismWithOldConfigAbi()
    {
        if (!OperatingSystem.IsLinux()) return;
        var publicZip = Environment.GetEnvironmentVariable("AMM_DSCS_PUBLIC_ZIP");
        var installedGame = Environment.GetEnvironmentVariable("AMM_DSCS_GAME_DIR");
        if (publicZip is null || installedGame is null) return;

        var gameDir = Path.Combine(root, "published-dscs-game");
        var gameSubdir = Path.Combine(gameDir, "app_digister");
        var prefix = Path.Combine(root, "published-dscs-prefix");
        Directory.CreateDirectory(gameSubdir);
        Directory.CreateDirectory(prefix);
        foreach (var name in new[] { "Digimon Story CS.exe", "freetype.dll", "DSCSModLoader.dll" })
            File.Copy(Path.Combine(installedGame, "app_digister", name),
                Path.Combine(gameSubdir, name));
        var dependency = new Dependency
        {
            Id = "dscs-mod-loader", Type = "framework",
            Check = new DependencyCheck { FilePath = "app_digister/DSCSModLoader.dll" },
            Fix = new DependencyFix
            {
                DownloadUrl = "https://github.com/SydMontague/DSCSModLoader/releases/download/v0.1.1/DSCSModLoader.zip",
                AutoInstall = new ExtractZipAutoInstall
                {
                    Sha256 = "9b45023c7746b97dff15d971d6e8dc7da8823ab8d93c0a0ab0c683dd741cb427"
                }
            }
        };
        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "dscs", DisplayName = "Cyber Sleuth", SteamAppId = "1042550",
                ExeName = "app_digister/Digimon Story CS.exe", Dependencies = [dependency]
            },
            PluginId = "amethyst", InstallPath = gameDir,
            ProtonPrefixPath = prefix, SteamRootPath = Path.Combine(root, "published-dscs-steam"),
            IsValid = true
        };
        var release = new ModRelease
        {
            GameId = "dscs", PluginId = "amethyst", Version = "1.0-beta24", Channel = "beta",
            Sha256 = "fc96dce6eaaf01c63dce6d7b41d8e4d174d471bd404e3747f7a83430f9cf92e6"
        };
        using var adapted = await new ProtonWindowsPackageAdapter(logger)
            .PrepareAsync(game, release, publicZip);
        var local = await LocalProtonPackage.OpenAsync(adapted.Path, logger);
        Assert.Equal(".amm/prism-0.18.3-x64/wine", local.Manifest.ProtonLaunch!.BridgeDirectory);
        Assert.Contains(local.Manifest.InstallActions, action =>
            action is CopyFileAction { Target: "resources/plugins/prism-core.dll" });
        using var archive = ZipFile.OpenRead(adapted.Path);
        static string Hash(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        Assert.Equal("f2f234b8ec3b0f6c0df5f4e501232e8e9c20a1299c885ffc7e19ef90559ab6ec",
            Hash(archive.GetEntry("files/resources/plugins/prism.dll")!));
        Assert.Equal("7c7d09c8c7306e8e1a0c46d603ccb2a391c194c77843400c11b746e7dd56a467",
            Hash(archive.GetEntry("files/amm-prism/prism-core.dll")!));
        Assert.NotNull(archive.GetEntry("files/amm-prism/placeholders/0/prism_orca_bridge.dll"));
        Assert.NotNull(archive.GetEntry(
            "files/.amm/prism-0.18.3-x64/wine/prism_orca_bridge.dll.so"));
    }

    private sealed class ArchiveHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(content)
            });
    }

    private sealed class RoutingArchiveHandler(byte[] linux, byte[] windows) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.Contains("linux")
                    ? linux : windows)
            });
    }

    private sealed class AcceptDependencyHost : IDependencyHost
    {
        public Task<bool> ConfirmDependencyInstallAsync(DependencyInstallPrompt prompt,
            CancellationToken ct) => Task.FromResult(true);
        public Task<bool> AwaitManualDependencyAsync(DependencyManualPrompt prompt,
            CancellationToken ct) => Task.FromResult(false);
        public void OnDependencyStarting(string dependencyId, string kind, string displayName) { }
        public void OnDependencyOutputLine(string line) { }
        public void OnDependencyFinished(string dependencyId, bool succeeded) { }
    }

    [Fact]
    public async Task AdaptedWindowsReleaseInstallsAndRestoresSteamSetup()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (detected, release, packagePath) = CreatePackage(includePrism: false);
        var steamRoot = Path.Combine(root, "Steam");
        Directory.CreateDirectory(steamRoot);
        var game = new GameInstall
        {
            Game = detected.Game, PluginId = detected.PluginId,
            InstallPath = detected.InstallPath, ProtonPrefixPath = detected.ProtonPrefixPath,
            SteamRootPath = steamRoot, IsValid = true
        };
        var wrapper = Path.Combine(root, "steam-proton-launch");
        File.WriteAllText(wrapper, "wrapper");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var proton = Path.Combine(root, "proton");
        File.WriteAllText(proton, "proton");
        var configPath = Path.Combine(root, "localconfig.vdf");
        const string config = "\"UserLocalConfigStore\"\n{\n\"Software\" { \"Valve\" { \"Steam\" { \"apps\"\n{\n\"123\"\n{\n\"LaunchOptions\" \"PROTON_LOG=1 %command%\"\n}\n}\n} } }\n}\n";
        File.WriteAllText(configPath, config);
        var backup = new BackupManager(logger);
        var receipts = new ReceiptStore(logger, Path.Combine(root, "receipts"));
        var engine = new InstallerEngine(backup, new InstallActionExecutor(backup, logger),
            new InstallVerifier(logger), new ManifestParser(logger), new SafeZipExtractor(logger),
            receipts, new DependencyChecker(logger), new LifecycleScriptRunner(logger),
            new DependencyAutoInstaller(new HttpClient(),
                new DependencyReceiptStore(logger, Path.Combine(root, "dependency-receipts")), logger),
            new GameVerifier(logger), logger);
        var coordinator = new ProtonSteamInstallCoordinator(engine, receipts,
            new ProtonPackageInspector(logger),
            new ProtonWindowsDesktopRuntime(new HttpClient(), Path.Combine(root, "runtime-cache")),
            new ProtonVisualCppRuntime(new HttpClient(), Path.Combine(root, "runtime-cache")),
            new SteamLocalConfigStore(configPath, () => false), Path.Combine(root, "setups"),
            wrapper, proton, logger);

        await coordinator.InstallCompatibleWindowsReleaseAsync(game, release, packagePath);

        Assert.True(File.Exists(Path.Combine(game.InstallPath, "winhttp.dll")));
        Assert.Contains("winhttp.dll=n,b", SteamLocalConfigEditor.Read(File.ReadAllText(configPath), "123"));
        Assert.NotNull(await receipts.LoadAsync("game", "author"));

        var alias = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "alias", DisplayName = "Game", SteamAppId = "123", ExeName = "Game.exe"
            },
            PluginId = "author", InstallPath = game.InstallPath,
            ProtonPrefixPath = game.ProtonPrefixPath, SteamRootPath = game.SteamRootPath
        };
        var aliasRelease = new ModRelease
        {
            GameId = "alias", PluginId = "author", Version = "1.0.0", Channel = "stable",
            TargetPlatform = ReleaseTarget.Proton, Sha256 = release.Sha256
        };
        var aliasError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.InstallAsync(alias, aliasRelease, packagePath));
        Assert.Contains("already has a mod launch setup", aliasError.Message);

        await coordinator.UninstallAsync(game, "author");

        Assert.False(File.Exists(Path.Combine(game.InstallPath, "winhttp.dll")));
        Assert.Equal(config, File.ReadAllText(configPath));
    }

    private (GameInstall Game, ModRelease Release, string Path) CreatePackage(
        bool includePrism, bool includeTolk = false)
    {
        var gameDir = Path.Combine(root, "game");
        var prefix = Path.Combine(root, "prefix");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(prefix);
        var x64Pe = File.ReadAllBytes(typeof(ProtonLoaderDetector).Assembly.Location);
        var peOffset = BitConverter.ToInt32(x64Pe, 0x3c);
        x64Pe[peOffset + 4] = 0x64;
        x64Pe[peOffset + 5] = 0x86;
        File.WriteAllBytes(Path.Combine(gameDir, "Game.exe"), x64Pe);
        var package = Path.Combine(root, includePrism ? "prism.zip" : "plain.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            var manifest = """
                {"gameId":"game","pluginId":"author","modVersion":"1.0.0",
                 "installActions":[
                   {"type":"copyFile","source":"winhttp.dll","target":"winhttp.dll"},
                   {"type":"copyFolder","sourceDir":"BepInEx","targetDir":"BepInEx"}TOLK_ACTION
                 ]}
                """;
            manifest = manifest.Replace("TOLK_ACTION", includeTolk
                ? ",\n   {\"type\":\"copyFile\",\"source\":\"Tolk.dll\",\"target\":\"Tolk.dll\"}"
                : "");
            Write(archive, "manifest.json", manifest);
            WriteBinary(archive, "files/winhttp.dll", x64Pe);
            WriteBinary(archive, "files/BepInEx/core/BepInEx.dll",
                File.ReadAllBytes(typeof(ProtonLoaderDetector).Assembly.Location));
            Write(archive, "files/BepInEx/plugins/ScreenReader.dll", "mod");
            if (includePrism) Write(archive, "files/BepInEx/plugins/prism.dll", "prism");
            if (includeTolk) Write(archive, "files/Tolk.dll", "tolk");
        }
        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "game", DisplayName = "Game", SteamAppId = "123", ExeName = "Game.exe"
            },
            PluginId = "author", InstallPath = gameDir, ProtonPrefixPath = prefix
        };
        var release = new ModRelease
        {
            GameId = "game", PluginId = "author", Version = "1.0.0", Channel = "stable",
            TargetPlatform = ReleaseTarget.Windows,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(package)))
        };
        return (game, release, package);
    }

    private static void Write(ZipArchive zip, string name, string content) =>
        WriteBinary(zip, name, System.Text.Encoding.UTF8.GetBytes(content));

    private static void WriteBinary(ZipArchive zip, string name, byte[] content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(content);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
