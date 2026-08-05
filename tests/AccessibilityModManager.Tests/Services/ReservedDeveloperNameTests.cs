using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// A source the user added may not present itself under Amethyst's name.
///
/// <para>The claim gate already stops a source taking her plugin ID, but the ID is not what a
/// listener hears — rows announce the DISPLAY name, which comes from the source's own catalog. So a
/// source with the id <c>buu420</c> could publish an author block reading "Amethyst" and be
/// announced as her on every row.</para>
///
/// <para>Her name only. Other authors' names are their own business, and a manager policing every
/// name would be making judgements it has no basis for.</para>
/// </summary>
public sealed class ReservedDeveloperNameTests
{
    [Theory]
    [InlineData("Amethyst")]
    [InlineData("amethyst")]
    [InlineData("AMETHYST")]
    [InlineData("  Amethyst  ")]
    [InlineData("Amethyst.")]
    [InlineData("A-M-E-T-H-Y-S-T")]
    [InlineData("Amethyst Mods")]
    [InlineData("The Amethyst Project")]
    [InlineData("amethyst_official")]
    public void Names_that_would_read_as_hers_are_reserved(string name)
    {
        // Compared with punctuation and spacing removed, because those are exactly what a spoken
        // announcement flattens away — matching on them would be matching on the part the listener
        // never hears.
        Assert.True(ReservedDeveloperNames.IsReserved(name), name);
    }

    [Theory]
    [InlineData("Buu420")]
    [InlineData("Amy")]
    [InlineData("Ametrine")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Other_names_are_left_alone(string? name)
    {
        Assert.False(ReservedDeveloperNames.IsReserved(name));
    }

    [Fact]
    public void A_user_source_claiming_her_name_is_announced_by_its_id_instead()
    {
        // Not a refusal of the whole catalog: the name lives in a document the source re-serves on
        // every refresh, so a source could rename itself into a ban at any moment and simply vanish
        // from a list the user chose to install from. The impersonation fails; the source does not.
        var index = IndexWithAuthor("Amethyst");

        var name = DeveloperNames.ResolveUserSource(index, savedName: null, "buu420", out var wasReserved);

        Assert.True(wasReserved);
        Assert.Equal("buu420", name);
    }

    [Fact]
    public void The_check_runs_on_the_catalog_not_just_the_saved_name()
    {
        // The case an add-time check alone would miss entirely: added innocently as "Buu", renamed
        // to "Amethyst" in the catalog the next day.
        var index = IndexWithAuthor("Amethyst");

        var name = DeveloperNames.ResolveUserSource(index, savedName: "Buu", "buu420", out var wasReserved);

        Assert.True(wasReserved);
        Assert.Equal("buu420", name);
    }

    [Fact]
    public void An_ordinary_source_keeps_its_name()
    {
        var name = DeveloperNames.ResolveUserSource(
            IndexWithAuthor("Buu"), savedName: null, "buu420", out var wasReserved);

        Assert.False(wasReserved);
        Assert.Equal("Buu", name);
    }

    [Fact]
    public void Her_own_registry_entry_is_untouched()
    {
        // The whole point is to protect this name, so the registry path must not filter it.
        var entry = Helpers.TestPluginEntry.Unanchored("amethyst", author: "Amethyst");

        Assert.Equal("Amethyst", DeveloperNames.Resolve(index: null, entry, "amethyst"));
    }

    [Fact]
    public void A_source_saved_under_her_name_is_not_loaded_at_all()
    {
        var source = Helpers.TestUserSource.Accepted("buu420", "Amethyst Mods");

        var result = UserPluginSourceValidation.Accept([source]);

        Assert.Empty(result.Accepted);
        Assert.Single(result.Rejected);
    }

    private static PluginRepoIndex IndexWithAuthor(string author) => new()
    {
        PluginId = "buu420",
        RepoVersion = "1",
        GeneratedAt = DateTime.UnixEpoch,
        Games = [],
        ReleasesByGameId = [],
        Author = new PluginAuthorInfo { DisplayName = author }
    };
}
