using System.Reflection.PortableExecutable;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.PortableTests;

public sealed class ProtonLaunchAnalyzerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-analyzer-" + Guid.NewGuid().ToString("N"));
    private static readonly IReadOnlySet<string> Builtins =
        new HashSet<string>(["winhttp", "version", "dinput8"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ImportTableIsReadFromBothImageFormats()
    {
        foreach (var machine in new[] { Machine.Amd64, Machine.I386 })
        {
            var image = PeImage.Read(TestPe.Injector(machine))!;
            Assert.Equal(machine, image.Machine);
            Assert.Contains("WriteProcessMemory", image.Imports["kernel32.dll"]);
            Assert.True(ProtonLaunchAnalyzer.IsInjector(image));
        }
        Assert.Null(PeImage.Read("not a program"u8.ToArray()));
    }

    [Fact]
    public void BundledInjectorReplacesTheGameButARegistryInstallerDoesNot()
    {
        var game = Game(Machine.Amd64);
        var plan = Analyze(game, new()
        {
            ["Mod_Launcher.exe"] = File(TestPe.Injector(Machine.Amd64)),
            ["Mod_Installer.exe"] = File(TestPe.Build(Machine.Amd64,
                ("ADVAPI32.dll", ["RegCreateKeyExW", "RegSetValueExW"]),
                ("SHELL32.dll", ["ShellExecuteExW"]))),
            ["Loader/Mod.dll"] = File(TestPe.Dll(Machine.Amd64))
        });

        Assert.Equal("Mod_Launcher.exe", plan.LauncherPath);
        Assert.Empty(plan.Proxies);
    }

    [Fact]
    public void DllShadowingAProtonBuiltinBesideTheGameGetsNativeFirstLoading()
    {
        var game = Game(Machine.I386, "game/Game.exe");
        var plan = Analyze(game, new()
        {
            ["game/version.dll"] = File(TestPe.Dll(Machine.I386)),
            ["game/plugins/winhttp.dll"] = File(TestPe.Dll(Machine.I386)),
            ["game/Mods/mod.dll"] = File("managed mod")
        }, gameExecutable: "game/Game.exe");

        var proxy = Assert.Single(plan.Proxies);
        Assert.Equal("version=n,b", proxy.Override);
        Assert.Equal("game/version.dll", proxy.Path);
        Assert.Null(plan.LauncherPath);
    }

    [Fact]
    public void DllTheGameImportsLoadsWithoutAnOverride()
    {
        var game = Game(Machine.Amd64, "Game.exe", "freetype.dll");
        var plan = Analyze(game, new(), new()
        {
            ["freetype.dll"] = File(TestPe.Dll(Machine.Amd64))
        });

        Assert.Empty(plan.Proxies);
        Assert.Equal("freetype.dll", Assert.Single(plan.GameLoadedDlls));
    }

    [Fact]
    public void LoaderWithTheWrongArchitectureIsRefused()
    {
        var game = Game(Machine.Amd64);
        var error = Assert.Throws<InvalidOperationException>(() => Analyze(game, new()
        {
            ["winhttp.dll"] = File(TestPe.Dll(Machine.I386))
        }));
        Assert.Contains("different architecture", error.Message);
    }

    [Fact]
    public void ModCodeWithNoWayToLoadIsRefused()
    {
        var game = Game(Machine.Amd64);
        var error = Assert.Throws<InvalidOperationException>(() => Analyze(game, new()
        {
            ["BepInEx/plugins/Mod.dll"] = File("managed mod")
        }));
        Assert.Contains("no launcher", error.Message);
    }

    [Fact]
    public void TwoLaunchersAreAmbiguous()
    {
        var game = Game(Machine.Amd64);
        Assert.Throws<InvalidOperationException>(() => Analyze(game, new()
        {
            ["First.exe"] = File(TestPe.Injector(Machine.Amd64)),
            ["Second.exe"] = File(TestPe.Injector(Machine.Amd64))
        }));
    }

    [Fact]
    public void DataOnlyModNeedsNoLaunchChange()
    {
        var game = Game(Machine.Amd64);
        var plan = Analyze(game, new() { ["data/strings.txt"] = File("text") });
        Assert.Null(plan.LauncherPath);
        Assert.Empty(plan.Proxies);
        Assert.Empty(plan.GameLoadedDlls);
    }

    [Fact]
    public void ModFilesFromAnEarlierInstallAreNotTreatedAsGameFiles()
    {
        var game = Game(Machine.Amd64);
        System.IO.File.WriteAllBytes(Path.Combine(game, "winhttp.dll"), TestPe.Dll(Machine.Amd64));
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "core"));
        System.IO.File.WriteAllBytes(Path.Combine(game, "BepInEx", "core", "BepInEx.dll"), TestPe.Dll(Machine.Amd64));
        System.IO.File.WriteAllBytes(Path.Combine(game, "Tolk.dll"), TestPe.Dll(Machine.Amd64));
        var package = new Dictionary<string, string>
        {
            ["Tolk.dll"] = File(TestPe.Dll(Machine.Amd64)),
            ["BepInEx/plugins/Mod.dll"] = File("managed mod")
        };

        var update = ProtonLaunchAnalyzer.Analyze(game, "Game.exe", package, new Dictionary<string, string>(), Builtins,
            new HashSet<string>(["Tolk.dll"], StringComparer.OrdinalIgnoreCase));

        Assert.Empty(update.GameLoadedDlls);
        Assert.Equal("winhttp.dll=n,b", update.InstalledLoader!.DllOverride);
        // A game-shipped DLL alongside a preinstalled loader still keeps the loader's override.
        Assert.NotNull(Analyze(game, package).InstalledLoader);
    }

    [Fact]
    public void RedistributableRuntimeBesideTheGameIsNotALoader()
    {
        var builtins = new HashSet<string>(["msvcp140", "winhttp"], StringComparer.OrdinalIgnoreCase);
        var game = Game(Machine.Amd64);
        var plan = ProtonLaunchAnalyzer.Analyze(game, "Game.exe", new Dictionary<string, string>
        {
            ["msvcp140.dll"] = File(TestPe.Dll(Machine.Amd64)),
            ["winhttp.dll"] = File(TestPe.Dll(Machine.Amd64))
        }, new Dictionary<string, string>(), builtins);
        Assert.Equal("winhttp=n,b", Assert.Single(plan.Proxies).Override);
    }

    [Fact]
    public void LauncherForTheOtherArchitectureIsIgnoredWhenAMatchingOneExists()
    {
        var game = Game(Machine.Amd64);
        var plan = Analyze(game, new()
        {
            ["x86/Launcher.exe"] = File(TestPe.Injector(Machine.I386)),
            ["Launcher.exe"] = File(TestPe.Injector(Machine.Amd64))
        });
        Assert.Equal("Launcher.exe", plan.LauncherPath);
    }

    [Fact]
    public void BuiltinListComesFromTheSelectedProtonArchitecture()
    {
        var proton = TestPe.Proton(root, "winhttp", "dinput8");
        Assert.Contains("dinput8", ProtonBuiltinModules.Read(proton, Machine.Amd64));
        Assert.Contains("winhttp", ProtonBuiltinModules.Read(proton, Machine.I386));
        Assert.Throws<InvalidOperationException>(() =>
            ProtonBuiltinModules.Read(Path.Combine(root, "missing", "proton"), Machine.Amd64));
    }

    private string Game(Machine machine, string executable = "Game.exe", params string[] imports)
    {
        var directory = Path.Combine(root, "install");
        var path = Path.Combine(directory, executable);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, TestPe.Game(machine, imports));
        return directory;
    }

    private string File(byte[] content)
    {
        var path = Path.Combine(root, "sources", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    private string File(string content) => File(System.Text.Encoding.UTF8.GetBytes(content));

    private static ProtonLaunchAnalyzer.Plan Analyze(string gameDirectory,
        Dictionary<string, string> package, Dictionary<string, string>? dependencies = null,
        string gameExecutable = "Game.exe") =>
        ProtonLaunchAnalyzer.Analyze(gameDirectory, gameExecutable, package,
            dependencies ?? [], Builtins);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
