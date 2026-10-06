using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.PortableTests;

public sealed class LinuxFoundationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amm-portable-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public void ContainmentUsesLinuxCaseSensitivity()
    {
        if (!OperatingSystem.IsLinux()) return;
        var game = Path.Combine(_root, "Game");
        var sibling = Path.Combine(_root, "game", "payload.dll");
        Assert.False(PathSafety.IsContained(game, sibling));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:\\escape")]
    [InlineData("..\\escape")]
    public void RelativePathsCannotEscapeOnLinux(string input)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Throws<InvalidOperationException>(() => PathSafety.CombineContained(_root, input));
    }

    [Theory]
    [InlineData("bad:name.dll")]
    [InlineData("folder\\file.dll")]
    [InlineData("folder/file.dll")]
    public void WindowsUnsafeFileNamesStayUnsafeOnLinux(string input)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Throws<InvalidOperationException>(() => PathSafety.EnsureLeafFileName(input, "test"));
    }

    [Theory]
    [InlineData("C:\\escape\\file.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("../escape.txt")]
    public async Task ZipExtractorRejectsWindowsAndUnixTraversal(string entryName)
    {
        var zip = CreateZip((entryName, "bad"));
        var output = Path.Combine(_root, "output");
        await Assert.ThrowsAsync<SecurityException>(() => new SafeZipExtractor(_logger).ExtractAsync(zip, output));
    }

    [Fact]
    public async Task ZipExtractorRejectsDuplicateCaseVariants()
    {
        var zip = CreateZip(("Mods/Reader.dll", "first"), ("mods/reader.dll", "second"));
        var output = Path.Combine(_root, "output");
        await Assert.ThrowsAsync<SecurityException>(() => new SafeZipExtractor(_logger).ExtractAsync(zip, output));
    }

    [Fact]
    public async Task ZipExtractorRejectsDuplicateResolvedPaths()
    {
        var zip = CreateZip(("Mods/Reader.dll", "first"), ("Mods/./Reader.dll", "second"));
        var output = Path.Combine(_root, "output");
        await Assert.ThrowsAsync<SecurityException>(() => new SafeZipExtractor(_logger).ExtractAsync(zip, output));
    }

    [Fact]
    public async Task SteamDetectorFindsInstalledWindowsGameInLinuxLibrary()
    {
        var steamRoot = Path.Combine(_root, "Steam");
        var library = Path.Combine(_root, "OtherLibrary");
        var steamapps = Path.Combine(library, "steamapps");
        var gameFolder = Path.Combine(steamapps, "common", "Example Game");
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
        Directory.CreateDirectory(gameFolder);
        var protonPrefix = Path.Combine(steamapps, "compatdata", "123", "pfx");
        Directory.CreateDirectory(protonPrefix);
        File.WriteAllText(Path.Combine(gameFolder, "Game.exe"), "test");
        File.WriteAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\"1\"\n{{\n\"path\" \"{library}\"\n}}\n}}");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_123.acf"),
            "\"AppState\"\n{\n\"appid\" \"123\"\n\"installdir\" \"Example Game\"\n\"StateFlags\" \"4\"\n}");

        var game = new GameDefinition
        {
            GameId = "example",
            DisplayName = "Example",
            SteamAppId = "123",
            ExeName = "Game.exe"
        };
        var detector = new SteamDetector(new GameVerifier(_logger), _logger, steamRoot);
        var installs = await detector.DetectInstalledGamesAsync([game], "test-plugin");

        var install = Assert.Single(installs);
        Assert.Equal(gameFolder, install.InstallPath);
        Assert.Equal(library, install.SteamLibraryPath);
        Assert.Equal(steamRoot, install.SteamRootPath);
        Assert.Equal(protonPrefix, install.ProtonPrefixPath);
    }

    [Fact]
    public void GameVerifierAcceptsWindowsSeparatorsForSteamGameFilesOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        var folder = Path.Combine(_root, "nested-game");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "bin", "Game.exe"), "test");
        File.WriteAllText(Path.Combine(folder, "bin", "loader.dll"), "test");
        var game = new GameDefinition
        {
            GameId = "example", DisplayName = "Example", ExeName = "bin\\Game.exe",
            ProbeRules = [new PathProbeRule { Type = "fileExists", RelativePath = "bin\\loader.dll" }]
        };

        Assert.True(new GameVerifier(_logger).VerifyInstallPath(game, folder));
    }

    [Fact]
    public void NativeLinuxExecutableCanIdentifyGameWithoutWindowsExecutable()
    {
        if (!OperatingSystem.IsLinux()) return;
        var folder = Path.Combine(_root, "native-game");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "bin", "Game.x86_64"), "test");
        var game = new GameDefinition
        {
            GameId = "native-example", DisplayName = "Native example",
            ExeName = "Game.exe", LinuxExeName = "bin/Game.x86_64"
        };

        Assert.True(new GameVerifier(_logger).VerifyInstallPath(game, folder));
        game = new GameDefinition
        {
            GameId = "native-example", DisplayName = "Native example",
            LinuxExeName = "../outside", ExeName = null
        };
        Assert.False(new GameVerifier(_logger).VerifyInstallPath(game, folder));
    }

    [Fact]
    public void DurableFileReplacesExistingReplayMarkerOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "registry-highwater.json");
        DurableFile.Write(path, "first");
        DurableFile.Write(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task SameAuthorProtonIdentityImportPreservesLaunchAndBackupOwnership()
    {
        if (!OperatingSystem.IsLinux()) return;
        var oldId = "old-" + Guid.NewGuid().ToString("N");
        var newId = "current-" + Guid.NewGuid().ToString("N");
        var setupRoot = Path.Combine(_root, "setups");
        var receipts = new ReceiptStore(_logger, Path.Combine(_root, "receipts"));
        var gameDir = Path.Combine(_root, "game");
        var prefix = Path.Combine(_root, "prefix");
        var backupFolder = Path.Combine(_root, "original-backups");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(prefix);
        Directory.CreateDirectory(backupFolder);
        var ownedPath = Path.Combine(prefix, "ReloadedII.json");
        File.WriteAllText(ownedPath, "working pointer");
        var state = new ProtonSteamSetupState(oldId, "author", "1.0", gameDir, "123", prefix,
            new SteamLaunchOptionsPlan("", "working Steam launch option"),
            [new OwnedProtonConfigFile(ownedPath,
                Convert.ToBase64String(Encoding.UTF8.GetBytes("working pointer")), null, null)]);
        var oldSetupPath = Path.Combine(setupRoot, "author", oldId + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(oldSetupPath)!);
        var payload = JsonSerializer.Serialize(state, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(oldSetupPath, JsonSerializer.Serialize(new
        {
            formatVersion = 2,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            payload = JsonSerializer.Deserialize<JsonElement>(payload)
        }));
        await receipts.SaveAsync(new InstallReceipt
        {
            GameId = oldId, PluginId = "author", InstalledVersion = "1.0",
            TargetPlatform = ReleaseTarget.Proton, InstalledAt = DateTime.UtcNow,
            Changes = [], BackupFolder = backupFolder, ManifestHash = "test"
        });
        var install = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = newId, DisplayName = "Example", SteamAppId = "123", ExeName = "Game.exe"
            },
            PluginId = "author", InstallPath = gameDir, ProtonPrefixPath = prefix
        };

        var backup = await new ProtonSetupIdentityMigration(setupRoot, receipts, _logger,
                Path.Combine(_root, "identity-backups"))
            .ImportAsync(install, state);

        Assert.True(File.Exists(Path.Combine(backup, "setup.json")));
        Assert.True(File.Exists(Path.Combine(backup, "receipt.json")));
        Assert.False(File.Exists(oldSetupPath));
        Assert.Null(await receipts.LoadAsync(oldId, "author"));
        Assert.Equal(backupFolder, (await receipts.LoadAsync(newId, "author"))!.BackupFolder);
        Assert.Equal("working Steam launch option", Assert.Single(ProtonSteamSetupLookup.Find(setupRoot,
            "123", gameDir)).Plan.Installed);
        Assert.Equal("working pointer", File.ReadAllText(ownedPath));
    }

    [Fact]
    public void SteamExecutableResolverPrefersUniqueGameTitleOverModLauncher()
    {
        if (!OperatingSystem.IsLinux()) return;
        var folder = Path.Combine(_root, "installed-game");
        Directory.CreateDirectory(folder);
        var pe = typeof(SteamGameExecutableResolver).Assembly.Location;
        File.Copy(pe, Path.Combine(folder, "Digimon Story Time Stranger.exe"));
        File.Copy(pe, Path.Combine(folder, "DSTS_Launcher.exe"));
        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "dsts", DisplayName = "Digimon Story Time Stranger", SteamAppId = "1984270"
            },
            PluginId = "author", InstallPath = folder
        };

        var resolved = SteamGameExecutableResolver.Resolve(game);

        Assert.Equal("Digimon Story Time Stranger.exe", resolved.Game.ExeName);
        Assert.Null(game.Game.ExeName);
    }

    [Fact]
    public void SteamExecutableResolverRefusesAmbiguousGameFolder()
    {
        if (!OperatingSystem.IsLinux()) return;
        var folder = Path.Combine(_root, "ambiguous-game");
        Directory.CreateDirectory(folder);
        var pe = typeof(SteamGameExecutableResolver).Assembly.Location;
        File.Copy(pe, Path.Combine(folder, "Launcher.exe"));
        File.Copy(pe, Path.Combine(folder, "Game.exe"));
        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "example", DisplayName = "Example", SteamAppId = "123"
            },
            PluginId = "author", InstallPath = folder
        };

        Assert.Throws<InvalidOperationException>(() => SteamGameExecutableResolver.Resolve(game));
    }

    [Fact]
    public void SteamExecutableResolverFindsUniqueNestedGameExecutable()
    {
        if (!OperatingSystem.IsLinux()) return;
        var folder = Path.Combine(_root, "nested-steam-game");
        var nested = Path.Combine(folder, "app_digister");
        Directory.CreateDirectory(nested);
        var pe = typeof(SteamGameExecutableResolver).Assembly.Location;
        File.Copy(pe, Path.Combine(nested, "Digimon Story CS.exe"));
        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "dscs", DisplayName = "Digimon Story Cyber Sleuth Complete Edition",
                SteamAppId = "1042550"
            },
            PluginId = "author", InstallPath = folder
        };

        Assert.Equal("app_digister/Digimon Story CS.exe",
            SteamGameExecutableResolver.Resolve(game).Game.ExeName);
    }

    [Fact]
    public void SteamExecutableResolverIgnoresUnityCrashHandler()
    {
        if (!OperatingSystem.IsLinux()) return;
        var folder = Path.Combine(_root, "unity-game");
        Directory.CreateDirectory(folder);
        var pe = typeof(SteamGameExecutableResolver).Assembly.Location;
        File.Copy(pe, Path.Combine(folder, "masterduel.exe"));
        File.Copy(pe, Path.Combine(folder, "UnityCrashHandler64.exe"));
        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "masterduel", DisplayName = "Yu-Gi-Oh! Master Duel",
                SteamAppId = "1449850"
            },
            PluginId = "author", InstallPath = folder
        };

        var resolved = SteamGameExecutableResolver.Resolve(game);

        Assert.Equal("masterduel.exe", resolved.Game.ExeName);
    }

    [Fact]
    public async Task SteamDetectorRejectsManifestPathOutsideCommonFolder()
    {
        var steamRoot = Path.Combine(_root, "Steam");
        var steamapps = Path.Combine(steamRoot, "steamapps");
        var common = Path.Combine(steamapps, "common");
        var outside = Path.Combine(steamapps, "outside");
        Directory.CreateDirectory(common);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Game.exe"), "test");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_123.acf"),
            "\"AppState\"\n{\n\"appid\" \"123\"\n\"installdir\" \"../outside\"\n\"StateFlags\" \"4\"\n}");

        var game = new GameDefinition
        {
            GameId = "example",
            DisplayName = "Example",
            SteamAppId = "123",
            ExeName = "Game.exe"
        };
        var detector = new SteamDetector(new GameVerifier(_logger), _logger, steamRoot);
        var installs = await detector.DetectInstalledGamesAsync([game], "test-plugin");

        Assert.Empty(installs);
    }

    [Fact]
    public async Task DependencyCheckerRejectsWindowsTraversalOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        var game = new GameDefinition
        {
            GameId = "example",
            DisplayName = "Example",
            Dependencies =
            [
                new Dependency
                {
                    Id = "loader",
                    Type = "framework",
                    Check = new DependencyCheck { FilePath = "..\\outside\\loader.dll" }
                }
            ]
        };
        var install = new GameInstall
        {
            Game = game,
            PluginId = "test-plugin",
            InstallPath = Path.Combine(_root, "Game")
        };

        var statuses = await new DependencyChecker(_logger).CheckAsync(install);

        var status = Assert.Single(statuses);
        Assert.Equal(DependencyStatusKind.Missing, status.Status);
        Assert.Equal("Invalid check path", status.Details);
    }

    [Fact]
    public async Task PrismBridgeCacheRequiresMatchingArchivesAndDetectsChangedFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        var linuxEntries = new List<(string Name, string Content)>();
        foreach (var name in new[] { "prism_orca_bridge", "prism_speech_dispatcher_bridge", "prism_spiel_bridge" })
        {
            linuxEntries.Add(($"dynamic/release/wine/placeholders/{name}.dll", "placeholder-" + name));
            linuxEntries.Add(($"dynamic/release/wine/{name}.dll.so", "host-" + name));
        }
        var linuxZip = CreateZip(linuxEntries.ToArray());
        var windowsZip = CreateZip(
            ("dynamic/release/bin/prism.dll", "windows-prism"),
            ("dynamic/release/bin/tolk.dll", "windows-tolk"),
            ("NOTICE", "Prism notices"),
            ("LICENSES/prism/mpl-2.0.txt", "MPL"));
        var release = new PrismBridgeRelease(
            "test", "x64", new Uri("https://example.org/linux.zip"), Hash(linuxZip),
            new Uri("https://example.org/windows.zip"), Hash(windowsZip));
        var cache = new PrismBridgeProvisioner(new HttpClient(), Path.Combine(_root, "cache"));

        var bundle = await cache.ProvisionFromArchivesAsync(release, linuxZip, windowsZip);
        Assert.Equal("windows-prism", File.ReadAllText(bundle.WindowsPrismDll));
        Assert.Equal("windows-tolk", File.ReadAllText(bundle.WindowsTolkDll));
        Assert.True(File.Exists(Path.Combine(bundle.WindowsPlaceholdersDirectory, "prism_orca_bridge.dll")));
        Assert.True(File.Exists(Path.Combine(bundle.HostModulesDirectory, "prism_orca_bridge.dll.so")));
        Assert.True(File.Exists(Path.Combine(bundle.NoticesDirectory, "LICENSES", "prism", "mpl-2.0.txt")));

        File.WriteAllText(bundle.WindowsPrismDll, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.ProvisionFromArchivesAsync(release, linuxZip, windowsZip));
    }

    [Fact]
    public async Task PrismBridgeCacheRejectsUnpinnedArchiveBeforeWritingCache()
    {
        if (!OperatingSystem.IsLinux()) return;
        var linuxZip = CreateZip(("irrelevant", "linux"));
        var windowsZip = CreateZip(("irrelevant", "windows"));
        var release = new PrismBridgeRelease(
            "test", "x64", new Uri("https://example.org/linux.zip"), new string('0', 64),
            new Uri("https://example.org/windows.zip"), Hash(windowsZip));
        var cacheRoot = Path.Combine(_root, "cache");
        var cache = new PrismBridgeProvisioner(new HttpClient(), cacheRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.ProvisionFromArchivesAsync(release, linuxZip, windowsZip));
        Assert.False(Directory.Exists(Path.Combine(cacheRoot, "prism-test-x64-shim1")));
    }

    [Fact]
    public void LegacyPackagesStayWindowsOnly()
    {
        Assert.Equal(ReleaseTarget.Windows, ReleaseTarget.Normalize(null));
        Assert.Equal(OperatingSystem.IsWindows(), ReleaseTarget.IsSupportedHere(null));
        if (OperatingSystem.IsLinux())
            Assert.Throws<PlatformNotSupportedException>(() => ReleaseTarget.EnsureSupportedHere(null));
    }

    [Theory]
    [InlineData("proton")]
    [InlineData("linux")]
    public void ExplicitLinuxTargetsAreAcceptedOnLinux(string target)
    {
        Assert.Equal(OperatingSystem.IsLinux(), ReleaseTarget.IsSupportedHere(target));
        if (OperatingSystem.IsLinux()) ReleaseTarget.EnsureSupportedHere(target);
        Assert.Equal(target, ReleaseTarget.Normalize(target));
    }

    [Fact]
    public void UnknownPackageTargetFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => ReleaseTarget.Normalize("wine"));
        Assert.Throws<InvalidOperationException>(() =>
            new ManifestParser(_logger).Parse("""
                {"gameId":"game","pluginId":"plugin","modVersion":"1.0","targetPlatform":"wine"}
                """));
    }

    [Fact]
    public void UnknownDependencyTargetFailsBeforeInstall()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ManifestParser(_logger).Parse("""
                {"gameId":"game","pluginId":"plugin","modVersion":"1.0",
                 "dependencies":[{"id":"loader","type":"framework","targetPlatforms":["wine"]}]}
                """));
    }

    [Theory]
    [InlineData("\"launchMode\":\"unknown\"")]
    [InlineData("\"launchMode\":\"direct\",\"wineDllOverrides\":[\"version=n,b\",\"version.dll=n\"]")]
    [InlineData("\"launchMode\":\"direct\",\"windowsDesktopRuntimeVersion\":\"6.0.36\"")]
    [InlineData("\"launchMode\":\"direct\",\"launcherPath\":\"Launcher.exe\"")]
    public void ProtonLaunchRejectsAmbiguousOrUnverifiedSetup(string launchFields)
    {
        var json = """
            {"gameId":"game","pluginId":"plugin","modVersion":"1.0", "targetPlatform":"proton",
             "protonLaunch":{"steamAppId":"123","gameDisplayName":"Game","gameExecutable":"Game.exe",
            """ + launchFields + "}}";
        Assert.Throws<InvalidOperationException>(() => new ManifestParser(_logger).Parse(json));
    }

    [Fact]
    public void ProtonLaunchAcceptsDirectModeWithPinnedRuntimeAndNoLauncher()
    {
        var json = """
            {"gameId":"game","pluginId":"plugin","modVersion":"1.0", "targetPlatform":"proton",
             "protonLaunch":{"steamAppId":"123","gameDisplayName":"Game","gameExecutable":"Game.exe",
                "launchMode":"direct","wineDllOverrides":["version=n,b"],
                "windowsDesktopRuntimeVersion":"6.0.36",
                "windowsDesktopRuntimeSha512":"
            """ + new string('a', 128) + "\"}}";
        var manifest = new ManifestParser(_logger).Parse(json);
        Assert.Null(manifest.ProtonLaunch!.LauncherPath);
        Assert.Equal("6.0.36", manifest.ProtonLaunch.WindowsDesktopRuntimeVersion);
    }

    [Fact]
    public void DirectReloadedLaunchCanUseAProxyInsteadOfAWindowsLauncher()
    {
        if (!OperatingSystem.IsLinux()) return;
        var manifest = new ManifestParser(_logger).Parse("""
            {"gameId":"game","pluginId":"plugin","modVersion":"1.0", "targetPlatform":"proton",
             "protonLaunch":{"steamAppId":"123","gameDisplayName":"Game","gameExecutable":"Game.exe",
                "launchMode":"direct","wineDllOverrides":["winmm=n,b"],
                "reloadedRoot":"Reloaded-II","reloadedModId":"Example.Accessibility"}}
            """);
        var gameDirectory = Path.Combine(_root, "Game");
        var wrapper = Path.Combine(_root, "steam-proton-launch");
        Directory.CreateDirectory(gameDirectory);
        File.WriteAllText(Path.Combine(gameDirectory, "Game.exe"), "game");
        File.WriteAllText(wrapper, "wrapper");

        var plan = SteamLaunchOptionsPlan.Create(null, wrapper,
            Path.Combine(gameDirectory, "Game.exe"), gameDirectory, manifest.ProtonLaunch!);

        Assert.Contains("--amm-v1 direct", plan.Installed);
        Assert.Contains("'winmm=n,b' 1 -- %command%", plan.Installed);
    }

    [Fact]
    public void DirectWineOverrideRequiresAnOwnedProxyInThePackage()
    {
        var zip = CreateZip(("manifest.json", """
            {"gameId":"game","pluginId":"plugin","modVersion":"1.0", "targetPlatform":"proton",
             "protonLaunch":{"steamAppId":"123","gameDisplayName":"Game","gameExecutable":"Game.exe",
                "launchMode":"direct","wineDllOverrides":["version=n,b"]}}
            """));
        using var stream = File.OpenRead(zip);

        var report = PluginPackageValidation.Validate(stream, "plugin", "game", "1.0", _logger);

        Assert.Contains(report.Errors, error => error.Contains("proxy 'version.dll'", StringComparison.Ordinal));
        Assert.Contains(report.Errors, error => error.Contains("install action", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectWineOverrideCanOwnAProxyInAGameSubfolder()
    {
        var zip = CreateZip(
            ("manifest.json", """
                {"gameId":"game","pluginId":"plugin","modVersion":"1.0", "targetPlatform":"proton",
                 "protonLaunch":{"steamAppId":"123","gameDisplayName":"Game","gameExecutable":"Game.exe",
                    "launchMode":"direct","wineDllOverrides":["freetype=n,b"],
                    "wineDllProxyPaths":{"freetype":"app_digister/freetype.dll"}},
                 "installActions":[{"type":"copyFolder","sourceDir":"app_digister","targetDir":"app_digister"}]}
                """),
            ("files/app_digister/freetype.dll", "loader"));
        using var stream = File.OpenRead(zip);

        var report = PluginPackageValidation.Validate(stream, "plugin", "game", "1.0", _logger);

        Assert.True(report.IsValid, string.Join("; ", report.Errors));
    }

    [Fact]
    public void SteamLaunchPlanPreservesExistingOptionsAndRestoresOnlyItsOwnValue()
    {
        if (!OperatingSystem.IsLinux()) return;
        var game = Path.Combine(_root, "Game's Folder", "Game.exe");
        var launcher = Path.Combine(_root, "Game's Folder", "Launcher.exe");
        var wrapper = Path.Combine(_root, "manager", "steam-proton-launch");
        var bridges = Path.Combine(_root, "prism", "wine");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        Directory.CreateDirectory(Path.GetDirectoryName(wrapper)!);
        Directory.CreateDirectory(bridges);
        File.WriteAllText(game, "game");
        File.WriteAllText(launcher, "launcher");
        File.WriteAllText(wrapper, "wrapper");

        var original = "PROTON_LOG=1 gamemoderun %command% --custom-arg";
        var plan = SteamLaunchOptionsPlan.Create(original, wrapper, game, launcher, bridges);

        Assert.StartsWith("PROTON_LOG=1 gamemoderun '", plan.Installed);
        Assert.Contains("Game'\\''s Folder", plan.Installed);
        Assert.EndsWith("-- %command% --custom-arg", plan.Installed);
        Assert.Equal(original, plan.Restore(plan.Installed));
        Assert.Throws<InvalidOperationException>(() => plan.Restore(plan.Installed + " -changed"));
    }

    [Fact]
    public void SteamLaunchPlanRejectsAmbiguousCommandMarker()
    {
        if (!OperatingSystem.IsLinux()) return;
        var file = Path.Combine(_root, "game.exe");
        Directory.CreateDirectory(_root);
        File.WriteAllText(file, "test");
        Assert.Throws<InvalidOperationException>(() =>
            SteamLaunchOptionsPlan.Create("'%command%'", file, file, file, _root));
        Assert.Throws<InvalidOperationException>(() =>
            SteamLaunchOptionsPlan.Create("%command% %command%", file, file, file, _root));
    }

    [Fact]
    public async Task ProtonRuntimeCheckUsesTheSelectedGamePrefix()
    {
        if (!OperatingSystem.IsLinux()) return;
        var steam = Path.Combine(_root, "Steam");
        var compat = Path.Combine(steam, "steamapps", "compatdata", "123");
        var dotnet = Path.Combine(compat, "pfx", "drive_c", "Program Files", "dotnet", "dotnet.exe");
        var proton = Path.Combine(_root, "proton");
        Directory.CreateDirectory(Path.GetDirectoryName(dotnet)!);
        File.WriteAllText(dotnet, "stub");
        File.WriteAllText(proton, """
            #!/bin/sh
            test "$STEAM_COMPAT_APP_ID" = 123 || exit 10
            test "$STEAM_COMPAT_DATA_PATH" = "$EXPECTED_COMPAT" || exit 11
            printf 'Microsoft.NETCore.App 9.0.20 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]\n'
            printf 'Microsoft.WindowsDesktop.App 9.0.20 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]\n'
            printf 'Microsoft.NETCore.App 6.0.36 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]\n'
            printf 'Microsoft.WindowsDesktop.App 6.0.36 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]\n'
            """ + "\n");
        File.SetUnixFileMode(proton, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var old = Environment.GetEnvironmentVariable("EXPECTED_COMPAT");
        Environment.SetEnvironmentVariable("EXPECTED_COMPAT", compat);
        try
        {
            var provisioner = new ProtonWindowsDesktopRuntime(new HttpClient(), Path.Combine(_root, "cache"));
            Assert.True(await provisioner.IsInstalledAsync(new ProtonContext(proton, steam, compat, "123")));
            Assert.False(await provisioner.EnsureAsync(new ProtonContext(proton, steam, compat, "123")));
            Assert.True(await provisioner.IsInstalledAsync(new ProtonContext(proton, steam, compat, "123"), "6.0.30"));
            Assert.False(await provisioner.IsInstalledAsync(new ProtonContext(proton, steam, compat, "123"), "8.0.1"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXPECTED_COMPAT", old);
        }
    }

    [Fact]
    public void SteamConfigEditorRestoresExactPreviousOptionAndOtherSettings()
    {
        const string original = """
            "UserLocalConfigStore"
            {
                "Software"
                {
                    "Valve"
                    {
                        "Steam"
                        {
                            "apps"
                            {
                                "123"
                                {
                                    "LaunchOptions" "PROTON_LOG=1 %command%"
                                    "OtherSetting" "leave this alone"
                                }
                            }
                        }
                    }
                }
            }
            """ + "\n";
        var plan = new SteamLaunchOptionsPlan("PROTON_LOG=1 %command%", "PROTON_LOG=1 '/wrapper' -- %command%")
        {
            WasPresent = true
        };

        var installed = SteamLocalConfigEditor.Install(original, "123", plan);

        Assert.Equal(plan.Installed, SteamLocalConfigEditor.Read(installed, "123"));
        Assert.Equal(original, SteamLocalConfigEditor.Restore(installed, "123", plan));
        Assert.Throws<InvalidOperationException>(() =>
            SteamLocalConfigEditor.Restore(installed.Replace("/wrapper", "/other"), "123", plan));
    }

    [Fact]
    public void SteamConfigEditorRemovesNewOptionOnRestore()
    {
        const string original = """
            "UserLocalConfigStore"
            {
                "Software" { "Valve" { "Steam" { "apps" {
                    "123"
                    {
                        "OtherSetting" "preserved"
                    }
                } } } }
            }
            """ + "\n";
        var plan = new SteamLaunchOptionsPlan("", "'/wrapper' -- %command%");

        var installed = SteamLocalConfigEditor.Install(original, "123", plan);

        Assert.Equal(plan.Installed, SteamLocalConfigEditor.Read(installed, "123"));
        Assert.Equal(original, SteamLocalConfigEditor.Restore(installed, "123", plan));
    }

    [Fact]
    public void SteamConfigEditorCreatesAndRemovesMissingAppBlock()
    {
        const string original = "\"UserLocalConfigStore\"\n{\n\t\"Software\" { \"Valve\" { \"Steam\" { \"apps\"\n\t\t{\n\t\t}\n } } }\n}\n";
        var plan = new SteamLaunchOptionsPlan("", "'/wrapper' -- %command%")
        {
            AppWasPresent = false
        };

        var installed = SteamLocalConfigEditor.Install(original, "123", plan);

        Assert.Equal(plan.Installed, SteamLocalConfigEditor.Read(installed, "123"));
        Assert.Equal(original, SteamLocalConfigEditor.Restore(installed, "123", plan));
    }

    [Fact]
    public void SteamConfigEditorKeepsSettingsSteamAddedAfterAppBlockCreation()
    {
        const string original = "\"UserLocalConfigStore\"\n{\n\"Software\" { \"Valve\" { \"Steam\" { \"apps\"\n{\n}\n} } }\n}\n";
        var plan = new SteamLaunchOptionsPlan("", "'/wrapper' -- %command%")
        {
            AppWasPresent = false
        };
        var installed = SteamLocalConfigEditor.Install(original, "123", plan);
        var changed = installed.Replace("\t\t\"LaunchOptions\"", "\t\t\"OtherSetting\" \"new\"\n\t\t\"LaunchOptions\"", StringComparison.Ordinal);

        var restored = SteamLocalConfigEditor.Restore(changed, "123", plan);

        Assert.Null(SteamLocalConfigEditor.Read(restored, "123"));
        Assert.Contains("\"OtherSetting\" \"new\"", restored);
    }

    [Fact]
    public async Task SteamConfigStoreUsesAtomicBytesAndRequiresSteamToBeClosed()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string vdf = "\"UserLocalConfigStore\"\n{\n\"Software\" { \"Valve\" { \"Steam\" { \"apps\" {\n" +
                           "\"123\"\n{\n\"LaunchOptions\" \"%command%\"\n}\n" +
                           "} } } }\n}\n";
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "localconfig.vdf");
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(vdf)).ToArray();
        await File.WriteAllBytesAsync(path, original);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var plan = new SteamLaunchOptionsPlan("%command%", "'/wrapper' -- %command%")
        {
            WasPresent = true
        };
        var blocked = new SteamLocalConfigStore(path, () => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.InstallAsync("123", plan));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));

        var store = new SteamLocalConfigStore(path, () => false);
        await store.InstallAsync("123", plan);
        Assert.Equal(plan.Installed, SteamLocalConfigEditor.Read(
            System.Text.Encoding.UTF8.GetString((await File.ReadAllBytesAsync(path))[3..]), "123"));
        await store.RestoreAsync("123", plan);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    private static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private string CreateZip(params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
