using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// Keeping a developer working after they leave the signed registry.
///
/// <para>The case this is built for is real and imminent: buu420 is in Amethyst's registry today,
/// people have his Final Fantasy VII mods installed, and he is going to become a source instead.
/// Without this, removing his entry orphans everyone who installed them.</para>
/// </summary>
public sealed class RegistryDepartureMigrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Dictionary<string, string> Addresses(params string[] ids) =>
        ids.ToDictionary(id => id, id => $"https://example.invalid/{id}/index.json");

    [Fact]
    public void A_departed_developer_with_installed_mods_is_carried_over()
    {
        var carried = RegistryDepartureMigration.FindDepartures(
            [TestPluginEntry.Unanchored("amethyst")],
            [],
            ["amethyst", "buu420"],
            Addresses("amethyst", "buu420"),
            [],
            Now);

        var moved = Assert.Single(carried);
        Assert.Equal("buu420", moved.Source.PluginId);
        Assert.Equal("https://example.invalid/buu420/index.json", moved.Source.IndexUrl);
    }

    [Fact]
    public void A_departed_developer_with_NOTHING_installed_is_not_carried_over()
    {
        // The rule Amethyst asked for in her own words: when his stuff is installed he gets added,
        // when it is not he does not. Leaving the registry with nothing installed simply means
        // leaving, which is what removing the entry is for.
        var carried = RegistryDepartureMigration.FindDepartures(
            [TestPluginEntry.Unanchored("amethyst")],
            [],
            ["amethyst"],
            Addresses("amethyst", "buu420"),
            [],
            Now);

        Assert.Empty(carried);
    }

    [Fact]
    public void A_developer_still_in_the_registry_is_left_alone()
    {
        var carried = RegistryDepartureMigration.FindDepartures(
            [TestPluginEntry.Unanchored("amethyst"), TestPluginEntry.Unanchored("buu420")],
            [],
            ["amethyst", "buu420"],
            Addresses("amethyst", "buu420"),
            [],
            Now);

        Assert.Empty(carried);
    }

    [Fact]
    public void Nobody_is_carried_over_twice()
    {
        var carried = RegistryDepartureMigration.FindDepartures(
            [TestPluginEntry.Unanchored("amethyst")],
            [TestUserSource.Accepted("buu420", "Buu")],
            ["buu420"],
            Addresses("buu420"),
            [],
            Now);

        Assert.Empty(carried);
    }

    [Fact]
    public void A_developer_carried_over_before_is_never_carried_over_again()
    {
        // The defect Amethyst hit: she removed the carried-over source and the next refresh put it
        // straight back, so "That source can't be added — you have already added a source using the
        // developer id buu420" was about an entry she had just deleted. Carrying someone over is a
        // ONE-TIME continuity event; after it, the decision is hers.
        var carried = RegistryDepartureMigration.FindDepartures(
            [TestPluginEntry.Unanchored("amethyst")],
            [],                       // she removed it, so it is not a source any more
            ["buu420"],               // his mods are still installed
            Addresses("buu420"),
            ["buu420"],               // but he has been carried over once already
            Now);

        Assert.Empty(carried);
    }

    [Fact]
    public void The_already_carried_check_uses_the_same_id_rules_as_everything_else()
    {
        var carried = RegistryDepartureMigration.FindDepartures(
            [], [], ["buu420"], Addresses("buu420"), ["BUU420"], Now);

        Assert.Empty(carried);
    }

    [Fact]
    public void A_developer_with_no_recorded_address_is_not_invented()
    {
        // Nothing on record means the manager never saw this developer under a registry that named
        // where their catalog lives. Their mods stay installed and uninstallable; guessing an
        // address would be inventing a download location for somebody else's mods.
        var carried = RegistryDepartureMigration.FindDepartures(
            [TestPluginEntry.Unanchored("amethyst")],
            [],
            ["buu420"],
            Addresses("amethyst"),
            [],
            Now);

        Assert.Empty(carried);
    }

    [Fact]
    public void A_carried_over_source_records_a_migration_not_an_acceptance()
    {
        // The user never saw the risk notice for one of these. Writing an acceptance timestamp
        // would be recording a decision they did not make.
        var moved = Assert.Single(RegistryDepartureMigration.FindDepartures(
            [], [], ["buu420"], Addresses("buu420"), [], Now));

        Assert.Null(moved.Source.NoticeAcceptedUtc);
        Assert.NotNull(moved.Source.MigratedFromRegistryUtc);
    }

    [Fact]
    public void A_carried_over_source_is_usable_by_the_loader_it_will_be_read_back_through()
    {
        // If the loader refused what the migration writes, the developer would be carried over,
        // announced, and then silently dropped on the next start.
        var moved = Assert.Single(RegistryDepartureMigration.FindDepartures(
            [], [], ["buu420"], Addresses("buu420"), [], Now));

        var accepted = UserPluginSourceValidation.Accept([moved.Source]);
        Assert.Single(accepted.Accepted);
        Assert.Empty(accepted.Rejected);
    }

    [Fact]
    public void A_migrated_source_loses_its_standing_if_its_address_is_edited()
    {
        // The identity binding applies to a migrated source exactly as it does to an accepted one,
        // so a migration cannot be edited into pointing somewhere else.
        var moved = Assert.Single(RegistryDepartureMigration.FindDepartures(
            [], [], ["buu420"], Addresses("buu420"), [], Now)).Source;
        moved.IndexUrl = "https://somewhere-else.invalid/index.json";

        Assert.Single(UserPluginSourceValidation.Accept([moved]).Rejected);
    }

    [Fact]
    public void A_non_https_recorded_address_is_refused()
    {
        var carried = RegistryDepartureMigration.FindDepartures(
            [], [], ["buu420"],
            new Dictionary<string, string> { ["buu420"] = "http://example.invalid/index.json" },
            [],
            Now);

        Assert.Empty(carried);
    }

    [Fact]
    public void An_id_that_could_not_be_a_source_is_not_carried_over()
    {
        var carried = RegistryDepartureMigration.FindDepartures(
            [], [], ["amethyst."],
            new Dictionary<string, string> { ["amethyst."] = "https://example.invalid/index.json" },
            [],
            Now);

        Assert.Empty(carried);
    }

    [Fact]
    public void Matching_follows_the_same_id_rules_as_everything_else()
    {
        // Recorded under one spelling, installed under another. Treating those as different
        // developers would carry someone over who never left.
        var carried = RegistryDepartureMigration.FindDepartures(
            [TestPluginEntry.Unanchored("BUU420")],
            [],
            ["buu420"],
            Addresses("buu420"),
            [],
            Now);

        Assert.Empty(carried);
    }
}
