using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// The one place that decides which catalogs a refresh reads, and whether a new source may be
/// added. Everything the claim gate promises has to be true THROUGH here, because this is what
/// production calls — a rule the app never invokes protects nobody.
/// </summary>
public sealed class CatalogSourceResolverTests
{
    private static UserPluginSource Source(string id, string? name = null) =>
        TestUserSource.Accepted(id, name);

    /// <summary>No address on record for anyone — the state before this manager has seen a source.</summary>
    private static readonly Dictionary<string, string> NoHistory = [];

    [Fact]
    public void The_registry_is_read_first_and_user_sources_follow_in_the_order_added()
    {
        var result = CatalogSourceResolver.Resolve(
            [TestPluginEntry.Unanchored("amethyst")],
            [Source("buu420"), Source("someone")]);

        Assert.Equal(["amethyst", "buu420", "someone"], result.Sources.Select(s => s.PluginId));
        Assert.Equal(CatalogSourceKind.Registry, result.Sources[0].Kind);
        Assert.Equal(CatalogSourceKind.UserAdded, result.Sources[1].Kind);
        Assert.Empty(result.Refused);
    }

    [Fact]
    public void A_source_impersonating_a_registry_plugin_is_dropped_and_the_registry_still_loads()
    {
        var result = CatalogSourceResolver.Resolve(
            [TestPluginEntry.Unanchored("amethyst")],
            [Source("amethyst", "Not Really Amethyst")]);

        Assert.Single(result.Sources);
        Assert.Equal(CatalogSourceKind.Registry, result.Sources[0].Kind);

        var refusal = Assert.Single(result.Refused);
        Assert.Contains("amethyst", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_that_already_has_mods_installed_is_not_blocked_by_its_own_installs()
    {
        // The bug this test exists for: reserving installed ids during a REFRESH would make every
        // source refuse itself from its second refresh onwards, because its own receipt folder is
        // named after it. Installed ids guard the add path, not this one.
        var result = CatalogSourceResolver.Resolve(
            [TestPluginEntry.Unanchored("amethyst")],
            [Source("buu420")]);

        Assert.Contains(result.Sources, s => s.PluginId == "buu420");
        Assert.Empty(result.Refused);
    }

    [Fact]
    public void Every_registry_id_is_claimed_even_though_no_index_was_fetched()
    {
        // Resolve never touches the network. The claim set is complete from the signed registry
        // document alone, so an unreachable index cannot open a window for a source to take an id.
        var result = CatalogSourceResolver.Resolve(
            [TestPluginEntry.Unanchored("amethyst"), TestPluginEntry.Unanchored("other")],
            [Source("other")]);

        Assert.Equal(2, result.Sources.Count);
        Assert.All(result.Sources, s => Assert.Equal(CatalogSourceKind.Registry, s.Kind));
        Assert.Single(result.Refused);
    }

    [Fact]
    public void Adding_a_source_is_refused_when_the_registry_owns_the_id()
    {
        var reason = CatalogSourceResolver.CanAdd(
            [TestPluginEntry.Unanchored("amethyst")], [], [], "amethyst",
            "https://example.invalid/new/index.json", NoHistory);

        Assert.NotNull(reason);
        Assert.Contains("amethyst", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_a_source_is_refused_when_an_orphaned_install_holds_the_id()
    {
        // A source removed while its mods stayed installed leaves its receipt folder behind. A
        // different source taking that id would inherit those installs, including their uninstall
        // records — so the id stays reserved even though no catalog offers it any more.
        var reason = CatalogSourceResolver.CanAdd(
            [TestPluginEntry.Unanchored("amethyst")], [], ["gone-away"], "gone-away",
            "https://example.invalid/new/index.json", NoHistory);

        Assert.NotNull(reason);
        Assert.Contains("installed", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Adding_a_source_is_refused_when_another_source_already_uses_the_id()
    {
        var reason = CatalogSourceResolver.CanAdd(
            [], [Source("buu420", "Buu")], [], "buu420",
            "https://example.invalid/new/index.json", NoHistory);

        Assert.NotNull(reason);
        Assert.Contains("already added", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_configured_sources_own_installs_do_not_stop_it_being_described_as_its_own()
    {
        // Installed ids are claimed AFTER configured sources, so an id a source legitimately owns
        // is attributed to that source rather than to its own files. Re-adding the same id is still
        // refused — but for the right reason, which is what the user is told.
        var reason = CatalogSourceResolver.CanAdd(
            [], [Source("buu420", "Buu")], ["buu420"], "buu420",
            "https://example.invalid/new/index.json", NoHistory);

        Assert.NotNull(reason);
        Assert.Contains("already added", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_same_source_can_be_added_back_after_being_removed()
    {
        // Exactly what Amethyst hit: she removed buu420's source, still had his mods installed, and
        // was told "you have mods installed under the developer id buu420, so a new source cannot
        // use it". The reservation is meant to stop a DIFFERENT catalog adopting somebody else's
        // installs — not to stop the source that created them coming back, which is the whole point
        // of being able to remove one.
        var known = new Dictionary<string, string>
        {
            ["buu420"] = "https://raw.githubusercontent.com/buu420/buu-s-mods/main/index.json"
        };

        var reason = CatalogSourceResolver.CanAdd(
            [TestPluginEntry.Unanchored("amethyst")], [], ["buu420"], "buu420",
            "https://raw.githubusercontent.com/buu420/buu-s-mods/main/index.json", known);

        Assert.Null(reason);
    }

    [Fact]
    public void A_DIFFERENT_catalog_still_cannot_take_an_id_with_installs_under_it()
    {
        // The other half. Same developer id, different address — that is a stranger claiming an
        // identity whose installs someone else created, and it stays refused.
        var known = new Dictionary<string, string>
        {
            ["buu420"] = "https://raw.githubusercontent.com/buu420/buu-s-mods/main/index.json"
        };

        var reason = CatalogSourceResolver.CanAdd(
            [], [], ["buu420"], "buu420", "https://impostor.invalid/index.json", known);

        Assert.NotNull(reason);
        Assert.Contains("installed", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_known_address_does_not_excuse_taking_a_registry_or_configured_id()
    {
        // The exemption is only about who created some FILES. An id that is in use right now stays
        // refused however familiar the address is.
        var known = new Dictionary<string, string>
        {
            ["amethyst"] = "https://accessibilitymods.com/registry/plugins/amethyst/index.json"
        };

        Assert.NotNull(CatalogSourceResolver.CanAdd(
            [TestPluginEntry.Unanchored("amethyst")], [], [], "amethyst",
            "https://accessibilitymods.com/registry/plugins/amethyst/index.json", known));
    }

    [Fact]
    public void A_free_id_may_be_added()
    {
        Assert.Null(CatalogSourceResolver.CanAdd(
            [TestPluginEntry.Unanchored("amethyst")], [Source("buu420")], ["amethyst"], "newcomer",
            "https://example.invalid/new/index.json", NoHistory));
    }

    [Fact]
    public void A_source_with_no_id_is_refused_rather_than_added()
    {
        Assert.NotNull(CatalogSourceResolver.CanAdd([], [], [], "   ", "https://example.invalid/i.json", NoHistory));
    }
}
