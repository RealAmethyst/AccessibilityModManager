using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.Authoring.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.PortableTests;

public sealed class XivLauncherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-xiv-test-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger logger = new LoggerConfiguration().CreateLogger();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly XivLauncherConfig Plugin = new()
    {
        InternalName = "XivAccess", PluginAssembly = "XivAccess.dll",
        WorkingPluginId = "507b48de-3362-4471-86f4-baa7e56d9387"
    };
    private static Dependency Xlm => XlmDependencyPreset.Create();

    public XivLauncherTests() => Directory.CreateDirectory(root);

    [Fact]
    public void LinuxSteamOverrideDoesNotChangeWindowsAndIsNotSerializedAsEffectiveId()
    {
        var game = new GameDefinition { GameId = "xiv", DisplayName = "XIV", LinuxSteamAppId = "39210", ExeName = "XIVLauncher.exe" };
        Assert.Null(game.GetSteamAppId(false));
        Assert.Equal("39210", game.GetSteamAppId(true));
        var json = JsonSerializer.Serialize(game, JsonOptions);
        Assert.DoesNotContain("effectiveSteamAppId", json);
        Assert.Equal("39210", JsonSerializer.Deserialize<GameDefinition>(json, JsonOptions)!.LinuxSteamAppId);
        var shared = new GameDefinition { GameId = "other", DisplayName = "Other", SteamAppId = "123" };
        Assert.Equal("123", shared.GetSteamAppId(true));
    }

    [Theory]
    [InlineData(true, 4, true)]
    [InlineData(false, 516, true)]
    [InlineData(false, 1024, false)]
    public async Task SteamDetectsXivDepotUsingLinuxIdDespiteWindowsLauncherExecutable(
        bool gameDownloaded, int stateFlags, bool expected)
    {
        var steam = Path.Combine(root, "Steam");
        var gamePath = Path.Combine(steam, "steamapps", "common", "FINAL FANTASY XIV Online");
        Directory.CreateDirectory(Path.Combine(gamePath, "game"));
        Directory.CreateDirectory(Path.Combine(gamePath, "boot"));
        File.WriteAllText(Path.Combine(gamePath, "game", "ffxivgame.ver"), "2012.01.01.0000.0000");
        if (gameDownloaded)
            File.WriteAllText(Path.Combine(gamePath, "game", "ffxiv_dx11.exe"), "game");
        File.WriteAllText(Path.Combine(gamePath, "boot", "ffxivboot.exe"), "boot");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_39210.acf"),
            "\"AppState\" { \"appid\" \"39210\" \"installdir\" \"FINAL FANTASY XIV Online\" \"StateFlags\" \"" + stateFlags + "\" }");
        var definition = new GameDefinition
        {
            GameId = "xiv", DisplayName = "XIV", LinuxSteamAppId = "39210",
            ExeName = "XIVLauncher.exe", Dependencies = [Xlm]
        };
        var found = await new SteamDetector(new GameVerifier(logger), logger, steam)
            .DetectInstalledGamesAsync([definition], "author");
        if (expected)
            Assert.Equal(gamePath, Assert.Single(found).InstallPath);
        else
            Assert.Empty(found);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void XivDetectionRejectsMissingBootLauncherOrGameDirectory(bool bootExists, bool gameExists)
    {
        if (gameExists) Directory.CreateDirectory(Path.Combine(root, "game"));
        if (bootExists)
        {
            Directory.CreateDirectory(Path.Combine(root, "boot"));
            File.WriteAllText(Path.Combine(root, "boot", "ffxivboot.exe"), "boot");
        }
        var game = new GameDefinition
        {
            GameId = "xiv", DisplayName = "XIV", LinuxSteamAppId = "39210",
            ExeName = "XIVLauncher.exe", Dependencies = [Xlm]
        };
        Assert.False(new GameVerifier(logger).VerifyInstallPath(game, root));
    }

    [Fact]
    public void RegistrationPreservesOtherPluginsAndRemovesOnlyItsOwnEntries()
    {
        var other = withName("Other", "Other.dll", "c13a2c08-df3a-4df1-884e-18756c44ac07");
        var first = XivLauncherSettings.Install(null, @"Z:\other\Other.dll", other);
        var initial = JsonNode.Parse(first.Json)!;
        initial["SomeUnrelatedSetting"] = "keep me";
        var own = XivLauncherSettings.Install(initial.ToJsonString(), @"Z:\mods\XivAccess.dll", Plugin);
        var changed = JsonNode.Parse(own.Json)!;
        changed["SomeUnrelatedSetting"] = "changed by user";
        var removed = XivLauncherSettings.Remove(changed.ToJsonString(), own.Registration);
        XivLauncherSettings.Verify(removed, first.Registration);
        Assert.Equal("changed by user", JsonNode.Parse(removed)!["SomeUnrelatedSetting"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(removed)!["DevMode"]!.GetValue<bool>());
        Assert.DoesNotContain("Z:\\\\mods", removed);
        Assert.Throws<InvalidOperationException>(() => XivLauncherSettings.Install(first.Json, @"Z:\other\Other.dll", other));
    }

    private static XivLauncherConfig withName(string name, string assembly, string id) => new()
        { InternalName = name, PluginAssembly = assembly, WorkingPluginId = id };

    [Fact]
    public void CompatibilityMappingChangesOnlyChosenGameAndRejectsAmbiguousBlocks()
    {
        const string original = "\"InstallConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" {\n\"Keep\" \"unchanged\"\n\"CompatToolMapping\" { \"1\" { \"name\" \"proton\" } }\n} } } }";
        string[] path = ["InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping", "39210"];
        const string mapping = "\"39210\" { \"name\" \"xlm\" \"priority\" \"250\" \"config\" \"\" }";
        var edited = SteamLocalConfigEditor.SetBlock(original, path, mapping);
        Assert.Contains("\"Keep\" \"unchanged\"", edited);
        Assert.Contains("\"1\" { \"name\" \"proton\" }", edited);
        Assert.True(SteamLocalConfigEditor.BlocksEqual(mapping,
            "\"39210\" {\n\"config\" \"\" \"priority\" \"250\" \"name\" \"xlm\"\n}"));
        Assert.Null(SteamLocalConfigEditor.ReadBlock(SteamLocalConfigEditor.SetBlock(edited, path, null), path));
        Assert.Throws<InvalidDataException>(() => SteamLocalConfigEditor.ReadBlock(edited.Replace(mapping, mapping + mapping), path));
    }

    [Fact]
    public async Task InstallUpdateAndUninstallRestoreSteamAndKeepUserSettings()
    {
        var fixture = await CreateFixture();
        await fixture.Setup.InstallAsync(fixture.Game, fixture.Release, fixture.Package, fixture.Account, false);
        var paths = XivLauncherPaths.Resolve(fixture.Game, root);
        Assert.Equal("first", File.ReadAllText(Path.Combine(paths.PayloadRoot, "XivAccess.dll")));
        Assert.NotNull(await fixture.Receipts.LoadAsync("xiv", "author"));
        await fixture.Setup.EnsureReadyAsync(fixture.Game);
        var json = JsonNode.Parse(File.ReadAllText(paths.ConfigPath))!;
        json["UserSettingAddedAfterInstall"] = "preserve";
        File.WriteAllText(paths.ConfigPath, json.ToJsonString());
        var updated = await BuildPackage("2.0", "second");
        await fixture.Setup.InstallAsync(fixture.Game, updated.Release, updated.Path, fixture.Account, true);
        Assert.Equal("second", File.ReadAllText(Path.Combine(paths.PayloadRoot, "XivAccess.dll")));
        await fixture.Setup.UninstallAsync(fixture.Game);
        Assert.Equal(fixture.OriginalAccount, File.ReadAllText(fixture.Account));
        Assert.True(SteamLocalConfigEditor.BlocksEqual(fixture.OriginalMapping,
            SteamLocalConfigEditor.ReadBlock(File.ReadAllText(paths.GlobalSteamConfig), MappingPath)));
        Assert.Equal("preserve", JsonNode.Parse(File.ReadAllText(paths.ConfigPath))!["UserSettingAddedAfterInstall"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(paths.PayloadRoot, "XivAccess.dll")));
        Assert.Null(await fixture.Receipts.LoadAsync("xiv", "author"));
        Assert.True(File.Exists(Path.Combine(XlmInstaller.ToolDirectory(paths.SteamRoot), "xlm")));
    }

    [Fact]
    public async Task ChangedSteamChoiceStopsUninstallWithoutRemovingPlugin()
    {
        var f = await CreateFixture();
        await f.Setup.InstallAsync(f.Game, f.Release, f.Package, f.Account, false);
        var paths = XivLauncherPaths.Resolve(f.Game, root);
        File.WriteAllText(paths.GlobalSteamConfig, SteamLocalConfigEditor.SetBlock(
            File.ReadAllText(paths.GlobalSteamConfig), MappingPath, "\"39210\" { \"name\" \"different-tool\" }"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Setup.UninstallAsync(f.Game));
        Assert.True(File.Exists(Path.Combine(paths.PayloadRoot, "XivAccess.dll")));
        Assert.NotNull(await f.Receipts.LoadAsync("xiv", "author"));
    }

    [Fact]
    public async Task BadPackageHashDoesNotWriteSettingsOrReceipt()
    {
        var f = await CreateFixture();
        await File.AppendAllTextAsync(f.Package, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Setup.InstallAsync(f.Game, f.Release, f.Package, f.Account, false));
        Assert.Equal(f.OriginalAccount, File.ReadAllText(f.Account));
        Assert.Null(await f.Receipts.LoadAsync("xiv", "author"));
        Assert.False(File.Exists(XivLauncherPaths.Resolve(f.Game, root).ConfigPath));
    }

    [Fact]
    public async Task FailedPayloadVerificationRollsBackAndCanBeRetried()
    {
        var f = await CreateFixture();
        using (var zip = ZipFile.Open(f.Package, ZipArchiveMode.Update))
        {
            zip.GetEntry("files/XivAccess.dll")!.Delete();
        }
        // Hash matches the broken author's upload; package validation must still reject it.
        var broken = Release("1.0", f.Package);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Setup.InstallAsync(f.Game, broken, f.Package, f.Account, false));
        Assert.Equal(f.OriginalAccount, File.ReadAllText(f.Account));
        var good = await BuildPackage("1.1", "retry");
        await f.Setup.InstallAsync(f.Game, good.Release, good.Path, f.Account, false);
        await f.Setup.EnsureReadyAsync(f.Game);
    }

    [Fact]
    public async Task FailureBetweenSettingsWritesRollsBackPluginAndRegistration()
    {
        var failed = false;
        var configPath = Path.Combine(root, ".xlcore", "dalamudConfig.json");
        void CheckClosed()
        {
            if (!failed && File.Exists(configPath))
            {
                failed = true;
                throw new InvalidOperationException("Simulated Steam restart during setup");
            }
        }
        var f = await CreateFixture(CheckClosed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Setup.InstallAsync(f.Game, f.Release, f.Package, f.Account, false));
        Assert.True(failed);
        Assert.Equal(f.OriginalAccount, File.ReadAllText(f.Account));
        Assert.Null(await f.Receipts.LoadAsync("xiv", "author"));
        Assert.DoesNotContain(Plugin.WorkingPluginId, File.ReadAllText(configPath));
        Assert.False(File.Exists(Path.Combine(root, "setups", "author", "xiv.json")));
        await f.Setup.InstallAsync(f.Game, f.Release, f.Package, f.Account, false);
        await f.Setup.EnsureReadyAsync(f.Game);
    }

    [Fact]
    public async Task InterruptedRollbackKeepsJournalAndNextInstallRecovers()
    {
        var block = true;
        var configPath = Path.Combine(root, ".xlcore", "dalamudConfig.json");
        void CheckClosed()
        {
            if (block && File.Exists(configPath))
                throw new InvalidOperationException("Steam is running");
        }
        var f = await CreateFixture(CheckClosed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Setup.InstallAsync(f.Game, f.Release, f.Package, f.Account, false));
        Assert.True(File.Exists(Path.Combine(root, "setups", "author", "xiv.json")));
        Assert.NotNull(await f.Receipts.LoadAsync("xiv", "author"));
        block = false;
        await f.Setup.InstallAsync(f.Game, f.Release, f.Package, f.Account, true);
        await f.Setup.EnsureReadyAsync(f.Game);
        await f.Setup.UninstallAsync(f.Game);
        Assert.Equal(f.OriginalAccount, File.ReadAllText(f.Account));
    }

    [Fact]
    public async Task BuilderIncludesExplicitMetadataAndRejectsWrongPluginName()
    {
        var source = Path.Combine(root, "build-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "XivAccess.dll"), "plugin");
        File.WriteAllText(Path.Combine(source, "XivAccess.json"), "{\"InternalName\":\"XivAccess\"}");
        var output = Path.Combine(root, "built.zip");
        var builder = new XivLauncherPackageBuilder(new ManifestBuilderService(logger));
        await builder.BuildAsync(source, "xiv", "author", "1.0", [Xlm], output,
            "XivAccess.dll", "XivAccess", Plugin.WorkingPluginId);
        using (var stream = File.OpenRead(output))
            Assert.True(PluginPackageValidation.Validate(stream, "author", "xiv", "1.0", logger).IsValid);
        await Assert.ThrowsAsync<InvalidDataException>(() => builder.BuildAsync(source, "xiv", "author", "2.0", [Xlm], output,
            "XivAccess.dll", "WrongName", Plugin.WorkingPluginId));
    }

    [Theory]
    [InlineData(XivLauncherSpeech.Prism0173Sha256, "0.17.3")]
    [InlineData(XivLauncherSpeech.Prism0183Sha256, "0.18.3")]
    public void DetectsPrismWithoutAnAuthorVersionChoice(string hash, string version) =>
        Assert.Equal(version, XivLauncherSpeech.VersionForHash(hash.ToUpperInvariant()));

    [Fact]
    public async Task UnrecognizedPrismStopsPackagingBeforeItReplacesAnExistingPackage()
    {
        var source = Path.Combine(root, "unknown-prism");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "XivAccess.dll"), "plugin");
        File.WriteAllText(Path.Combine(source, "XivAccess.json"), "{\"InternalName\":\"XivAccess\"}");
        File.WriteAllText(Path.Combine(source, "prism.dll"), "unknown ABI");
        var output = Path.Combine(root, "existing.zip");
        File.WriteAllText(output, "existing package");
        await Assert.ThrowsAsync<InvalidDataException>(() => new XivLauncherPackageBuilder(new ManifestBuilderService(logger))
            .BuildAsync(source, "xiv", "author", "1.0", [Xlm], output, "XivAccess.dll", "XivAccess", Plugin.WorkingPluginId));
        Assert.Equal("existing package", File.ReadAllText(output));
        Assert.Equal("unknown ABI", File.ReadAllText(Path.Combine(source, "prism.dll")));
    }

    [Fact]
    public void XlmDependencyNeverAppliesToWindowsAndFlatpakIsExplicitlyRefused()
    {
        Assert.False(DependencyTargeting.AppliesTo(Xlm, ReleaseTarget.Windows));
        Assert.False(DependencyTargeting.AppliesTo(Xlm, ReleaseTarget.Proton));
        Assert.True(DependencyTargeting.AppliesTo(Xlm, ReleaseTarget.XivLauncher));
        var game = new GameInstall
        {
            Game = new GameDefinition { GameId = "xiv", DisplayName = "XIV", LinuxSteamAppId = "39210" },
            PluginId = "author", InstallPath = root,
            SteamRootPath = root + "/.var/app/com.valvesoftware.Steam/.local/share/Steam"
        };
        Assert.Throws<PlatformNotSupportedException>(() => XivLauncherPaths.Resolve(game, root));
    }

    [Fact]
    public void SteamAliasCannotHideAFlatpakInstallation()
    {
        var flatpak = Path.Combine(root, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        Directory.CreateDirectory(flatpak);
        var alias = Path.Combine(root, "steam-alias");
        Directory.CreateSymbolicLink(alias, flatpak);
        var game = new GameInstall
        {
            Game = new GameDefinition { GameId = "xiv", DisplayName = "XIV", LinuxSteamAppId = "39210" },
            PluginId = "author", InstallPath = root, SteamRootPath = alias
        };
        Assert.Throws<PlatformNotSupportedException>(() => XivLauncherPaths.Resolve(game, root));
    }

    private static readonly string[] MappingPath = ["InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping", "39210"];
    private async Task<(XivLauncherSetup Setup, GameInstall Game, ModRelease Release, string Package,
        string Account, string OriginalAccount, string OriginalMapping, ReceiptStore Receipts)> CreateFixture(Action? ensureClosed = null)
    {
        var steam = Path.Combine(root, "Steam");
        Directory.CreateDirectory(Path.Combine(steam, "config"));
        var mapping = "\"39210\" { \"name\" \"proton-test\" \"priority\" \"250\" \"config\" \"\" }";
        File.WriteAllText(Path.Combine(steam, "config", "config.vdf"),
            "\"InstallConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"CompatToolMapping\" { " + mapping + " } } } } }");
        var account = Path.Combine(steam, "userdata", "123", "config", "localconfig.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(account)!);
        const string accountText = "\"UserLocalConfigStore\"\n{\n\"Software\" { \"Valve\" { \"Steam\" { \"apps\"\n{\n\"39210\"\n{\n\"LaunchOptions\" \"PROTON_LOG=1 %command%\"\n}\n}\n} } }\n}\n";
        File.WriteAllText(account, accountText);
        var tool = XlmInstaller.ToolDirectory(steam);
        Directory.CreateDirectory(tool);
        foreach (var name in new[] { "xlm", "xlm.sh", "toolmanifest.vdf" }) File.WriteAllText(Path.Combine(tool, name), "fixture");
        File.WriteAllText(Path.Combine(tool, "compatibilitytool.vdf"), "\"compatibilitytools\" { \"compat_tools\" { \"xlm\" { \"display_name\" \"XLCore [XLM]\" } } }");
        var game = new GameInstall
        {
            Game = new GameDefinition { GameId = "xiv", DisplayName = "XIV", LinuxSteamAppId = "39210", ExeName = "XIVLauncher.exe", Dependencies = [Xlm] },
            PluginId = "author", InstallPath = Path.Combine(root, "Game"), SteamRootPath = steam, IsValid = true
        };
        Directory.CreateDirectory(game.InstallPath);
        var receipts = new ReceiptStore(logger, Path.Combine(root, "receipts"));
        var backup = new BackupManager(logger);
        var client = new HttpClient();
        var engine = new InstallerEngine(backup, new InstallActionExecutor(backup, logger),
            new InstallVerifier(logger), new ManifestParser(logger), new SafeZipExtractor(logger),
            receipts, new DependencyChecker(logger), new LifecycleScriptRunner(logger),
            new DependencyAutoInstaller(client, new DependencyReceiptStore(logger, Path.Combine(root, "dep-receipts")), logger),
            new GameVerifier(logger), logger);
        var setup = new XivLauncherSetup(engine, receipts, client, logger, Path.Combine(root, "setups"), root, ensureClosed ?? (() => { }));
        var package = await BuildPackage("1.0", "first");
        return (setup, game, package.Release, package.Path, account, accountText, mapping, receipts);
    }

    private async Task<(string Path, ModRelease Release)> BuildPackage(string version, string contents)
    {
        var source = Path.Combine(root, "source-" + version);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "XivAccess.dll"), contents);
        File.WriteAllText(Path.Combine(source, "XivAccess.json"), "{\"InternalName\":\"XivAccess\"}");
        var path = Path.Combine(root, version + ".zip");
        await new ManifestBuilderService(logger).BuildPackageAsync(source, "xiv", "author", version, [Xlm], path,
            targetPlatform: ReleaseTarget.XivLauncher, xivLauncher: Plugin);
        return (path, Release(version, path));
    }

    private static ModRelease Release(string version, string path) => new()
    {
        PluginId = "author", GameId = "xiv", Version = version, Channel = "stable", TargetPlatform = ReleaseTarget.XivLauncher,
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
    };

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
