namespace AccessibilityModManager.Core.Models;

/// <summary>Where a catalog came from. <see cref="Unknown"/> is the zero value, and fails closed.</summary>
public enum CatalogSourceKind
{
    /// <summary>Never set. Not an answer — consumers refuse it, so a forgotten assignment cannot pass.</summary>
    Unknown = 0,

    /// <summary>Listed in the signed plugin registry.</summary>
    Registry,

    /// <summary>Added by the user from a URL they supplied. Nothing vouches for it but their decision.</summary>
    UserAdded
}

/// <summary>
/// One catalog the manager will fetch an index for, and the trust that applies to it.
///
/// <para>This exists so that a user-added source is never a <see cref="PluginEntry"/>. A registry
/// entry carries a registry id, a registry-signed index URL and a registry-resolved trust state; a
/// user source has none of those, and dressing one up as the other would mean every consumer had to
/// remember a second flag to tell them apart. Two mechanisms that must agree is how a gap opens, so
/// there is one: origin and trust are decided together, here, at construction, and the pairing
/// cannot be written down any other way.</para>
///
/// <para>The constructor is private and there are exactly two factories. A registry source takes
/// whatever the registry resolved — <see cref="IndexTrustStatus.None"/>,
/// <see cref="IndexTrustStatus.Anchored"/> or <see cref="IndexTrustStatus.Unusable"/> — and a user
/// source is always <see cref="IndexTrustStatus.UserApprovedUnsigned"/>, never anything else.
/// <see cref="PluginEntry.ResolveIndexTrust"/> refuses the user state from the other direction, so
/// neither origin can borrow the other's trust.</para>
/// </summary>
public sealed class CatalogSource
{
    private CatalogSource(
        CatalogSourceKind kind, string pluginId, Uri indexUrl, IndexTrustResolution trust,
        PluginEntry? registryEntry, string? userDisplayName, bool discoversIdentity = false)
    {
        Kind = kind;
        PluginId = pluginId;
        IndexUrl = indexUrl;
        Trust = trust;
        RegistryEntry = registryEntry;
        UserDisplayName = userDisplayName;
        DiscoversIdentity = discoversIdentity;
    }

    /// <summary>
    /// The registry listing this came from, or null for a user source. Carried only so the existing
    /// name-resolution order (index name, then registry author, then registry name, then the id)
    /// keeps working — it is NOT where trust comes from. <see cref="Trust"/> was resolved by the
    /// registry acceptance gate and copied at construction; nothing reads it back off this.
    /// </summary>
    public PluginEntry? RegistryEntry { get; }

    /// <summary>The name recorded when the user added this source, or null for a registry source.</summary>
    public string? UserDisplayName { get; }

    /// <summary>
    /// True only while LOOKING at an address the user has not added yet, where the developer id is
    /// not known in advance — it is what the fetched catalog declares about itself.
    ///
    /// <para>Everywhere else, the id is a prior claim and the catalog must match it exactly: the
    /// signed registry names it for a registry plugin, and an added source has it pinned, which is
    /// what makes "this source changed its developer id" a refusal rather than a silent follow. A
    /// preview has no such claim to check against, so the identity is ADOPTED from the document
    /// instead — and then everything else, including that the releases inside agree with it, is
    /// validated against exactly that id.</para>
    ///
    /// <para>This exists because the alternative did not work at all: feeding a placeholder id into
    /// the identity check refused every real catalog, since no genuine index claims to be called
    /// "candidate".</para>
    /// </summary>
    public bool DiscoversIdentity { get; }

    public CatalogSourceKind Kind { get; }

    /// <summary>
    /// The plugin id this catalog publishes under. For a registry source it is the id the SIGNED
    /// registry gives, never the one the fetched index claims about itself. For a user source it is
    /// the id pinned when the user added it.
    /// </summary>
    public string PluginId { get; }

    public Uri IndexUrl { get; }

    public IndexTrustResolution Trust { get; }

    public bool IsUserAdded => Kind == CatalogSourceKind.UserAdded;

    /// <summary>
    /// A catalog the signed registry lists. The trust state comes from the registry acceptance gate
    /// via <see cref="PluginEntry.IndexTrust"/>; an entry nobody resolved arrives as
    /// <see cref="IndexTrustStatus.Unresolved"/> and is refused downstream exactly as before.
    /// </summary>
    public static CatalogSource FromRegistry(PluginEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new CatalogSource(
            CatalogSourceKind.Registry, entry.Id, entry.RepoIndexUrl, entry.IndexTrust,
            registryEntry: entry, userDisplayName: null);
    }

    /// <summary>
    /// A catalog the user added. Always unsigned and never anchored — there is no registry entry to
    /// anchor it to, and the trust argument is not a parameter precisely so that no call site can
    /// supply a stronger one.
    /// </summary>
    public static CatalogSource FromUserSource(UserPluginSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Uri.TryCreate(source.IndexUrl, UriKind.Absolute, out var url))
            throw new ArgumentException($"Source '{source.PluginId}' has an unusable address.", nameof(source));

        return new CatalogSource(
            CatalogSourceKind.UserAdded, source.PluginId, url, IndexTrustResolution.UserApprovedUnsigned,
            registryEntry: null, userDisplayName: source.DisplayName);
    }

    /// <summary>
    /// An address the user is only considering. Unsigned like any user source, and the one case
    /// where the developer id is learned from the catalog rather than checked against a prior
    /// claim — see <see cref="DiscoversIdentity"/>.
    /// </summary>
    public static CatalogSource ForPreview(Uri indexUrl)
    {
        ArgumentNullException.ThrowIfNull(indexUrl);
        return new CatalogSource(
            CatalogSourceKind.UserAdded, "", indexUrl, IndexTrustResolution.UserApprovedUnsigned,
            registryEntry: null, userDisplayName: null, discoversIdentity: true);
    }
}
