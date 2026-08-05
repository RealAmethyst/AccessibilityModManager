using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// The wall between a catalog the signed registry vouches for and one the user added themselves.
///
/// <para>Amethyst settled that user sources are unsigned and get the same capabilities as her own
/// mods, so nothing here tries to restrict what a source may DO. What it pins is that the two
/// origins cannot borrow each other's trust: a registry entry can never be demoted to the
/// user-source state, and a user source can never be promoted to an anchored one. Those are the two
/// directions an impersonation would have to travel.</para>
/// </summary>
public sealed class UserSourceTrustTests
{
    private static UserPluginSource Source(string id = "someone", string url = "https://example.invalid/index.json") =>
        TestUserSource.Accepted(id, "Someone", url);

    [Fact]
    public void A_registry_entry_cannot_be_stamped_with_the_user_source_state()
    {
        // The demotion direction. An entry that kept its registry id and registry index URL while
        // claiming to be user-added would be reporting two provenances at once, and the trust
        // switch would then read it as the arm meant for catalogs nobody vouched for.
        var entry = TestPluginEntry.Unresolved(
            "amethyst", "https://accessibilitymods.com/registry/plugins/amethyst/index.json");

        var ex = Assert.Throws<ArgumentException>(() =>
            entry.ResolveIndexTrust(IndexTrustResolution.UserApprovedUnsigned));

        Assert.Contains("amethyst", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_user_source_is_always_unsigned_and_never_anchored()
    {
        // The promotion direction. There is no parameter to pass a stronger trust through, so this
        // pins that the factory has not grown one.
        var source = CatalogSource.FromUserSource(Source());

        Assert.Equal(CatalogSourceKind.UserAdded, source.Kind);
        Assert.Equal(IndexTrustStatus.UserApprovedUnsigned, source.Trust.Status);
        Assert.Null(source.Trust.Anchor);
        Assert.True(source.IsUserAdded);
    }

    [Fact]
    public void A_user_sources_index_cannot_talk_its_way_into_being_anchored()
    {
        // The fetched document is the thing under suspicion. PluginRepoIndex carries no trust
        // member at all, so an `indexTrust` block inside a user source's JSON has nowhere to land —
        // this asserts that absence rather than trusting it to stay absent.
        var trustMembers = typeof(PluginRepoIndex).GetProperties()
            .Where(p => p.PropertyType == typeof(IndexTrustResolution) ||
                        p.PropertyType == typeof(ClaimTrustAnchor) ||
                        p.PropertyType == typeof(IndexTrustStatus))
            .ToList();

        Assert.Empty(trustMembers);
    }

    [Fact]
    public void A_registry_source_carries_whatever_the_registry_resolved()
    {
        var anchor = new ClaimTrustAnchor
        {
            PluginId = "amethyst",
            RepoIndexUrl = "https://accessibilitymods.com/registry/plugins/amethyst/index.json",
            Scheme = ClaimTrustAnchor.SchemeV1,
            KeyId = "k1",
            Algorithm = ClaimTrustAnchor.AlgorithmRsaPssSha256,
            // Never parsed here: this test is about which trust state travels, not about verifying.
            PublicKeyPem = "-----BEGIN PUBLIC KEY-----\nnot used by this test\n-----END PUBLIC KEY-----"
        };
        var anchored = CatalogSource.FromRegistry(TestPluginEntry.Anchored(anchor));
        var unanchored = CatalogSource.FromRegistry(TestPluginEntry.Unanchored());

        Assert.Equal(CatalogSourceKind.Registry, anchored.Kind);
        Assert.Equal(IndexTrustStatus.Anchored, anchored.Trust.Status);
        Assert.Equal(CatalogSourceKind.Registry, unanchored.Kind);
        Assert.Equal(IndexTrustStatus.None, unanchored.Trust.Status);

        // None and UserApprovedUnsigned are both "unsigned" but they are different facts, and the
        // registry one must not quietly become the other.
        Assert.NotEqual(IndexTrustStatus.UserApprovedUnsigned, unanchored.Trust.Status);
    }

    [Fact]
    public void An_unresolved_registry_entry_stays_unresolved_through_the_source()
    {
        // Forgetting the acceptance gate has to keep failing closed after this refactor. If
        // FromRegistry defaulted a missing resolution to anything, this is where it would show.
        var source = CatalogSource.FromRegistry(TestPluginEntry.Unresolved());

        Assert.Equal(IndexTrustStatus.Unresolved, source.Trust.Status);
    }

    [Fact]
    public void A_source_with_an_unusable_address_is_refused_rather_than_carried()
    {
        var broken = Source(url: "not a url");

        Assert.Throws<ArgumentException>(() => CatalogSource.FromUserSource(broken));
    }
}
