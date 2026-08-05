using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// Who owns a plugin id. This is the impersonation defence: the id keys the index dictionary, the
/// receipt folder and the dependency refcounts, so a source publishing under someone else's id
/// would be adopting their installs.
/// </summary>
public sealed class CatalogClaimSetTests
{
    private static UserPluginSource Source(string id, string? name = null) =>
        TestUserSource.Accepted(id, name);

    [Fact]
    public void A_source_cannot_take_a_registry_plugins_id()
    {
        var claims = new CatalogClaimSet();
        claims.ClaimRegistry([TestPluginEntry.Unanchored("amethyst")]);

        var taken = claims.TryClaimUserSource(Source("amethyst"), out var owner);

        Assert.False(taken);
        Assert.NotNull(owner);
        Assert.Equal(CatalogSourceKind.Registry, owner!.Kind);
    }

    [Fact]
    public void The_registry_wins_even_when_it_claims_second_in_wall_clock_terms()
    {
        // The registry winning is a consequence of ClaimRegistry being called first, which is a rule
        // rather than an accident of which fetch finished first. This test would fail if the caller
        // ever seeded user sources before the registry, which is exactly the mistake to catch —
        // network timing must never decide who owns an identity.
        var claims = new CatalogClaimSet();
        claims.ClaimRegistry([TestPluginEntry.Unanchored("amethyst")]);
        claims.TryClaimUserSource(Source("amethyst"), out _);

        Assert.True(claims.IsClaimed("amethyst", out var owner));
        Assert.Equal(CatalogSourceKind.Registry, owner!.Kind);
    }

    [Fact]
    public void Game_ids_are_deliberately_not_claimed()
    {
        // Two developers modding the same game is a supported scenario — the mods list builds one
        // row per (developer, game) pair and receipts are keyed by both. Claiming game ids would
        // lock community authors out of every game the registry covers and buy no safety, since
        // impersonation runs on the plugin id.
        var claims = new CatalogClaimSet();
        claims.ClaimRegistry([TestPluginEntry.Unanchored("amethyst")]);

        Assert.False(claims.IsClaimed("masterduel", out _));
        Assert.True(claims.TryClaimUserSource(Source("someone-else"), out _));
    }

    [Fact]
    public void First_source_added_keeps_the_id_against_a_later_one()
    {
        var claims = new CatalogClaimSet();
        claims.ClaimRegistry([]);

        Assert.True(claims.TryClaimUserSource(Source("shared", "First"), out _));
        Assert.False(claims.TryClaimUserSource(Source("shared", "Second"), out var owner));

        Assert.Contains("First", owner!.Describe, StringComparison.Ordinal);
    }

    [Fact]
    public void An_installed_mod_keeps_its_id_reserved()
    {
        // The receipt folder is named for the plugin that installed it. If a new source could take
        // that id, it would inherit an existing install's receipts — including its uninstall record.
        var claims = new CatalogClaimSet();
        claims.ClaimRegistry([]);
        claims.ClaimInstalled(["gone-away"]);

        Assert.False(claims.TryClaimUserSource(Source("gone-away"), out var owner));
        Assert.NotNull(owner);
    }

    [Fact]
    public void Ids_that_differ_only_by_case_are_the_same_claim()
    {
        // These become Windows folder names, where Amethyst and amethyst are one directory. Treating
        // them as distinct here would let one source write into another's receipts.
        var claims = new CatalogClaimSet();
        claims.ClaimRegistry([TestPluginEntry.Unanchored("amethyst")]);

        Assert.False(claims.TryClaimUserSource(Source("AMETHYST"), out _));
        Assert.True(claims.IsClaimed("Amethyst", out _));
    }

    [Fact]
    public void A_free_id_is_claimed_and_then_owned()
    {
        var claims = new CatalogClaimSet();
        claims.ClaimRegistry([TestPluginEntry.Unanchored("amethyst")]);

        Assert.True(claims.TryClaimUserSource(Source("buu420", "Buu"), out var owner));
        Assert.Null(owner);

        Assert.True(claims.IsClaimed("buu420", out var now));
        Assert.Equal(CatalogSourceKind.UserAdded, now!.Kind);
    }

    [Fact]
    public void A_blank_id_is_never_claimable()
    {
        var claims = new CatalogClaimSet();

        Assert.False(claims.TryClaimUserSource(Source("   "), out _));
        Assert.False(claims.IsClaimed("   ", out _));
    }
}
