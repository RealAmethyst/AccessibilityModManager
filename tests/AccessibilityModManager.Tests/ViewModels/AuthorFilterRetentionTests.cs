using System.ComponentModel;
using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Patreon;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.ViewModels;

/// <summary>
/// What happens to a saved author filter when that developer's catalog can't be loaded.
///
/// <para>The filter list is rebuilt from the mods actually on screen, so an unavailable developer
/// has no checkbox. Saving the filters from the checkboxes alone would therefore drop a selection
/// the user never cleared — it would come back changed. The selection is held instead; these tests
/// pin that it is held AND that the user can still get rid of it, which is the part that was
/// missing: with the Clear button disabled, a retained filter was unreachable.</para>
/// </summary>
public class AuthorFilterRetentionTests
{
    [Fact]
    public async Task AnUnavailableDevelopersFilterIsKeptAndStaysClearable()
    {
        var config = new AppConfig();
        config.SelectedAuthorFilters.Add("amethyst");
        var configService = new RecordingConfigService(config);

        var vm = Build(configService, new ThrowingRepoClient());
        await vm.RefreshGamesCommand.ExecuteAsync(null);

        // No rows loaded, so nobody has a checkbox...
        Assert.Empty(vm.AuthorFilters);
        // ...but the filter is still in effect, so Clear must be available. This was the defect:
        // the selection survived while the only control that could remove it was disabled.
        Assert.True(vm.HasAnyFilterSelected);

        vm.ClearFiltersCommand.Execute(null);

        Assert.False(vm.HasAnyFilterSelected);
        Assert.Empty(configService.Saved.SelectedAuthorFilters);
    }

    [Fact]
    public async Task ClearingIsNotifiedSoTheButtonUpdates()
    {
        var config = new AppConfig();
        config.SelectedAuthorFilters.Add("amethyst");
        var configService = new RecordingConfigService(config);

        var vm = Build(configService, new ThrowingRepoClient());

        var notified = 0;
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GamesListViewModel.HasAnyFilterSelected)) notified++;
        };

        // Rebuilding the filters changes whether anything is selected. It is a computed property,
        // so without an explicit notification the Clear button keeps whatever state it had.
        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.True(notified > 0);
    }

    /// <summary>
    /// A developer whose catalog was refused IS worth interrupting for, so the status line both
    /// shows and speaks.
    /// </summary>
    [Fact]
    public async Task ARefusedCatalogIsSpoken()
    {
        var vm = Build(new RecordingConfigService(new AppConfig()), new ThrowingRepoClient());
        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.Contains("signature didn't check out", vm.StatusMessage);
        Assert.Equal(vm.StatusMessage, vm.StatusAnnouncement);
    }

    /// <summary>
    /// The routine case, and the reason this split exists: a plain "found N mods" is shown and left
    /// alone. Amethyst heard three counts talk over each other on one refresh.
    /// </summary>
    [Fact]
    public async Task APlainCountIsShownButNotSpoken()
    {
        var vm = Build(new RecordingConfigService(new AppConfig()), new EmptyRepoClient());
        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.Contains("Found", vm.StatusMessage);
        Assert.Null(vm.StatusAnnouncement);
        // The filter count is shown too, and has no announcement channel at all.
        Assert.NotNull(vm.MatchCountText);
    }

    // ------------------------------------------------------------------ harness

    private static GamesListViewModel Build(IConfigService configService, IPluginRepoClient repoClient)
    {
        var http = new HttpClient();
        var patreon = new PatreonService(
            new PatreonClient(http, PatreonAppRegistry.Manager, TestLogger.Create()),
            new StubAccountStore(),
            new PatreonEntitlementCache(),
            http,
            TestLogger.Create());

        return new GamesListViewModel(
            new StubRegistryClient(),
            repoClient,
            configService,
            new StubReceiptStore(),
            new StubVerifier(),
            new GameAggregator(new StubSteamDetector(), new StubRegistryDetector(), new StubVerifier(), TestLogger.Create()),
            patreon,
            TestLogger.Create(),
            (_, _, _) => { },
            (_, _, _, _) => { },
            _ => null);
    }

    private sealed class RecordingConfigService : IConfigService
    {
        private readonly AppConfig _config;

        public RecordingConfigService(AppConfig config)
        {
            _config = config;
            Saved = config;
        }

        public AppConfig Saved { get; private set; }
        public Task<AppConfig> LoadAsync() => Task.FromResult(_config);
        public Task SaveAsync(AppConfig config) { Saved = config; return Task.CompletedTask; }
        public string? LastLoadProblem => null;
        public void AcknowledgeLoadProblem() { }
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

    /// <summary>Stands in for a developer whose catalog is refused or unreachable this refresh.</summary>
    private sealed class ThrowingRepoClient : IPluginRepoClient
    {
        public Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(CatalogSource source, CancellationToken ct = default) =>
            Task.FromException<Fetched<PluginRepoIndex>>(
                new CatalogRefusedException(source.PluginId, "Its signature didn't check out."));

        public Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>A developer whose catalog loads fine and simply has nothing in it.</summary>
    private sealed class EmptyRepoClient : IPluginRepoClient
    {
        public Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(CatalogSource source, CancellationToken ct = default) =>
            Task.FromResult(new Fetched<PluginRepoIndex>
            {
                Value = new PluginRepoIndex
                {
                    PluginId = source.PluginId,
                    RepoVersion = "1",
                    GeneratedAt = DateTime.UnixEpoch,
                    Games = [],
                    ReleasesByGameId = []
                }
            });

        public Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubReceiptStore : IReceiptStore
    {
        public Task<InstallReceipt?> LoadAsync(string gameId, string pluginId) => Task.FromResult<InstallReceipt?>(null);
        public Task SaveAsync(InstallReceipt receipt) => Task.CompletedTask;
        public Task DeleteAsync(string gameId, string pluginId) => Task.CompletedTask;
        public Task<List<InstallReceipt>> LoadAllForGameAsync(string gameId) => Task.FromResult(new List<InstallReceipt>());
        public Task<List<string>> UnreadablePluginIdsForGameAsync(string gameId) => Task.FromResult(new List<string>());
        public string GetReceiptDirectory(string gameId, string pluginId) => string.Empty;

        public Task<List<string>> InstalledPluginIdsAsync() =>
            Task.FromResult(new List<string>());
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
