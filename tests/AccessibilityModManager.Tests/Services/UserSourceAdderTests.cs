using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// What happens between the user typing an address and the risk notice appearing.
///
/// <para>Everything is checked BEFORE anything is written, so the notice names a real developer with
/// a real number of mods rather than asking the user to approve an address that might turn out to be
/// nothing. And a candidate the user then cancels must leave no trace — cancelling should not have
/// taught the manager anything about a source they decided against.</para>
/// </summary>
public sealed class UserSourceAdderTests
{
    private static UserSourceAdder Adder(IPluginRepoClient repo) => new(repo, TestLogger.Create());

    private static Task<SourcePreview> Preview(
        IPluginRepoClient repo, string address,
        IReadOnlyList<PluginEntry>? registry = null,
        IReadOnlyList<UserPluginSource>? existing = null,
        IReadOnlyList<string>? installed = null)
        => Adder(repo).PreviewAsync(address, registry ?? [], existing ?? [], installed ?? []);

    [Fact]
    public async Task A_good_address_previews_the_developer_and_their_mod_count()
    {
        var result = await Preview(new StubRepo(Index("buu420", "Buu", games: 2)),
            "https://example.invalid/buu420/index.json");

        Assert.True(result.CanAdd);
        Assert.Null(result.Refusal);
        Assert.Equal("buu420", result.PluginId);
        Assert.Equal("Buu", result.DisplayName);
        Assert.Equal(2, result.GameCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://example.invalid/index.json")]
    public async Task An_address_that_is_not_a_secure_web_address_is_refused_without_a_fetch(string address)
    {
        // ThrowingRepo makes the point: refusing these must not require reaching out first.
        var result = await Preview(new ThrowingRepo(), address);

        Assert.False(result.CanAdd);
        Assert.False(string.IsNullOrWhiteSpace(result.Refusal));
    }

    [Fact]
    public async Task A_plain_http_address_is_refused()
    {
        var result = await Preview(new ThrowingRepo(), "http://example.invalid/index.json");

        Assert.False(result.CanAdd);
        Assert.Contains("https", result.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_address_with_no_catalog_behind_it_is_refused_in_plain_words()
    {
        // What the user hears has to be a sentence, not a JSON path and a byte offset.
        var result = await Preview(
            new ThrowingRepo(new System.Text.Json.JsonException(
                "The JSON value could not be converted to System.Collections.Generic.List`1[Foo]. " +
                "Path: $.games[0] | LineNumber: 12 | BytePositionInLine: 40.")),
            "https://example.invalid/index.json");

        Assert.False(result.CanAdd);
        Assert.DoesNotContain("LineNumber", result.Refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Collections", result.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_claiming_a_registry_developers_id_is_refused()
    {
        var result = await Preview(
            new StubRepo(Index("amethyst", "Someone")),
            "https://example.invalid/index.json",
            registry: [TestPluginEntry.Unanchored("amethyst")]);

        Assert.False(result.CanAdd);
        Assert.Contains("amethyst", result.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_source_claiming_an_id_with_mods_installed_under_it_is_refused()
    {
        var result = await Preview(
            new StubRepo(Index("gone-away", "Someone")),
            "https://example.invalid/index.json",
            installed: ["gone-away"]);

        Assert.False(result.CanAdd);
        Assert.Contains("installed", result.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_source_presenting_Amethysts_name_is_refused_before_it_can_be_added()
    {
        // It cannot take her id, but the id is not what a listener hears.
        var result = await Preview(
            new StubRepo(Index("buu420", "Amethyst")),
            "https://example.invalid/index.json");

        Assert.False(result.CanAdd);
        Assert.Contains("isn't allowed to use", result.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_source_whose_own_id_is_unusable_is_refused()
    {
        var result = await Preview(
            new StubRepo(Index("amethyst.", "Someone")),
            "https://example.invalid/index.json");

        Assert.False(result.CanAdd);
    }

    [Fact]
    public async Task The_candidate_is_fetched_as_a_user_source_not_as_a_registry_one()
    {
        // Previewing it under different rules from the ones it will be read under later would mean
        // the thing checked is not the thing used.
        var repo = new StubRepo(Index("buu420", "Buu"));

        await Preview(repo, "https://example.invalid/buu420/index.json");

        Assert.Equal(CatalogSourceKind.UserAdded, repo.LastSource!.Kind);
        Assert.Equal(IndexTrustStatus.UserApprovedUnsigned, repo.LastSource.Trust.Status);
    }

    [Fact]
    public void Accepting_binds_the_approval_to_the_exact_source()
    {
        var preview = new SourcePreview(true, null, "buu420", "Buu", "https://example.invalid/i.json", 1);

        var saved = UserSourceAdder.Accept(preview, DateTimeOffset.UnixEpoch);

        Assert.Equal(UserPluginSource.AcceptanceKey("buu420", "https://example.invalid/i.json"),
            saved.AcceptedFor);
        Assert.NotNull(saved.NoticeAcceptedUtc);

        // And what it produces has to survive the loader it will be read back through.
        Assert.Single(UserPluginSourceValidation.Accept([saved]).Accepted);
    }

    [Fact]
    public void A_refused_candidate_cannot_be_accepted()
    {
        var refused = new SourcePreview(false, "no");

        Assert.Throws<InvalidOperationException>(() =>
            UserSourceAdder.Accept(refused, DateTimeOffset.UnixEpoch));
    }

    // ------------------------------------------------------------------ harness

    private static PluginRepoIndex Index(string pluginId, string author, int games = 1)
    {
        var defs = new List<GameDefinition>();
        var releases = new Dictionary<string, List<ModRelease>>();
        for (var i = 0; i < games; i++)
        {
            var gameId = $"{pluginId}-game{i}";
            defs.Add(new GameDefinition { GameId = gameId, DisplayName = $"Game {i}", ModName = "Mod" });
            releases[gameId] =
            [
                new ModRelease
                {
                    PluginId = pluginId, GameId = gameId, Version = "1.0.0", Channel = "stable",
                    PackageUrl = new Uri("https://example.invalid/p.zip"),
                    Sha256 = new string('a', 64)
                }
            ];
        }

        return new PluginRepoIndex
        {
            PluginId = pluginId,
            RepoVersion = "1",
            GeneratedAt = DateTime.UnixEpoch,
            Games = defs,
            ReleasesByGameId = releases,
            Author = new PluginAuthorInfo { DisplayName = author }
        };
    }

    private sealed class StubRepo(PluginRepoIndex index) : IPluginRepoClient
    {
        public CatalogSource? LastSource { get; private set; }

        // Previewing must never go through the CACHING path: a saved copy would let a preview
        // succeed while the address is unreachable, and would leave a trace of a candidate the user
        // then declined. Making it throw means a regression cannot quietly pass.
        public Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(CatalogSource source, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "Preview used the caching fetch. It must use FetchIndexUncachedAsync.");

        public Task<PluginRepoIndex> FetchIndexUncachedAsync(CatalogSource source, CancellationToken ct = default)
        {
            LastSource = source;
            return Task.FromResult(index);
        }

        public Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingRepo(Exception? failure = null) : IPluginRepoClient
    {
        public Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(CatalogSource source, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "Preview used the caching fetch. It must use FetchIndexUncachedAsync.");

        public Task<PluginRepoIndex> FetchIndexUncachedAsync(CatalogSource source, CancellationToken ct = default) =>
            Task.FromException<PluginRepoIndex>(
                failure ?? new InvalidOperationException("no fetch should have happened"));

        public Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
