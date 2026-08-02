using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Tests.Helpers;

/// <summary>
/// Registry entries for tests, with their trust state already resolved.
///
/// <para>A <see cref="PluginEntry"/> nobody resolved reads as
/// <see cref="IndexTrustStatus.Unresolved"/> and every consumer refuses it, because in production
/// the only thing that resolves one is the registry acceptance gate. Tests that build an entry by
/// hand have to say which case they are exercising — which is the point, not an inconvenience: an
/// entry that silently defaulted to "unsigned" would let a test claim to cover the signed path
/// while running the unsigned one.</para>
/// </summary>
public static class TestPluginEntry
{
    /// <summary>An entry the registry names no signing key for — the unsigned path.</summary>
    public static PluginEntry Unanchored(
        string id = "plug-a", string indexUrl = "https://example.invalid/index.json",
        string author = "Author")
    {
        var entry = Build(id, indexUrl, author);
        entry.ResolveIndexTrust(IndexTrustResolution.NoAnchor);
        return entry;
    }

    /// <summary>An entry the registry names <paramref name="anchor"/> as the signer for.</summary>
    public static PluginEntry Anchored(ClaimTrustAnchor anchor, string? id = null)
    {
        var entry = Build(id ?? anchor.PluginId, anchor.RepoIndexUrl);
        entry.ResolveIndexTrust(IndexTrustResolution.Anchored(anchor));
        return entry;
    }

    /// <summary>An entry whose registry-named key cannot be used.</summary>
    public static PluginEntry Unusable(
        string reason, string id = "plug-a", string indexUrl = "https://example.invalid/index.json")
    {
        var entry = Build(id, indexUrl);
        entry.ResolveIndexTrust(IndexTrustResolution.Unusable(reason));
        return entry;
    }

    /// <summary>An entry no gate has spoken for — the state every consumer must refuse.</summary>
    public static PluginEntry Unresolved(
        string id = "plug-a", string indexUrl = "https://example.invalid/index.json") =>
        Build(id, indexUrl);

    private static PluginEntry Build(string id, string indexUrl, string author = "Author") => new()
    {
        Id = id,
        Name = "Plug A",
        Author = author,
        Description = "desc",
        RepoIndexUrl = new Uri(indexUrl)
    };
}
