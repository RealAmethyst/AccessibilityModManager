using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// What may be used as a developer, game or dependency id.
///
/// <para>These become Windows directory names, and the receipt tree is
/// <c>{root}/{pluginId}/{gameId}/</c> with cached uninstall scripts and rollback backups beneath.
/// So "a different id" has to mean "a different folder" — and on Windows a plain character
/// whitelist does not deliver that.</para>
/// </summary>
public sealed class SafeIdTests
{
    [Theory]
    [InlineData("amethyst")]
    [InlineData("buu420")]
    [InlineData("tcg-live")]
    [InlineData("dotnet-9-desktop-x64")]
    [InlineData("PTCGL")]
    [InlineData("a.b")]
    [InlineData("_under")]
    public void Ids_already_published_stay_valid(string id)
    {
        // Every id live in the registry and both plugin indexes on 2026-08-05 was checked against
        // this rule before it was tightened; refusing one of them would dark an author's catalog.
        Assert.True(SafeId.IsValid(id, out _), id);
    }

    [Fact]
    public void A_trailing_dot_is_refused_because_windows_removes_it()
    {
        // The impersonation this closes: "amethyst." passes an id-uniqueness check against
        // "amethyst" while Windows puts both in the same receipt folder.
        Assert.False(SafeId.IsValid("amethyst.", out var reason));
        Assert.Contains("Windows", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("NUL.txt")]
    [InlineData("aux.json")]
    public void Reserved_device_names_are_refused_with_or_without_an_extension(string id)
    {
        // Windows keeps the device meaning even with an extension, so a path built from one does
        // not behave like a folder at all.
        Assert.False(SafeId.IsValid(id, out _), id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("café")]
    public void Names_that_are_not_one_plain_segment_are_refused(string id)
    {
        Assert.False(SafeId.IsValid(id, out _), id);
    }

    [Fact]
    public void An_over_long_id_is_refused()
    {
        Assert.False(SafeId.IsValid(new string('a', SafeId.MaxLength + 1), out _));
        Assert.True(SafeId.IsValid(new string('a', SafeId.MaxLength), out _));
    }

    [Fact]
    public void Canonical_collapses_what_windows_would_collapse()
    {
        // The second line of defence: an id that reaches a comparison without being validated must
        // still collide with its filesystem equivalent rather than reading as a separate identity.
        Assert.Equal(SafeId.Canonical("amethyst"), SafeId.Canonical("amethyst."));
        Assert.Equal(SafeId.Canonical("amethyst"), SafeId.Canonical("amethyst "));
        Assert.Equal(SafeId.Canonical("amethyst"), SafeId.Canonical("amethyst. ."));
    }

    [Fact]
    public void Canonical_does_not_fold_case()
    {
        // Callers compare with an ordinal-ignore-case comparer. Folding here as well would hide
        // which of the two behaviours a given caller depends on.
        Assert.NotEqual(SafeId.Canonical("Amethyst"), SafeId.Canonical("amethyst"));
    }

    [Fact]
    public void The_claim_gate_treats_a_trailing_dot_as_the_same_identity()
    {
        // The property the whole rule exists for, asserted where it matters rather than only on
        // the helper.
        var claims = new CatalogClaimSet();
        claims.ClaimRegistry([Helpers.TestPluginEntry.Unanchored("amethyst")]);

        var taken = claims.TryClaimUserSource(new UserPluginSource
        {
            PluginId = "amethyst.",
            IndexUrl = "https://example.invalid/index.json",
            NoticeAcceptedUtc = DateTimeOffset.UnixEpoch
        }, out var owner);

        Assert.False(taken);
        Assert.Equal(CatalogSourceKind.Registry, owner!.Kind);
    }

    [Fact]
    public void A_source_with_a_trailing_dot_id_never_loads()
    {
        // Bound acceptance on purpose: the refusal must come from the ID rule, not from an
        // unconfirmed source, or this would pass for the wrong reason.
        var result = UserPluginSourceValidation.Accept([
            Helpers.TestUserSource.Accepted("amethyst.")]);

        Assert.Empty(result.Accepted);
        Assert.Single(result.Rejected);
    }
}
