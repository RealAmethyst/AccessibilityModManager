using System.Net.Http;
using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Patreon;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.ViewModels;

/// <summary>
/// Which developer's game definition an operation runs with, when two developers define the same
/// game.
///
/// <para>Shared game ids are allowed on purpose — a community author should be able to mod a game
/// Amethyst also covers. The danger is not the sharing, it is whose DEFINITION gets used: a
/// <see cref="GameInstall"/> carries a <see cref="GameDefinition"/>, and that definition supplies
/// the dependencies, the setup scripts and the wording of the consent prompts for everything done
/// next. The mods list used to fall back to any developer's detection of the game id, so an
/// unsigned source declaring a registry game id could have supplied the dependency installer —
/// possibly an elevated one — used while installing the REGISTRY developer's mod, with the screen
/// naming the registry developer throughout.</para>
/// </summary>
public sealed class OwnedInstallScopingTests
{
    private const string SharedGame = "shared-game";

    [Fact]
    public async Task An_unsigned_sources_detection_never_supplies_the_registry_rows_definition()
    {
        // Only the user source detects the folder, and it does not satisfy the registry
        // developer's own definition. The registry row must not open on the source's terms.
        var opened = new List<GameInstall>();
        var vm = Build(detectFor: "buu420", verifies: false, opened: opened);

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        var registryRow = vm.Mods.First(m => m.PluginId == "amethyst");
        vm.OpenGameDetailsCommand.Execute(registryRow);

        Assert.Empty(opened);
    }

    [Fact]
    public async Task A_borrowed_folder_is_used_only_after_it_verifies_against_the_rows_own_definition()
    {
        // Same setup, except the folder genuinely is the game by the registry developer's own
        // reckoning. The location may then be borrowed — but the definition that travels is the
        // row's own, never the one whose detection found it.
        var opened = new List<GameInstall>();
        var vm = Build(detectFor: "buu420", verifies: true, opened: opened);

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        var registryRow = vm.Mods.First(m => m.PluginId == "amethyst");
        vm.OpenGameDetailsCommand.Execute(registryRow);

        var install = Assert.Single(opened);
        Assert.Equal("amethyst", install.PluginId);
        Assert.Equal("Amethyst's definition", install.Game.DisplayName);
        Assert.Equal(@"C:\games\shared", install.InstallPath);
    }

    [Fact]
    public async Task A_developers_own_detection_is_always_preferred()
    {
        var opened = new List<GameInstall>();
        var vm = Build(detectFor: "amethyst", verifies: true, opened: opened);

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        vm.OpenGameDetailsCommand.Execute(vm.Mods.First(m => m.PluginId == "amethyst"));

        var install = Assert.Single(opened);
        Assert.Equal("amethyst", install.PluginId);
        Assert.Equal("Amethyst's definition", install.Game.DisplayName);
    }

    // ------------------------------------------------------------------ harness

    private static GamesListViewModel Build(string detectFor, bool verifies, List<GameInstall> opened)
    {
        var config = new AppConfig();
        config.UserPluginSources.Add(TestUserSource.Accepted("buu420", "Buu"));

        var verifier = new ToggleVerifier(verifies);

        return new GamesListViewModel(
            new StubRegistryClient(),
            new SharedGameRepoClient(),
            new StubConfigService(config),
            new StubReceiptStore(),
            verifier,
            new GameAggregator(new OnePluginDetector(detectFor), new StubRegistryDetector(), verifier, TestLogger.Create()),
            MakePatreonService(),
            TestLogger.Create(),
            (install, _, _) => opened.Add(install),
            (_, _, _, _) => { },
            _ => null);
    }

    /// <summary>Both developers define the same game id, with visibly different definitions.</summary>
    private sealed class SharedGameRepoClient : IPluginRepoClient
    {
        public Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(CatalogSource source, CancellationToken ct = default)
        {
            var name = source.Kind == CatalogSourceKind.Registry
                ? "Amethyst's definition"
                : "Buu's definition";

            return Task.FromResult(new Fetched<PluginRepoIndex>
            {
                Value = new PluginRepoIndex
                {
                    PluginId = source.PluginId,
                    RepoVersion = "1",
                    GeneratedAt = DateTime.UnixEpoch,
                    Games = [new GameDefinition { GameId = SharedGame, DisplayName = name, ModName = "Mod" }],
                    ReleasesByGameId = new Dictionary<string, List<ModRelease>>
                    {
                        [SharedGame] =
                        [
                            new ModRelease
                            {
                                PluginId = source.PluginId, GameId = SharedGame, Version = "1.0.0",
                                Channel = "stable",
                                PackageUrl = new Uri("https://example.invalid/p.zip"),
                                Sha256 = new string('a', 64)
                            }
                        ]
                    }
                }
            });
        }

        public Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Detects the shared game for exactly one plugin, so the fallback path is exercised.</summary>
    private sealed class OnePluginDetector(string pluginId) : ISteamDetector
    {
        public Task<List<GameInstall>> DetectInstalledGamesAsync(
            IEnumerable<GameDefinition> knownGames, string forPluginId, CancellationToken ct = default)
        {
            if (!string.Equals(forPluginId, pluginId, StringComparison.Ordinal))
                return Task.FromResult(new List<GameInstall>());

            var game = knownGames.FirstOrDefault(g => g.GameId == SharedGame);
            if (game is null) return Task.FromResult(new List<GameInstall>());

            return Task.FromResult(new List<GameInstall>
            {
                new() { Game = game, PluginId = pluginId, InstallPath = @"C:\games\shared", IsValid = true }
            });
        }
    }

    private sealed class ToggleVerifier(bool result) : IGameVerifier
    {
        public bool VerifyInstallPath(GameDefinition game, string path) => result;
    }

    private static PatreonService MakePatreonService()
    {
        var http = new HttpClient();
        return new PatreonService(
            new PatreonClient(http, PatreonAppRegistry.Manager, TestLogger.Create()),
            new StubAccountStore(),
            new PatreonEntitlementCache(),
            http,
            TestLogger.Create());
    }

    private sealed class StubRegistryClient : IPluginRegistryClient
    {
        public Task<Fetched<PluginRegistry>> FetchRegistryAsync(Uri registryUrl, CancellationToken ct = default) =>
            Task.FromResult(new Fetched<PluginRegistry>
            {
                Value = new PluginRegistry
                {
                    RegistryVersion = "9.0.0",
                    UpdatedAt = DateTime.UnixEpoch,
                    Plugins = [TestPluginEntry.Unanchored("amethyst", author: "Amethyst")]
                }
            });
    }

    private sealed class StubConfigService(AppConfig config) : IConfigService
    {
        public Task<AppConfig> LoadAsync() => Task.FromResult(config);
        public Task SaveAsync(AppConfig c) => Task.CompletedTask;
        public string? LastLoadProblem => null;
        public void AcknowledgeLoadProblem() { }
    }

    private sealed class StubReceiptStore : IReceiptStore
    {
        public Task<InstallReceipt?> LoadAsync(string gameId, string pluginId) => Task.FromResult<InstallReceipt?>(null);
        public Task SaveAsync(InstallReceipt receipt) => Task.CompletedTask;
        public Task DeleteAsync(string gameId, string pluginId) => Task.CompletedTask;
        public Task<List<InstallReceipt>> LoadAllForGameAsync(string gameId) => Task.FromResult(new List<InstallReceipt>());
        public string GetReceiptDirectory(string gameId, string pluginId) => "";
        public Task<List<string>> UnreadablePluginIdsForGameAsync(string gameId) => Task.FromResult(new List<string>());
        public Task<List<string>> InstalledPluginIdsAsync() => Task.FromResult(new List<string>());
    }

    private sealed class StubRegistryDetector : IRegistryGameDetector
    {
        public string? ResolveInstallPath(GameDefinition game) => null;
    }

    private sealed class StubAccountStore : IPatreonAccountStore
    {
        public Task<PatreonAccount?> LoadAsync() => Task.FromResult<PatreonAccount?>(null);
        public Task SaveAsync(PatreonAccount account) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }
}
