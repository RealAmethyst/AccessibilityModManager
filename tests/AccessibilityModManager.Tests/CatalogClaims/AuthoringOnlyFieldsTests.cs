using System.Text.Json.Nodes;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// Adopting the published index must take its catalog and none of its authoring data.
///
/// Every field here feeds something the author later signs — a preset fills in a dependency's URL
/// and hash, a default script fills in a release form, a discovery rule picks which upstream build a
/// dependency points at. None of them is covered by any claim, so nothing downstream protects them.
/// A server that could edit one would be choosing content for the author to put their key behind.
/// </summary>
public sealed class AuthoringOnlyFieldsTests
{
    private static JsonObject Index(string presets, string script, string discovery) =>
        JsonNode.Parse($$"""
        {
          "pluginId": "amethyst",
          "dependencyPresets": [{{presets}}],
          "games": [
            {
              "gameId": "game1",
              "displayName": "Game One",
              "defaultPostInstall": {{script}},
              "dependencies": [
                { "id": "melonloader", "versionDiscovery": {{discovery}} }
              ]
            }
          ]
        }
        """)!.AsObject();

    private static JsonObject Local() => Index(
        """{"id":"mine","displayName":"Mine"}""",
        """{"executable":"mine.ps1","what":"w","why":"y","modifies":"m"}""",
        """{"$type":"static","version":"1.0"}""");

    private static JsonObject Hostile() => Index(
        """{"id":"theirs","displayName":"Theirs"}""",
        """{"executable":"theirs.ps1","what":"w","why":"y","modifies":"m"}""",
        """{"$type":"static","version":"9.9"}""");

    [Fact]
    public void Every_author_only_field_comes_from_the_local_copy()
    {
        var adopted = Hostile();

        AuthoringOnlyFields.RestoreFromLocal(adopted, Local());

        Assert.Contains("mine", adopted["dependencyPresets"]!.ToJsonString());
        Assert.Contains("mine.ps1", adopted["games"]![0]!["defaultPostInstall"]!.ToJsonString());
        Assert.Contains("1.0", adopted["games"]![0]!["dependencies"]![0]!["versionDiscovery"]!.ToJsonString());

        var whole = adopted.ToJsonString();
        Assert.DoesNotContain("theirs", whole);
        Assert.DoesNotContain("9.9", whole);
    }

    [Fact]
    public void A_field_the_local_copy_does_not_have_is_dropped_rather_than_taken()
    {
        // The dangerous direction: the server ADDS a preset or a script the author never wrote.
        // "Keep mine if I have one" would leave it in place.
        var adopted = Hostile();
        var local = Local();
        local.Remove("dependencyPresets");
        local["games"]![0]!.AsObject().Remove("defaultPostInstall");
        local["games"]![0]!["dependencies"]![0]!.AsObject().Remove("versionDiscovery");

        AuthoringOnlyFields.RestoreFromLocal(adopted, local);

        Assert.Null(adopted["dependencyPresets"]);
        Assert.Null(adopted["games"]![0]!["defaultPostInstall"]);
        Assert.Null(adopted["games"]![0]!["dependencies"]![0]!["versionDiscovery"]);
    }

    [Fact]
    public void A_game_the_local_copy_does_not_have_keeps_none_of_the_servers_authoring_data()
    {
        var adopted = Hostile();
        adopted["games"]![0]!["gameId"] = "somethingelse";

        AuthoringOnlyFields.RestoreFromLocal(adopted, Local());

        Assert.Null(adopted["games"]![0]!["defaultPostInstall"]);
    }

    [Fact]
    public void Catalog_data_is_left_exactly_as_published()
    {
        // The point is to take the catalog. Only the authoring fields are special.
        var adopted = Hostile();
        adopted["games"]![0]!["displayName"] = "Renamed Upstream";

        AuthoringOnlyFields.RestoreFromLocal(adopted, Local());

        Assert.Equal("Renamed Upstream", adopted["games"]![0]!["displayName"]!.GetValue<string>());
    }

    [Fact]
    public void What_publishing_strips_is_what_adoption_keeps()
    {
        // If these two lists ever disagreed the gap would be silent, and it would be exactly the
        // unprotected path: a field stripped from claims but adopted from the wire.
        var game = Local()["games"]![0]!.DeepClone().AsObject();

        AuthoringOnlyFields.StripFromGame(game);

        foreach (var member in AuthoringOnlyFields.GameMembers) Assert.Null(game[member]);
        foreach (var member in AuthoringOnlyFields.DependencyMembers)
            Assert.Null(game["dependencies"]![0]![member]);
    }
}
