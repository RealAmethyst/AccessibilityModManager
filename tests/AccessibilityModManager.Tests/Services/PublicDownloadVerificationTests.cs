using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using Xunit;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// Which of a catalog's downloads the AuthorTool will try to prove after publishing.
///
/// <para>The check this feeds exists because the download server serves an ungated file only when
/// the live catalog already lists it — so before publishing, the right answer to "does this address
/// work" is always no, and asking there made publishing a new public release impossible. Choosing
/// the wrong set here brings that back in a different shape: include a gated release and every
/// publish reports a failure that is the server working correctly.</para>
/// </summary>
public sealed class PublicDownloadVerificationTests
{
    private const string Base = "https://downloads.example.com/releases";

    private static ModRelease Release(
        string gameId, string version, string? url, PatreonGate? gate = null, string sha = "abc123")
        => new()
        {
            GameId = gameId,
            PluginId = "author",
            Version = version,
            Channel = "stable",
            PackageUrl = url is null ? null : new Uri(url),
            Sha256 = sha,
            Patreon = gate
        };

    private static PluginRepoIndex IndexOf(params ModRelease[] releases)
    {
        var byGame = new Dictionary<string, List<ModRelease>>();
        foreach (var release in releases)
        {
            if (!byGame.TryGetValue(release.GameId, out var list))
                byGame[release.GameId] = list = [];
            list.Add(release);
        }

        return new PluginRepoIndex
        {
            PluginId = "author",
            RepoVersion = "1",
            GeneratedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Games = [],
            ReleasesByGameId = byGame
        };
    }

    [Fact]
    public void Selects_a_public_release_hosted_on_the_configured_server()
    {
        var index = IndexOf(Release("tcg", "1.1.1", $"{Base}/tcg/1.1.1/tcg.zip", sha: "deadbeef"));

        var selected = PublicDownloadVerification.ServerHostedPublicDownloads(index, Base);

        var only = Assert.Single(selected);
        Assert.Equal("tcg", only.GameId);
        Assert.Equal("1.1.1", only.Version);
        Assert.Equal("deadbeef", only.Sha256);
    }

    [Fact]
    public void Skips_a_gated_release_even_on_the_configured_server()
    {
        // The server turns an unauthenticated request away, correctly. Checking it would report
        // the tier lock working as a broken download.
        var index = IndexOf(Release(
            "tcg", "1.2", $"{Base}/tcg/1.2/tcg.zip", new PatreonGate { CampaignId = "c", TierIds = ["t"] }));

        Assert.Empty(PublicDownloadVerification.ServerHostedPublicDownloads(index, Base));
    }

    [Fact]
    public void Skips_a_release_hosted_somewhere_else()
    {
        var index = IndexOf(Release(
            "tcg", "1.0", "https://github.com/owner/repo/releases/download/v1.0/tcg.zip"));

        Assert.Empty(PublicDownloadVerification.ServerHostedPublicDownloads(index, Base));
    }

    [Fact]
    public void Skips_a_release_with_no_package_address()
    {
        Assert.Empty(PublicDownloadVerification.ServerHostedPublicDownloads(
            IndexOf(Release("tcg", "1.0", null)), Base));
    }

    [Fact]
    public void Selects_every_public_server_release_not_only_the_newest()
    {
        // Breadth is the point: a base URL that moves breaks releases nobody edited that day.
        var index = IndexOf(
            Release("tcg", "1.0", $"{Base}/tcg/1.0/tcg.zip"),
            Release("tcg", "1.1", $"{Base}/tcg/1.1/tcg.zip"),
            Release("duel", "2.0", $"{Base}/duel/2.0/duel.zip"));

        Assert.Equal(3, PublicDownloadVerification.ServerHostedPublicDownloads(index, Base).Count);
    }

    [Fact]
    public void A_trailing_slash_on_the_configured_base_changes_nothing()
    {
        var index = IndexOf(Release("tcg", "1.0", $"{Base}/tcg/1.0/tcg.zip"));

        Assert.Single(PublicDownloadVerification.ServerHostedPublicDownloads(index, Base + "/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    public void No_usable_public_base_selects_nothing(string configured)
    {
        var index = IndexOf(Release("tcg", "1.0", $"{Base}/tcg/1.0/tcg.zip"));

        Assert.Empty(PublicDownloadVerification.ServerHostedPublicDownloads(index, configured));
    }

    [Fact]
    public void A_path_that_merely_starts_with_the_base_spelling_is_not_under_it()
    {
        // Same host, adjacent folder. This server never uploaded there and does not serve it, so
        // claiming it would produce a failure report about someone else's address.
        var index = IndexOf(Release("tcg", "1.0", "https://downloads.example.com/releases-archive/tcg/1.0/tcg.zip"));

        Assert.Empty(PublicDownloadVerification.ServerHostedPublicDownloads(index, Base));
    }

    [Fact]
    public void A_different_host_is_not_under_the_base()
    {
        var index = IndexOf(Release("tcg", "1.0", "https://elsewhere.example.com/releases/tcg/1.0/tcg.zip"));

        Assert.Empty(PublicDownloadVerification.ServerHostedPublicDownloads(index, Base));
    }

    [Fact]
    public void A_plain_http_address_is_not_under_an_https_base()
    {
        var index = IndexOf(Release("tcg", "1.0", "http://downloads.example.com/releases/tcg/1.0/tcg.zip"));

        Assert.Empty(PublicDownloadVerification.ServerHostedPublicDownloads(index, Base));
    }

    [Fact]
    public void A_different_scheme_on_the_same_explicit_port_is_not_under_the_base()
    {
        // The scheme is the only thing separating these two. Without an explicit port the default
        // ports differ and the port comparison would carry it, which is why the plain http case
        // above cannot show that the scheme is checked at all.
        var index = IndexOf(Release("tcg", "1.0", "http://downloads.example.com:8443/releases/tcg/1.0/tcg.zip"));

        Assert.Empty(PublicDownloadVerification.ServerHostedPublicDownloads(
            index, "https://downloads.example.com:8443/releases"));
    }

    [Fact]
    public void A_different_port_on_the_same_host_is_not_under_the_base()
    {
        // Scheme and host both match; only the port separates them.
        var index = IndexOf(Release("tcg", "1.0", "https://downloads.example.com:8443/releases/tcg/1.0/tcg.zip"));

        Assert.Empty(PublicDownloadVerification.ServerHostedPublicDownloads(index, Base));
    }

    [Fact]
    public void An_address_written_with_an_uppercase_host_is_still_recognised()
    {
        // True because Uri normalises the host, not because of how this compares them — so it holds
        // however the author spelled it in the index.
        var index = IndexOf(Release("tcg", "1.0", "https://DOWNLOADS.example.com/releases/tcg/1.0/tcg.zip"));

        Assert.Single(PublicDownloadVerification.ServerHostedPublicDownloads(index, Base));
    }

    [Fact]
    public void An_origin_only_base_owns_every_path_under_it()
    {
        var index = IndexOf(Release("tcg", "1.0", "https://downloads.example.com/anything/tcg.zip"));

        Assert.Single(PublicDownloadVerification.ServerHostedPublicDownloads(
            index, "https://downloads.example.com"));
    }

    [Fact]
    public void The_base_itself_is_not_one_of_its_own_downloads()
    {
        var index = IndexOf(Release("tcg", "1.0", Base));

        Assert.Empty(PublicDownloadVerification.ServerHostedPublicDownloads(index, Base));
    }
}
