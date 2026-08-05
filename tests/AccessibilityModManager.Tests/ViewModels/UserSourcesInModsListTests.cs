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
/// Whether the mods list actually READS the user's own sources, and actually refuses the ones the
/// claim gate rejects.
///
/// <para>These are the tests that make the rest of the user-source work real. The claim gate and
/// the resolver can be perfect on their own and still protect nobody if the refresh path never
/// calls them — this project has already shipped a validation rule that existed in one place while
/// the code that mattered never invoked it. Everything here goes through
/// <see cref="GamesListViewModel.RefreshGamesCommand"/>, the same entry point the app uses.</para>
/// </summary>
public sealed class UserSourcesInModsListTests
{
    [Fact]
    public async Task A_mod_from_a_user_added_source_appears_in_the_list()
    {
        // The plain "it works" case, and the one that fails the moment the refresh path stops
        // consulting the configured sources at all.
        var vm = Build(config => config.UserPluginSources.Add(Source("buu420")));

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.Contains(vm.Mods, m => m.AnnouncementText.Contains("Buu's Game", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_source_is_announced_by_the_name_it_was_saved_under()
    {
        // The index here carries no author block, which is the ordinary case for a small catalog.
        // Without the saved name the row announces the slug "buu420" — and asserting only on the
        // GAME name, as the test above does, would not notice.
        var vm = Build(config => config.UserPluginSources.Add(Source("buu420", "Buu")));

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        var row = vm.Mods.First(m => m.PluginId == "buu420");
        Assert.Equal("Buu", row.DeveloperName);
        Assert.DoesNotContain("buu420", row.DeveloperName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_the_registry_and_a_user_source_are_read_in_the_same_refresh()
    {
        var vm = Build(config => config.UserPluginSources.Add(Source("buu420")));

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.Contains(vm.Mods, m => m.AnnouncementText.Contains("Amethyst's Game", StringComparison.Ordinal));
        Assert.Contains(vm.Mods, m => m.AnnouncementText.Contains("Buu's Game", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_source_impersonating_the_registrys_developer_id_is_refused_and_said_out_loud()
    {
        // The attack this whole increment exists to stop: a source publishing under the id the
        // signed registry already uses would key its installs and receipts as that developer.
        // Display name deliberately unremarkable: this test is about the developer ID clash, and a
        // name that also trips the reserved-name rule would refuse the source a step earlier and
        // pass for the wrong reason.
        var vm = Build(config => config.UserPluginSources.Add(Source("amethyst", "Impostor")));

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.Contains("amethyst", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already used by", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);

        // The registry's own catalog is untouched by the refusal.
        Assert.Contains(vm.Mods, m => m.AnnouncementText.Contains("Amethyst's Game", StringComparison.Ordinal));

        // And nothing from the impersonator got in.
        Assert.DoesNotContain(vm.Mods, m => m.AnnouncementText.Contains("Buu's Game", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_source_the_user_never_accepted_is_not_read_and_is_reported()
    {
        // Reaches the loading path only if something wrote it straight into the settings file.
        var smuggled = Source("buu420");
        smuggled.NoticeAcceptedUtc = null;
        var vm = Build(config => config.UserPluginSources.Add(smuggled));

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.DoesNotContain(vm.Mods, m => m.AnnouncementText.Contains("Buu's Game", StringComparison.Ordinal));
        Assert.Contains("wasn't loaded", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_non_https_source_is_not_contacted()
    {
        var insecure = Source("buu420");
        insecure.IndexUrl = "http://example.invalid/buu420/index.json";
        var vm = Build(config => config.UserPluginSources.Add(insecure));

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.DoesNotContain(vm.Mods, m => m.AnnouncementText.Contains("Buu's Game", StringComparison.Ordinal));
        Assert.Contains("https", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_developer_who_leaves_the_registry_with_mods_installed_is_carried_over()
    {
        // The real scenario: buu420 is dropped from the registry, and anyone with his mods
        // installed keeps him as a source instead of silently losing him.
        var config = new AppConfig();
        config.KnownPluginAddresses["buu420"] = "https://example.invalid/buu420/index.json";

        var vm = Build(c =>
        {
            c.KnownPluginAddresses["buu420"] = "https://example.invalid/buu420/index.json";
        }, installed: ["amethyst", "buu420"]);

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.Contains("kept as a source", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);

        // And his catalog loads on this same refresh, not the next one.
        Assert.Contains(vm.Mods, m => m.PluginId == "buu420");
    }

    [Fact]
    public async Task Removing_a_carried_over_source_sticks()
    {
        // End to end, through the refresh, because that is where it went wrong: the removal itself
        // always worked, and the next refresh undid it.
        AppConfig? cfg = null;
        var vm = Build(c =>
        {
            cfg = c;
            c.KnownPluginAddresses["buu420"] = "https://example.invalid/buu420/index.json";
        }, installed: ["amethyst", "buu420"]);

        await vm.RefreshGamesCommand.ExecuteAsync(null);
        Assert.Single(cfg!.UserPluginSources);           // carried over once
        Assert.Contains("buu420", cfg.CarriedOverPluginIds);

        // The user removes it, then anything triggers another refresh.
        cfg.UserPluginSources.Clear();
        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.Empty(cfg.UserPluginSources);
    }

    [Fact]
    public async Task The_carry_over_is_announced_once_not_every_refresh()
    {
        AppConfig? cfg = null;
        var vm = Build(c =>
        {
            cfg = c;
            c.KnownPluginAddresses["buu420"] = "https://example.invalid/buu420/index.json";
        }, installed: ["buu420"]);

        await vm.RefreshGamesCommand.ExecuteAsync(null);
        Assert.Contains("kept as a source", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);

        await vm.RefreshGamesCommand.ExecuteAsync(null);
        Assert.DoesNotContain("kept as a source", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_developer_who_leaves_with_NOTHING_installed_is_not_carried_over()
    {
        var vm = Build(c =>
        {
            c.KnownPluginAddresses["buu420"] = "https://example.invalid/buu420/index.json";
        }, installed: ["amethyst"]);

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.DoesNotContain("kept as a source", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(vm.Mods, m => m.PluginId == "buu420");
    }

    [Fact]
    public async Task The_registrys_addresses_are_written_down_while_it_still_names_them()
    {
        // The record the migration depends on. Without it, clearing the index cache would strand a
        // developer whose mods are installed.
        AppConfig? saved = null;
        var vm = Build(c => saved = c);

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.True(saved!.KnownPluginAddresses.ContainsKey("amethyst"));
    }

    [Fact]
    public async Task With_no_sources_configured_nothing_changes()
    {
        // The upgrade case: an existing user opens the app and sees exactly what they saw before.
        var vm = Build(_ => { });

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.Single(vm.Mods);
        Assert.DoesNotContain("source", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ harness

    private static UserPluginSource Source(string id, string? name = null) =>
        TestUserSource.Accepted(id, name ?? "Buu");

    /// <summary>
    /// Serves a different index per plugin id, so a test can tell WHICH catalog a row came from.
    /// A single shared index would let the impersonation test pass without the filter doing
    /// anything, because both catalogs would look identical.
    /// </summary>
    private sealed class PerSourceRepoClient : IPluginRepoClient
    {
        public Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(CatalogSource source, CancellationToken ct = default)
        {
            var name = source.Kind == CatalogSourceKind.Registry ? "Amethyst's Game" : "Buu's Game";
            return Task.FromResult(new Fetched<PluginRepoIndex> { Value = Index(source.PluginId, name) });
        }

        public async Task<PluginRepoIndex> FetchIndexUncachedAsync(CatalogSource source, CancellationToken ct = default) =>
            (await FetchPluginIndexAsync(source, ct)).Value;

        public Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default) =>
            throw new NotSupportedException();

        private static PluginRepoIndex Index(string pluginId, string gameName) => new()
        {
            PluginId = pluginId,
            RepoVersion = "1",
            GeneratedAt = DateTime.UnixEpoch,
            Games = [new GameDefinition { GameId = $"{pluginId}-game", DisplayName = gameName, ModName = "Mod" }],
            ReleasesByGameId = new Dictionary<string, List<ModRelease>>
            {
                [$"{pluginId}-game"] =
                [
                    new ModRelease
                    {
                        PluginId = pluginId, GameId = $"{pluginId}-game", Version = "1.0.0", Channel = "stable",
                        PackageUrl = new Uri("https://example.invalid/p.zip"),
                        Sha256 = new string('a', 64)
                    }
                ]
            }
        };
    }

    private static GamesListViewModel Build(Action<AppConfig> configure, params string[] installed)
    {
        var config = new AppConfig();
        configure(config);

        return new GamesListViewModel(
            new StubRegistryClient(),
            new PerSourceRepoClient(),
            new StubConfigService(config),
            new StubReceiptStore(installed),
            new StubVerifier(),
            new GameAggregator(new StubSteamDetector(), new StubRegistryDetector(), new StubVerifier(), TestLogger.Create()),
            MakePatreonService(),
            TestLogger.Create(),
            (_, _, _) => { },
            (_, _, _, _) => { },
            _ => null);
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
        public async Task<AppConfig> UpdateAsync(Action<AppConfig> change)
        {
            var config = await LoadAsync();
            change(config);
            await SaveAsync(config);
            return config;
        }
        public string? LastLoadProblem => null;
        public void AcknowledgeLoadProblem() { }
    }

    private sealed class StubReceiptStore(params string[] installed) : IReceiptStore
    {
        public Task<InstallReceipt?> LoadAsync(string gameId, string pluginId) => Task.FromResult<InstallReceipt?>(null);
        public Task SaveAsync(InstallReceipt receipt) => Task.CompletedTask;
        public Task DeleteAsync(string gameId, string pluginId) => Task.CompletedTask;
        public Task<List<InstallReceipt>> LoadAllForGameAsync(string gameId) => Task.FromResult(new List<InstallReceipt>());
        public string GetReceiptDirectory(string gameId, string pluginId) => "";
        public Task<List<string>> UnreadablePluginIdsForGameAsync(string gameId) => Task.FromResult(new List<string>());
        public Task<List<string>> InstalledPluginIdsAsync() => Task.FromResult(installed.ToList());
    }

    private sealed class StubVerifier : IGameVerifier
    {
        public bool VerifyInstallPath(GameDefinition game, string path) => false;
    }

    private sealed class StubSteamDetector : ISteamDetector
    {
        public Task<List<GameInstall>> DetectInstalledGamesAsync(
            IEnumerable<GameDefinition> knownGames, string pluginId, CancellationToken ct = default) =>
            Task.FromResult(new List<GameInstall>());
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
