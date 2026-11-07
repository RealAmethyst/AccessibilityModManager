using System.Reflection.PortableExecutable;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.PortableTests;

public sealed class UnrealBootstrapStubTests : IDisposable
{
    private const string RealExecutable = "End/Binaries/Win64/ff7remake_.exe";
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-unreal-stub-" + Guid.NewGuid().ToString("N"));
    private static readonly IReadOnlySet<string> Builtins =
        new HashSet<string>(["dwmapi", "version", "dinput8"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void BootstrapResourceNamesTheProgramTheGameRuns()
    {
        var game = Game(TestPe.UnrealBootstrap(Machine.Amd64, RealExecutable));
        Program(game, RealExecutable, TestPe.Game(Machine.Amd64));

        Assert.Equal(RealExecutable, UnrealBootstrapStub.Target(game, "ff7remake.exe"));
        Assert.Equal(RealExecutable, UnrealBootstrapStub.ProcessExecutable(game, "ff7remake.exe"));
    }

    [Fact]
    public void BackslashTargetIsRelativeToTheBootstrapFolder()
    {
        var game = Game(TestPe.UnrealBootstrap(Machine.I386, "Binaries\\Win32\\Game.exe"), "Sub/Game.exe");
        Program(game, "Sub/Binaries/Win32/Game.exe", TestPe.Game(Machine.I386));

        Assert.Equal("Sub/Binaries/Win32/Game.exe", UnrealBootstrapStub.Target(game, "Sub\\Game.exe"));
    }

    [Fact]
    public void OrdinaryGamesKeepTheirExecutable()
    {
        var plain = Game(TestPe.Game(Machine.Amd64));
        Assert.Null(UnrealBootstrapStub.Target(plain, "ff7remake.exe"));
        Assert.Equal("ff7remake.exe", UnrealBootstrapStub.ProcessExecutable(plain, "ff7remake.exe"));

        var text = Game("not a program"u8.ToArray(), "Text.exe");
        Assert.Equal("Text.exe", UnrealBootstrapStub.ProcessExecutable(text, "Text.exe"));

        var otherResource = Game(TestPe.Build(Machine.Amd64, new Dictionary<int, byte[]>
        {
            [201] = System.Text.Encoding.Unicode.GetBytes("a configuration value")
        }, ("KERNEL32.dll", ["ExitProcess"])), "Other.exe");
        Assert.Null(UnrealBootstrapStub.Target(otherResource, "Other.exe"));
    }

    [Theory]
    [InlineData("../Outside/Game.exe")]
    [InlineData("/home/Game.exe")]
    [InlineData("C:/Game/Game.exe")]
    [InlineData("End//Game.exe")]
    [InlineData("ff7remake.exe")]
    public void UnsafeTargetsAreRefused(string target)
    {
        var game = Game(TestPe.UnrealBootstrap(Machine.Amd64, target));
        Assert.Throws<InvalidOperationException>(() => UnrealBootstrapStub.Target(game, "ff7remake.exe"));
    }

    [Fact]
    public void MissingOrMismatchedTargetIsRefused()
    {
        var game = Game(TestPe.UnrealBootstrap(Machine.Amd64, RealExecutable));
        var missing = Assert.Throws<InvalidOperationException>(() =>
            UnrealBootstrapStub.Target(game, "ff7remake.exe"));
        Assert.Contains(RealExecutable, missing.Message);

        Program(game, RealExecutable, TestPe.Game(Machine.I386));
        Assert.Throws<InvalidOperationException>(() => UnrealBootstrapStub.Target(game, "ff7remake.exe"));
        Program(game, RealExecutable, "not a program"u8.ToArray());
        Assert.Throws<InvalidOperationException>(() => UnrealBootstrapStub.Target(game, "ff7remake.exe"));
    }

    [Fact]
    public void TruncatedResourceTreeIsNotABootstrap()
    {
        var image = TestPe.UnrealBootstrap(Machine.Amd64, RealExecutable);
        // Point the resource directory outside every section.
        var directories = 0x44 + 20 + 112;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(directories + 16), 0x7FFFF000);
        var game = Game(image);
        Program(game, RealExecutable, TestPe.Game(Machine.Amd64));

        Assert.Null(UnrealBootstrapStub.Target(game, "ff7remake.exe"));
    }

    [Fact]
    public void LoaderBesideTheRealProgramIsANativeFirstProxy()
    {
        var game = Game(TestPe.UnrealBootstrap(Machine.Amd64, RealExecutable));
        Program(game, RealExecutable, TestPe.Game(Machine.Amd64));

        var plan = ProtonLaunchAnalyzer.Analyze(game, "ff7remake.exe", new Dictionary<string, string>
        {
            ["End/Binaries/Win64/dwmapi.dll"] = Source(TestPe.Dll(Machine.Amd64))
        }, new Dictionary<string, string>(), Builtins);
        Assert.Equal("End/Binaries/Win64/dwmapi.dll", Assert.Single(plan.Proxies).Path);

        var misplaced = Assert.Throws<InvalidOperationException>(() =>
            ProtonLaunchAnalyzer.Analyze(game, "ff7remake.exe", new Dictionary<string, string>
            {
                ["dwmapi.dll"] = Source(TestPe.Dll(Machine.Amd64))
            }, new Dictionary<string, string>(), Builtins));
        Assert.Contains(RealExecutable, misplaced.Message);
    }

    [Fact]
    public void ReloadedConfigNamesTheRealProgram()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (game, roaming) = ReloadedGame(TestPe.UnrealBootstrap(Machine.Amd64, RealExecutable),
            """{"ModId":"FF7Remake.Accessibility","SupportedAppId":["ff7remake_.exe"]}""");
        Program(game.InstallPath, RealExecutable, TestPe.Game(Machine.Amd64));

        var files = ProtonReloadedConfig.Plan(game, Launch("ff7remake.exe"));

        Assert.Equal(Path.Combine(roaming, "Reloaded-Mod-Loader-II", "ReloadedII.json"), files[0].Path);
        Assert.Equal(Path.Combine(game.InstallPath, "Reloaded-II", "Apps", "ff7remake_.exe", "AppConfig.json"),
            files[1].Path);
        using var config = JsonDocument.Parse(Convert.FromBase64String(files[1].InstalledBase64));
        var real = "Z:" + Path.Combine(game.InstallPath, "End", "Binaries", "Win64").Replace('/', '\\');
        Assert.Equal("ff7remake_.exe", config.RootElement.GetProperty("AppId").GetString());
        Assert.Equal("ff7remake_.exe", config.RootElement.GetProperty("AppName").GetString());
        Assert.Equal(real + "\\ff7remake_.exe", config.RootElement.GetProperty("AppLocation").GetString());
        Assert.Equal(real, config.RootElement.GetProperty("WorkingDirectory").GetString());
    }

    [Fact]
    public void ReloadedConfigForAnOrdinaryGameIsUnchanged()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (game, _) = ReloadedGame(TestPe.Game(Machine.Amd64),
            """{"ModId":"FF7Remake.Accessibility","SupportedAppId":["FF7REMAKE.exe"]}""");

        var files = ProtonReloadedConfig.Plan(game, Launch("ff7remake.exe"));

        var location = "Z:" + game.InstallPath.Replace('/', '\\');
        var expected = $$"""
            {
              "AppId": "ff7remake.exe",
              "AppName": "ff7remake.exe",
              "AppLocation": "{{location.Replace("\\", "\\\\")}}\\ff7remake.exe",
              "AppArguments": "",
              "AppIcon": "",
              "AutoInject": false,
              "EnabledMods": [
                "FF7Remake.Accessibility"
              ],
              "WorkingDirectory": "{{location.Replace("\\", "\\\\")}}",
              "PluginData": {},
              "SortedMods": [],
              "PreserveDisabledModOrder": true,
              "DontInject": false,
              "IsMsStore": false
            }

            """;
        Assert.Equal(Path.Combine(game.InstallPath, "Reloaded-II", "Apps", "ff7remake.exe", "AppConfig.json"),
            files[1].Path);
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(files[1].InstalledBase64)));
    }

    [Theory]
    [InlineData("""{"ModId":"FF7Remake.Accessibility","SupportedAppId":["ff7remake.exe"]}""")]
    [InlineData("""{"ModId":"Another.Mod"}""")]
    [InlineData("not json")]
    public void ReloadedSetupRefusesAModThatCannotLoadInTheGame(string modConfig)
    {
        if (!OperatingSystem.IsLinux()) return;
        var (game, _) = ReloadedGame(TestPe.UnrealBootstrap(Machine.Amd64, RealExecutable), modConfig);
        Program(game.InstallPath, RealExecutable, TestPe.Game(Machine.Amd64));

        Assert.Throws<InvalidOperationException>(() => ProtonReloadedConfig.Plan(game, Launch("ff7remake.exe")));
    }

    [Fact]
    public void ReloadedSetupFindsNestedModsOnceAndRefusesDuplicates()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string mod = """{"ModId":"FF7Remake.Accessibility"}""";
        var (game, _) = ReloadedGame(TestPe.Game(Machine.Amd64), null);
        var mods = Path.Combine(game.InstallPath, "Reloaded-II", "Mods");
        Directory.CreateDirectory(Path.Combine(mods, "Group", "Accessibility", "Nested"));
        File.WriteAllText(Path.Combine(mods, "Group", "Accessibility", "ModConfig.json"), mod);
        // The loader stops at a folder with a config, so this copy is not a second mod.
        File.WriteAllText(Path.Combine(mods, "Group", "Accessibility", "Nested", "ModConfig.json"), mod);
        Assert.Equal(2, ProtonReloadedConfig.Plan(game, Launch("ff7remake.exe")).Count);

        // Wine matches the loader's file name without regard to case.
        Directory.CreateDirectory(Path.Combine(mods, "Copy"));
        File.WriteAllText(Path.Combine(mods, "Copy", "modconfig.json"), mod);
        Assert.Throws<InvalidOperationException>(() => ProtonReloadedConfig.Plan(game, Launch("ff7remake.exe")));
    }

    private (GameInstall Game, string Roaming) ReloadedGame(byte[] executable, string? modConfig)
    {
        var gamePath = Game(executable);
        var prefix = Path.Combine(root, "compatdata", "123", "pfx");
        var roaming = Path.Combine(prefix, "drive_c", "users", "steamuser", "AppData", "Roaming");
        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        Directory.CreateSymbolicLink(Path.Combine(prefix, "dosdevices", "z:"), "/");
        var mod = Path.Combine(gamePath, "Reloaded-II", "Mods", "FF7Remake.Accessibility");
        Directory.CreateDirectory(mod);
        if (modConfig is not null) File.WriteAllText(Path.Combine(mod, "ModConfig.json"), modConfig);
        return (new GameInstall
        {
            Game = new GameDefinition { GameId = "ff7r", DisplayName = "Example", ExeName = "ff7remake.exe" },
            PluginId = "amethyst", InstallPath = gamePath, ProtonPrefixPath = prefix
        }, roaming);
    }

    private static ProtonLaunchConfig Launch(string executable) => new()
    {
        SteamAppId = "123", GameDisplayName = "Example", GameExecutable = executable,
        LauncherPath = "Launcher.exe", BridgeDirectory = "prism-wine",
        ReloadedRoot = "Reloaded-II", ReloadedModId = "FF7Remake.Accessibility"
    };

    private string Game(byte[] executable, string name = "ff7remake.exe")
    {
        var game = Path.Combine(root, "game");
        Program(game, name, executable);
        return game;
    }

    private static void Program(string game, string relative, byte[] content)
    {
        var path = Path.Combine(game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private string Source(byte[] content)
    {
        var path = Path.Combine(root, "sources", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
