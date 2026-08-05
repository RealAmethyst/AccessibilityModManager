namespace AccessibilityModManager.Core.Models;

/// <summary>A source that will not be read this refresh, with a reason written to be read aloud.</summary>
public sealed record RefusedCatalogSource(string Describe, string Reason);

/// <summary>
/// The catalogs to read this refresh, in order, plus everything that was refused getting here.
/// </summary>
public sealed record CatalogSourceResolution(
    IReadOnlyList<CatalogSource> Sources,
    IReadOnlyList<RefusedCatalogSource> Refused);

/// <summary>
/// Turns the signed registry plus the user's own sources into the ordered list of catalogs to read,
/// and decides whether a new source may be added.
///
/// <para>This is the ONE place the claim gate runs, and the only thing that builds a
/// <see cref="CatalogSource"/> for a user source. Keeping both paths here is not tidiness: this
/// project has already shipped a validation rule that lived in two places, drifted, and left the
/// checks that mattered on the side whose refusal protected nobody.</para>
///
/// <para><b>Order is the rule.</b> The registry claims every id it lists before any user source is
/// offered one, so the signed catalog always wins an identity contest — not because its fetch
/// finished first, but because it is seeded first. User sources are then offered in the order the
/// user added them, so an earlier source keeps its id against a later one, and keeps it while it is
/// merely offline.</para>
///
/// <para>Every registry plugin id comes from the signed registry DOCUMENT, so the claim set is
/// complete even when every index fetch fails. There is no window in which an unreachable index
/// lets a user source take a registry identity.</para>
/// </summary>
public static class CatalogSourceResolver
{
    /// <summary>
    /// The refresh path: which catalogs to read, in order.
    ///
    /// <para>Installed mods are deliberately NOT consulted here. A configured source that has mods
    /// installed under its own id would otherwise be refused by its own installs from the second
    /// refresh onwards. Installed ids protect against a DIFFERENT source adopting them, which is a
    /// question only <see cref="CanAdd"/> has to answer.</para>
    /// </summary>
    /// <param name="registryPlugins">Entries from the accepted, signature-verified registry.</param>
    /// <param name="userSources">Sources already filtered by <see cref="UserPluginSourceValidation"/>.</param>
    public static CatalogSourceResolution Resolve(
        IEnumerable<PluginEntry> registryPlugins,
        IEnumerable<UserPluginSource> userSources)
    {
        ArgumentNullException.ThrowIfNull(registryPlugins);
        ArgumentNullException.ThrowIfNull(userSources);

        var registry = registryPlugins.ToList();
        var claims = new CatalogClaimSet();
        claims.ClaimRegistry(registry);

        var sources = new List<CatalogSource>(registry.Count);
        foreach (var plugin in registry)
            sources.Add(CatalogSource.FromRegistry(plugin));

        var refused = new List<RefusedCatalogSource>();
        foreach (var user in userSources)
        {
            var describe = Describe(user);

            if (!claims.TryClaimUserSource(user, out var owner))
            {
                refused.Add(new RefusedCatalogSource(describe, ExplainClash(user, owner)));
                continue;
            }

            CatalogSource source;
            try
            {
                source = CatalogSource.FromUserSource(user);
            }
            catch (ArgumentException ex)
            {
                // Load validation should have stopped this before it was stored, so arriving here
                // means the source got in some other way. Refuse the row rather than throwing out
                // the whole refresh over it.
                refused.Add(new RefusedCatalogSource(describe, ex.Message));
                continue;
            }

            sources.Add(source);
        }

        return new CatalogSourceResolution(sources, refused);
    }

    /// <summary>
    /// The add path: may a source publishing under <paramref name="candidatePluginId"/> be added?
    ///
    /// <para>Unlike the refresh path this DOES consult installed mods, because that is the case it
    /// exists for: a source removed while its mods stayed installed leaves a receipt folder named
    /// after it, and a different source taking that id would inherit those installs — including
    /// their uninstall records.</para>
    /// </summary>
    /// <returns>Null when the id is free; otherwise the reason it is not, written to be read aloud.</returns>
    public static string? CanAdd(
        IEnumerable<PluginEntry> registryPlugins,
        IEnumerable<UserPluginSource> existingSources,
        IEnumerable<string> installedPluginIds,
        string candidatePluginId)
    {
        ArgumentNullException.ThrowIfNull(registryPlugins);
        ArgumentNullException.ThrowIfNull(existingSources);
        ArgumentNullException.ThrowIfNull(installedPluginIds);

        if (string.IsNullOrWhiteSpace(candidatePluginId))
            return "that source doesn't say which developer it belongs to";

        var claims = new CatalogClaimSet();
        claims.ClaimRegistry(registryPlugins);
        foreach (var existing in existingSources)
            claims.TryClaimUserSource(existing, out _);

        // Last, so an id a configured source legitimately owns is attributed to that source rather
        // than to its own installed files.
        claims.ClaimInstalled(installedPluginIds);

        if (!claims.IsClaimed(candidatePluginId, out var owner)) return null;

        return owner!.Kind switch
        {
            CatalogSourceKind.Registry =>
                $"the developer id \"{candidatePluginId}\" is already used by {owner.Describe}",
            CatalogSourceKind.UserAdded =>
                $"you have already added a source using the developer id \"{candidatePluginId}\" ({owner.Describe})",
            _ =>
                $"you have mods installed under the developer id \"{candidatePluginId}\", so a new source cannot use it"
        };
    }

    private static string Describe(UserPluginSource source) =>
        string.IsNullOrWhiteSpace(source.DisplayName) ? source.PluginId : source.DisplayName!;

    private static string ExplainClash(UserPluginSource source, ClaimOwner? owner) =>
        owner is null
            ? "it has no usable developer id"
            : $"the developer id \"{source.PluginId}\" is already used by {owner.Describe}";
}
