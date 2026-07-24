using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Detection;

public class GameAggregatorTests : IDisposable
{
    private readonly string _tempRoot;

    public GameAggregatorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_agg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) { try { Directory.Delete(_tempRoot, true); } catch { } }
    }

    private static PluginRepoIndex IndexWith(GameDefinition game) => new()
    {
        PluginId = "plug-a",
        RepoVersion = "1",
        GeneratedAt = DateTime.UtcNow,
        Games = new List<GameDefinition> { game },
        ReleasesByGameId = new Dictionary<string, List<ModRelease>>()
    };

    private static GameDefinition RegistryGame() => new()
    {
        GameId = "ptcgl",
        DisplayName = "Pokémon TCG Live",
        ExeName = "Pokemon TCG Live.exe",
        RegistryProbe = new RegistryProbe { Hive = "HKCU", Key = "Software\\Whatever", Value = "Path" }
    };

    /// <summary>An emulator-style game: declares an extractApp game-installer (the gate the
    /// emulator heal requires), detected via override/emulator memory, no registry probe.</summary>
    private static GameDefinition EmulatorGame() => new()
    {
        GameId = "pkmnbw",
        DisplayName = "Pokémon Black and White",
        ExeName = "EmuHawk.exe",
        Dependencies = new List<Dependency>
        {
            new()
            {
                Id = "bizhawk",
                Type = "system",
                IsGameInstaller = true,
                Fix = new DependencyFix
                {
                    DownloadUrl = "https://example.invalid/bizhawk.zip",
                    AutoInstall = new ExtractAppAutoInstall { Sha256 = "00" }
                }
            }
        }
    };

    private GameAggregator Aggregator(IRegistryGameDetector registry) =>
        new(new StubSteamDetector(), registry, new GameVerifier(TestLogger.Create()), TestLogger.Create());

    /// <summary>Creates a folder that VERIFIES for <paramref name="game"/> (its exe present).</summary>
    private string VerifiableDir(string name, GameDefinition game)
    {
        var dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, game.ExeName!), "exe");
        return dir;
    }

    [Fact]
    public async Task DetectAllGames_ValidOverrideTakesPrecedenceOverRegistry()
    {
        var game = RegistryGame();
        var overridePath = VerifiableDir("junction", game);
        var registryPath = VerifiableDir("realgame", game);

        var registry = new StubRegistryDetector(registryPath);
        var agg = Aggregator(registry);

        var indexes = new Dictionary<string, PluginRepoIndex> { ["plug-a"] = IndexWith(game) };
        var overrides = new Dictionary<string, string> { ["ptcgl"] = overridePath };

        var result = await agg.DetectAllGamesAsync(indexes, overrides, new Dictionary<string, string>());

        var install = Assert.Single(result.Installs);
        Assert.Equal(overridePath, install.InstallPath);
        Assert.False(registry.WasCalled); // a VERIFIED override short-circuits before the registry probe
        Assert.Empty(result.HealedOverrides);
    }

    [Fact]
    public async Task DetectAllGames_StaleOverride_IsRescuedByRegistryProbe_AndOverrideKept()
    {
        var game = RegistryGame();
        // Exists but is gutted: no exe inside — the finding-32 case that used to stay "detected".
        var stalePath = Path.Combine(_tempRoot, "stale");
        Directory.CreateDirectory(stalePath);
        var registryPath = VerifiableDir("realgame", game);

        var registry = new StubRegistryDetector(registryPath);
        var agg = Aggregator(registry);

        var indexes = new Dictionary<string, PluginRepoIndex> { ["plug-a"] = IndexWith(game) };
        var overrides = new Dictionary<string, string> { ["ptcgl"] = stalePath };

        var result = await agg.DetectAllGamesAsync(indexes, overrides, new Dictionary<string, string>());

        var install = Assert.Single(result.Installs);
        Assert.Equal(registryPath, install.InstallPath);
        Assert.True(registry.WasCalled); // the stale override no longer blocks the probe
        Assert.Equal(stalePath, overrides["ptcgl"]); // override kept so it can recover
        Assert.Empty(result.HealedOverrides); // probe rescue is not an override heal
    }

    [Fact]
    public async Task DetectAllGames_StaleOverride_EmulatorGame_HealsFromInstalledEmulators()
    {
        var game = EmulatorGame();
        var stalePath = Path.Combine(_tempRoot, "gone"); // never created — user's "folder vanished" report
        var emulatorPath = VerifiableDir("bizhawk", game);

        var agg = Aggregator(new StubRegistryDetector(null));

        var indexes = new Dictionary<string, PluginRepoIndex> { ["plug-a"] = IndexWith(game) };
        var overrides = new Dictionary<string, string> { ["pkmnbw"] = stalePath };
        var emulators = new Dictionary<string, string> { ["emuhawk.exe"] = emulatorPath };

        var result = await agg.DetectAllGamesAsync(indexes, overrides, emulators);

        var install = Assert.Single(result.Installs);
        Assert.Equal(emulatorPath, install.InstallPath);
        Assert.Equal(emulatorPath, Assert.Contains("pkmnbw", (IDictionary<string, string>)result.HealedOverrides));
    }

    [Fact]
    public async Task DetectAllGames_OverrideNeverWritten_EmulatorGame_HealsFromInstalledEmulators()
    {
        var game = EmulatorGame();
        var emulatorPath = VerifiableDir("bizhawk", game);

        var agg = Aggregator(new StubRegistryDetector(null));

        var indexes = new Dictionary<string, PluginRepoIndex> { ["plug-a"] = IndexWith(game) };
        var emulators = new Dictionary<string, string> { ["emuhawk.exe"] = emulatorPath };

        var result = await agg.DetectAllGamesAsync(indexes, new Dictionary<string, string>(), emulators);

        var install = Assert.Single(result.Installs);
        Assert.Equal(emulatorPath, install.InstallPath);
        Assert.Equal(emulatorPath, Assert.Contains("pkmnbw", (IDictionary<string, string>)result.HealedOverrides));
    }

    [Fact]
    public async Task DetectAllGames_NothingVerifies_NotDetected_OverrideKept()
    {
        var game = EmulatorGame();
        var stalePath = Path.Combine(_tempRoot, "gone");
        var deadEmulator = Path.Combine(_tempRoot, "dead-emu"); // recorded but also gone

        var agg = Aggregator(new StubRegistryDetector(null));

        var indexes = new Dictionary<string, PluginRepoIndex> { ["plug-a"] = IndexWith(game) };
        var overrides = new Dictionary<string, string> { ["pkmnbw"] = stalePath };
        var emulators = new Dictionary<string, string> { ["emuhawk.exe"] = deadEmulator };

        var result = await agg.DetectAllGamesAsync(indexes, overrides, emulators);

        Assert.Empty(result.Installs);
        Assert.Empty(result.HealedOverrides);
        Assert.Equal(stalePath, overrides["pkmnbw"]); // kept for recovery
    }

    [Fact]
    public async Task DetectAllGames_NonEmulatorGame_NeverHealsFromInstalledEmulators()
    {
        // Same exe name recorded, but the game declares NO extractApp game-installer — adopting
        // the emulator folder for it would be the wrong-folder bug the heal gate exists to stop.
        var game = new GameDefinition
        {
            GameId = "someother",
            DisplayName = "Some Other Game",
            ExeName = "EmuHawk.exe"
        };
        var emulatorPath = VerifiableDir("bizhawk", game);

        var agg = Aggregator(new StubRegistryDetector(null));
        var indexes = new Dictionary<string, PluginRepoIndex> { ["plug-a"] = IndexWith(game) };
        var emulators = new Dictionary<string, string> { ["emuhawk.exe"] = emulatorPath };

        var result = await agg.DetectAllGamesAsync(indexes, new Dictionary<string, string>(), emulators);

        Assert.Empty(result.Installs);
        Assert.Empty(result.HealedOverrides);
    }

    [Fact]
    public async Task DetectAllGames_NoOverride_FallsBackToRegistry()
    {
        var game = RegistryGame();
        var registryPath = VerifiableDir("realgame", game);

        var agg = Aggregator(new StubRegistryDetector(registryPath));
        var indexes = new Dictionary<string, PluginRepoIndex> { ["plug-a"] = IndexWith(game) };

        var result = await agg.DetectAllGamesAsync(
            indexes, new Dictionary<string, string>(), new Dictionary<string, string>());

        var install = Assert.Single(result.Installs);
        Assert.Equal(registryPath, install.InstallPath);
    }

    [Fact]
    public async Task DetectAllGames_RegistryReturnsNull_NoInstall()
    {
        var agg = Aggregator(new StubRegistryDetector(null));
        var indexes = new Dictionary<string, PluginRepoIndex> { ["plug-a"] = IndexWith(RegistryGame()) };

        var result = await agg.DetectAllGamesAsync(
            indexes, new Dictionary<string, string>(), new Dictionary<string, string>());

        Assert.Empty(result.Installs);
    }

    private sealed class StubSteamDetector : ISteamDetector
    {
        public Task<List<GameInstall>> DetectInstalledGamesAsync(
            IEnumerable<GameDefinition> knownGames, string pluginId, CancellationToken ct = default)
            => Task.FromResult(new List<GameInstall>());
    }

    private sealed class StubRegistryDetector : IRegistryGameDetector
    {
        private readonly string? _path;
        public bool WasCalled { get; private set; }
        public StubRegistryDetector(string? path) => _path = path;
        public string? ResolveInstallPath(GameDefinition game)
        {
            WasCalled = true;
            return _path;
        }
    }
}
