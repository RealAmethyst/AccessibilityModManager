using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.ViewModels;

/// <summary>
/// Adding and removing a source, driven the way the Developers tab drives it.
///
/// <para>The risk notice is the whole of the protection at add time, so what matters most here is
/// that it is genuinely a gate: nothing may be written before the user accepts, and declining must
/// leave no trace of a source they decided against.</para>
/// </summary>
public sealed class AddRemoveSourceTests
{
    [Fact]
    public async Task Adding_a_source_asks_before_saving_anything()
    {
        var config = new AppConfig();
        var saw = new List<SourcePreview>();
        var vm = Build(config, confirmRisk: p => { saw.Add(p); return true; });

        vm.NewSourceAddress = "https://example.invalid/buu420/index.json";
        await vm.AddSourceCommand.ExecuteAsync(null);

        // The notice was shown, and it named a real developer with a real mod count rather than
        // just echoing the address back.
        var shown = Assert.Single(saw);
        Assert.Equal("buu420", shown.PluginId);
        Assert.Equal("Buu", shown.DisplayName);
        Assert.Equal(1, shown.GameCount);

        Assert.Single(config.UserPluginSources);

        // The confirmation survives the list reload that follows it. Reporting before the reload
        // left the user hearing "Loaded 3 developers" in place of the one thing they pressed a
        // button to find out.
        Assert.Contains("Added Buu", vm.StatusMessage ?? "", StringComparison.Ordinal);
        Assert.Equal(vm.StatusMessage, vm.StatusAnnouncement);
    }

    [Fact]
    public async Task Declining_the_notice_writes_nothing()
    {
        var config = new AppConfig();
        var vm = Build(config, confirmRisk: _ => false);

        vm.NewSourceAddress = "https://example.invalid/buu420/index.json";
        await vm.AddSourceCommand.ExecuteAsync(null);

        Assert.Empty(config.UserPluginSources);
        Assert.Contains("Cancelled", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_saved_source_survives_the_loader_it_will_be_read_back_through()
    {
        // The add path and the load path have to agree. If Accept produced a record the loader
        // refused, a source would be added, announced as added, and then silently ignored on the
        // next start.
        var config = new AppConfig();
        var vm = Build(config, confirmRisk: _ => true);

        vm.NewSourceAddress = "https://example.invalid/buu420/index.json";
        await vm.AddSourceCommand.ExecuteAsync(null);

        Assert.Single(UserPluginSourceValidation.Accept(config.UserPluginSources).Accepted);
    }

    [Fact]
    public async Task A_source_that_cannot_be_added_is_refused_out_loud_and_saves_nothing()
    {
        var config = new AppConfig();
        var asked = false;
        var vm = Build(config, confirmRisk: _ => { asked = true; return true; },
            registryPluginId: "buu420");

        vm.NewSourceAddress = "https://example.invalid/buu420/index.json";
        await vm.AddSourceCommand.ExecuteAsync(null);

        // Refused before the notice: there is no decision to put to the user about a source that
        // cannot be added at all.
        Assert.False(asked);
        Assert.Empty(config.UserPluginSources);
        Assert.Contains("wasn't added", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(vm.StatusMessage, vm.StatusAnnouncement);
    }

    [Fact]
    public async Task An_empty_address_says_so_rather_than_doing_nothing()
    {
        // Silence is the worst outcome on a screen reader — pressing a button and hearing nothing
        // is indistinguishable from the button being broken.
        var vm = Build(new AppConfig(), confirmRisk: _ => true);

        vm.NewSourceAddress = "   ";
        await vm.AddSourceCommand.ExecuteAsync(null);

        Assert.False(string.IsNullOrWhiteSpace(vm.StatusAnnouncement));
    }

    [Fact]
    public async Task The_same_result_twice_is_announced_twice()
    {
        // Observable properties suppress a change notification when the value is equal, so a
        // repeated identical result would raise nothing the second time and the live region would
        // stay silent — making the button seem broken to someone who cannot see the text. Invoking
        // once, as the earlier test does, cannot catch this.
        var vm = Build(new AppConfig(), confirmRisk: _ => true);
        var announcements = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusAnnouncement) && !string.IsNullOrEmpty(vm.StatusAnnouncement))
                announcements++;
        };

        vm.NewSourceAddress = "   ";
        await vm.AddSourceCommand.ExecuteAsync(null);
        await vm.AddSourceCommand.ExecuteAsync(null);

        Assert.Equal(2, announcements);
    }

    [Fact]
    public async Task Adding_clears_the_address_box()
    {
        var vm = Build(new AppConfig(), confirmRisk: _ => true);

        vm.NewSourceAddress = "https://example.invalid/buu420/index.json";
        await vm.AddSourceCommand.ExecuteAsync(null);

        Assert.True(string.IsNullOrEmpty(vm.NewSourceAddress));
    }

    [Fact]
    public async Task Adding_tells_the_mods_list_to_re_read_its_catalogs()
    {
        // Otherwise the user adds a source, is told it worked, and sees no new mods until they
        // refresh by hand — which reads as the feature not working.
        var raised = 0;
        var vm = Build(new AppConfig(), confirmRisk: _ => true);
        vm.SourcesChanged += () => raised++;

        vm.NewSourceAddress = "https://example.invalid/buu420/index.json";
        await vm.AddSourceCommand.ExecuteAsync(null);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Removing_a_source_asks_first_and_leaves_installed_mods_alone()
    {
        var config = new AppConfig();
        config.UserPluginSources.Add(TestUserSource.Accepted("buu420", "Buu"));

        var vm = Build(config, confirmRisk: _ => true, confirmRemove: _ => true);
        await vm.LoadPluginsCommand.ExecuteAsync(null);

        await vm.RemoveSourceCommand.ExecuteAsync(Assert.Single(vm.UserSources));

        Assert.Empty(config.UserPluginSources);
        Assert.Contains("still installed", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Declining_the_removal_keeps_the_source()
    {
        var config = new AppConfig();
        config.UserPluginSources.Add(TestUserSource.Accepted("buu420", "Buu"));

        var vm = Build(config, confirmRisk: _ => true, confirmRemove: _ => false);
        await vm.LoadPluginsCommand.ExecuteAsync(null);

        await vm.RemoveSourceCommand.ExecuteAsync(Assert.Single(vm.UserSources));

        Assert.Single(config.UserPluginSources);
    }

    [Fact]
    public async Task The_tab_lists_only_sources_the_loader_accepted()
    {
        // A source the loader refuses must not sit in the list looking like it works.
        var config = new AppConfig();
        config.UserPluginSources.Add(TestUserSource.Accepted("buu420", "Buu"));
        var broken = TestUserSource.Accepted("someone");
        broken.NoticeAcceptedUtc = null;
        config.UserPluginSources.Add(broken);

        var vm = Build(config, confirmRisk: _ => true);
        await vm.LoadPluginsCommand.ExecuteAsync(null);

        var listed = Assert.Single(vm.UserSources);
        Assert.Equal("buu420", listed.PluginId);
        Assert.Contains("wasn't loaded", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_source_row_says_it_was_added_by_you_and_where_it_comes_from()
    {
        var config = new AppConfig();
        config.UserPluginSources.Add(TestUserSource.Accepted("buu420", "Buu"));

        var vm = Build(config, confirmRisk: _ => true);
        await vm.LoadPluginsCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.UserSources);
        Assert.Contains("Buu", row.AnnouncementText, StringComparison.Ordinal);
        Assert.Contains("added by you", row.AnnouncementText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("example.invalid", row.AnnouncementText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ harness

    private static PluginsViewModel Build(
        AppConfig config,
        Func<SourcePreview, bool> confirmRisk,
        Func<string, bool>? confirmRemove = null,
        string registryPluginId = "amethyst")
    {
        var repo = new StubRepo();
        return new PluginsViewModel(
            new StubRegistryClient(registryPluginId),
            new StubConfigService(config),
            new StubReceiptStore(),
            new UserSourceAdder(repo, TestLogger.Create()),
            TestLogger.Create(),
            navigateToDeveloperDetails: null,
            confirmRisk: confirmRisk,
            confirmRemove: confirmRemove ?? (_ => true));
    }

    private sealed class StubRepo : IPluginRepoClient
    {
        public Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(CatalogSource source, CancellationToken ct = default) =>
            Task.FromResult(new Fetched<PluginRepoIndex>
            {
                Value = new PluginRepoIndex
                {
                    PluginId = "buu420",
                    RepoVersion = "1",
                    GeneratedAt = DateTime.UnixEpoch,
                    Games = [new GameDefinition { GameId = "ffvii", DisplayName = "FF7", ModName = "Mod" }],
                    ReleasesByGameId = new Dictionary<string, List<ModRelease>>
                    {
                        ["ffvii"] =
                        [
                            new ModRelease
                            {
                                PluginId = "buu420", GameId = "ffvii", Version = "1.0.0", Channel = "stable",
                                PackageUrl = new Uri("https://example.invalid/p.zip"),
                                Sha256 = new string('a', 64)
                            }
                        ]
                    },
                    Author = new PluginAuthorInfo { DisplayName = "Buu" }
                }
            });

        public async Task<PluginRepoIndex> FetchIndexUncachedAsync(CatalogSource source, CancellationToken ct = default) =>
            (await FetchPluginIndexAsync(source, ct)).Value;

        public Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubRegistryClient(string pluginId) : IPluginRegistryClient
    {
        public Task<Fetched<PluginRegistry>> FetchRegistryAsync(Uri registryUrl, CancellationToken ct = default) =>
            Task.FromResult(new Fetched<PluginRegistry>
            {
                Value = new PluginRegistry
                {
                    RegistryVersion = "9.0.0",
                    UpdatedAt = DateTime.UnixEpoch,
                    Plugins = [TestPluginEntry.Unanchored(pluginId, author: "Amethyst")]
                }
            });
    }

    private sealed class StubConfigService(AppConfig config) : IConfigService
    {
        public Task<AppConfig> LoadAsync() => Task.FromResult(config);
        public Task SaveAsync(AppConfig c) => Task.CompletedTask;

        public async Task<AppConfig> UpdateAsync(Action<AppConfig> change)
        {
            var loaded = await LoadAsync();
            change(loaded);
            await SaveAsync(loaded);
            return loaded;
        }

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
}
