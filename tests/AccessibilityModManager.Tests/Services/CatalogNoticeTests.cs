using System.Net.Http;
using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Patreon;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// What the user is TOLD when a developer's catalog cannot be loaded.
///
/// <para>On a screen reader the status line is the whole of what happens. A plugin being refused used
/// to be written to the log and then followed by an ordinary "Found N mods" — so a developer
/// vanishing from the catalog, whether refused, tampered with, or simply unreachable, was
/// indistinguishable from that developer having published nothing. Verification makes refusals real,
/// which is what makes the silence a defect rather than an untidiness.</para>
/// </summary>
public sealed class CatalogNoticeTests
{
    [Fact]
    public async Task ARefusedPluginIsNamedInTheStatusLine_BeforeTheCount()
    {
        var vm = Build(new ThrowingRepoClient(
            new InvalidOperationException("The registry's signing key for this developer can't be used.")));

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.NotNull(vm.StatusMessage);
        Assert.Contains("Amethyst's mods couldn't be loaded", vm.StatusMessage);
        Assert.Contains("signing key", vm.StatusMessage);

        // The problem leads. A warning that arrives after "Found 0 mods" is a warning about a list
        // the listener has already accepted as complete.
        Assert.True(
            vm.StatusMessage!.IndexOf("couldn't be loaded", StringComparison.Ordinal) <
            vm.StatusMessage.IndexOf("Found", StringComparison.Ordinal),
            $"the count came first: {vm.StatusMessage}");
    }

    [Fact]
    public async Task AServedButRefusedCatalogSaysItIsShowingTheLastAcceptedCopy()
    {
        // Distinct from being offline. Being offline is ordinary; a catalog that was reached and
        // failed its checks is not, and the user is looking at older data because of it.
        var vm = Build(new StubRepoClient(new Fetched<PluginRepoIndex>
        {
            Value = EmptyIndex(),
            FromCache = true,
            CachedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            LiveRejectionReason = "the catalog served carries no signature at all"
        }));

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.Contains("live catalog was refused", vm.StatusMessage);
        Assert.Contains("copy saved", vm.StatusMessage);
        Assert.DoesNotContain("Offline", vm.StatusMessage);
        Assert.Contains("no signature", vm.StatusMessage);
    }

    [Fact]
    public async Task FrameworkExceptionText_IsNotReadAloud()
    {
        // An unsigned plugin serving a wrong JSON type produces a message full of CLR type names,
        // JSON paths, line numbers and byte positions. That belongs in the log. What reaches someone
        // listening to a screen reader has to be a sentence.
        var vm = Build(new ThrowingRepoClient(new System.Text.Json.JsonException(
            "The JSON value could not be converted to System.Collections.Generic.List`1[Foo]. " +
            "Path: $.games[0].tags | LineNumber: 12 | BytePositionInLine: 40.")));

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.Contains("Amethyst's mods couldn't be loaded", vm.StatusMessage);
        Assert.DoesNotContain("LineNumber", vm.StatusMessage);
        Assert.DoesNotContain("System.Collections", vm.StatusMessage);
        Assert.Contains("couldn't be read", vm.StatusMessage);
    }

    [Fact]
    public async Task ATypedRefusal_IsReadAloudVerbatim()
    {
        // The reasons this codebase writes ARE for the ear, so they must survive intact rather than
        // being flattened into the generic fallback.
        var vm = Build(new ThrowingRepoClient(new CatalogRefusedException(
            "amethyst", "Its signature didn't check out: claim signature does not verify.")));

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.Contains("Its signature didn't check out", vm.StatusMessage);
    }

    [Fact]
    public async Task AHealthyRefreshSaysNothingAboutRefusals()
    {
        var vm = Build(new StubRepoClient(new Fetched<PluginRepoIndex> { Value = EmptyIndex() }));

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.DoesNotContain("couldn't be loaded", vm.StatusMessage);
        Assert.DoesNotContain("refused", vm.StatusMessage);
        Assert.Contains("Found 0 mods", vm.StatusMessage);
    }

    [Fact]
    public async Task CancellingIsNotReportedAsAPluginFailure()
    {
        // The user cancelling, or the refresh being torn down, is not this developer's catalog
        // failing — and announcing it as one is a lie they then have to investigate.
        var vm = Build(new ThrowingRepoClient(new OperationCanceledException()));

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        Assert.DoesNotContain("couldn't be loaded", vm.StatusMessage);
        Assert.Contains("cancelled", vm.StatusMessage);
    }

    [Fact]
    public async Task RefreshingWithNothingChanged_DoesNotRebuildTheList()
    {
        // Every rebuild of the visible list destroys the row the user is focused on, which costs a
        // screen-reader user a re-announcement. Startup does exactly this: the list renders, gets
        // focused, and is refreshed again a moment later when the Patreon membership load finishes
        // and asks every view to re-render. If nothing about the rows changed, nothing should move.
        var vm = Build(new StubRepoClient(new Fetched<PluginRepoIndex> { Value = OneModIndex() }));

        await vm.RefreshGamesCommand.ExecuteAsync(null);
        var first = Assert.Single(vm.Mods);

        await vm.RefreshGamesCommand.ExecuteAsync(null);

        // The very same row object, so its list item — and the focus on it — was never destroyed.
        Assert.Same(first, Assert.Single(vm.Mods));
    }

    [Fact]
    public async Task RefreshingWithAChangedRow_DoesRebuildTheList()
    {
        // The guard must not make the list stale. A row whose spoken text differs — a mod that got
        // installed, updated, or appeared — has to come through.
        var repo = new SwappableRepoClient(OneModIndex());
        var vm = Build(repo);

        await vm.RefreshGamesCommand.ExecuteAsync(null);
        var before = Assert.Single(vm.Mods);

        repo.Index = OneModIndex(gameName: "Game One Deluxe");
        await vm.RefreshGamesCommand.ExecuteAsync(null);

        var after = Assert.Single(vm.Mods);
        Assert.NotSame(before, after);
        Assert.Contains("Deluxe", after.AnnouncementText);
    }

    private static PluginRepoIndex OneModIndex(string gameName = "Game One") => new()
    {
        PluginId = "amethyst",
        RepoVersion = "1",
        GeneratedAt = DateTime.UnixEpoch,
        Games = [new GameDefinition { GameId = "game-1", DisplayName = gameName, ModName = "Mod" }],
        ReleasesByGameId = new Dictionary<string, List<ModRelease>>
        {
            ["game-1"] =
            [
                new ModRelease
                {
                    PluginId = "amethyst", GameId = "game-1", Version = "1.0.0", Channel = "stable",
                    PackageUrl = new Uri("https://example.invalid/p.zip"),
                    Sha256 = new string('a', 64)
                }
            ]
        }
    };

    private sealed class SwappableRepoClient(PluginRepoIndex index) : IPluginRepoClient
    {
        public PluginRepoIndex Index { get; set; } = index;

        public Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(PluginEntry plugin, CancellationToken ct = default) =>
            Task.FromResult(new Fetched<PluginRepoIndex> { Value = Index });

        public Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // ------------------------------------------------------------------ harness

    private static GamesListViewModel Build(IPluginRepoClient repoClient) => new(
        new StubRegistryClient(),
        repoClient,
        new StubConfigService(),
        new StubReceiptStore(),
        new StubVerifier(),
        new GameAggregator(new StubSteamDetector(), new StubRegistryDetector(), new StubVerifier(), TestLogger.Create()),
        MakePatreonService(),
        TestLogger.Create(),
        (_, _, _) => { },
        (_, _, _, _) => { },
        _ => null);

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

    private static PluginRepoIndex EmptyIndex() => new()
    {
        PluginId = "amethyst",
        RepoVersion = "1",
        GeneratedAt = DateTime.UnixEpoch,
        Games = [],
        ReleasesByGameId = []
    };

    private sealed class StubRegistryClient : IPluginRegistryClient
    {
        public Task<Fetched<PluginRegistry>> FetchRegistryAsync(Uri registryUrl, CancellationToken ct = default)
        {
            var entry = TestPluginEntry.Unanchored("amethyst", author: "Amethyst");
            return Task.FromResult(new Fetched<PluginRegistry>
            {
                Value = new PluginRegistry
                {
                    RegistryVersion = "9.0.0",
                    UpdatedAt = DateTime.UnixEpoch,
                    Plugins = [entry]
                }
            });
        }
    }

    private sealed class ThrowingRepoClient(Exception failure) : IPluginRepoClient
    {
        public Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(PluginEntry plugin, CancellationToken ct = default) =>
            Task.FromException<Fetched<PluginRepoIndex>>(failure);

        public Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubRepoClient(Fetched<PluginRepoIndex> result) : IPluginRepoClient
    {
        public Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(PluginEntry plugin, CancellationToken ct = default) =>
            Task.FromResult(result);

        public Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubConfigService : IConfigService
    {
        private readonly AppConfig _config = new();
        public Task<AppConfig> LoadAsync() => Task.FromResult(_config);
        public Task SaveAsync(AppConfig config) => Task.CompletedTask;
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
