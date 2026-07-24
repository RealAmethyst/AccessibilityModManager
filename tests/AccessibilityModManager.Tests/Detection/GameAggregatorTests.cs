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

    [Fact]
    public async Task DetectAllGames_OverrideTakesPrecedenceOverRegistry()
    {
        var overridePath = Path.Combine(_tempRoot, "junction");
        Directory.CreateDirectory(overridePath);
        var registryPath = Path.Combine(_tempRoot, "realgame");
        Directory.CreateDirectory(registryPath);

        var registry = new StubRegistryDetector(registryPath);
        var agg = new GameAggregator(new StubSteamDetector(), registry, TestLogger.Create());

        var indexes = new Dictionary<string, PluginRepoIndex> { ["plug-a"] = IndexWith(RegistryGame()) };
        var overrides = new Dictionary<string, string> { ["ptcgl"] = overridePath };

        var installs = await agg.DetectAllGamesAsync(indexes, overrides);

        var install = Assert.Single(installs);
        Assert.Equal(overridePath, install.InstallPath);
        Assert.False(registry.WasCalled); // override short-circuits before the registry probe
    }

    [Fact]
    public async Task DetectAllGames_NoOverride_FallsBackToRegistry()
    {
        var registryPath = Path.Combine(_tempRoot, "realgame");
        Directory.CreateDirectory(registryPath);

        var agg = new GameAggregator(new StubSteamDetector(), new StubRegistryDetector(registryPath), TestLogger.Create());
        var indexes = new Dictionary<string, PluginRepoIndex> { ["plug-a"] = IndexWith(RegistryGame()) };

        var installs = await agg.DetectAllGamesAsync(indexes, new Dictionary<string, string>());

        var install = Assert.Single(installs);
        Assert.Equal(registryPath, install.InstallPath);
    }

    [Fact]
    public async Task DetectAllGames_RegistryReturnsNull_NoInstall()
    {
        var agg = new GameAggregator(new StubSteamDetector(), new StubRegistryDetector(null), TestLogger.Create());
        var indexes = new Dictionary<string, PluginRepoIndex> { ["plug-a"] = IndexWith(RegistryGame()) };

        var installs = await agg.DetectAllGamesAsync(indexes, new Dictionary<string, string>());

        Assert.Empty(installs);
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
