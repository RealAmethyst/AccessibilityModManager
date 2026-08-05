namespace AccessibilityModManager.Core.Models;

/// <summary>Who owns a plugin id, for a refusal message the user can act on.</summary>
public sealed record ClaimOwner(CatalogSourceKind Kind, string Describe);

/// <summary>
/// Decides which plugin ids are already spoken for, so a user-added source can never publish under
/// someone else's identity.
///
/// <para><b>Plugin ids only, deliberately.</b> Game ids are NOT claimed. Two developers modding the
/// same game is a first-class scenario the manager already supports — the mods list builds one row
/// per (developer, game) pair and receipts are keyed by both — so refusing a shared game id would
/// lock community authors out of every game the registry covers while buying no safety. Identity,
/// and therefore impersonation, runs on the plugin id: it keys the index dictionary, the receipt
/// folder, and the dependency refcounts.</para>
///
/// <para><b>The registry always wins, and it is knowable without the network.</b> Every registry
/// plugin id is present in the signed registry document itself, so the claim set is complete even
/// when every index fetch fails. That is why an unreachable index cannot open a window in which a
/// user source takes a registry id.</para>
///
/// <para><b>Installed mods keep their identity.</b> A receipt folder is named for the plugin that
/// installed it, so an id with an install behind it stays claimed even if its source is gone —
/// otherwise a new source could adopt an existing install's receipts.</para>
///
/// <para>One implementation, two callers: the add-source flow and the refresh path. This repo has
/// already had a validation rule exist in two copies that drifted, with the checks that mattered
/// living only on the side whose refusal protected nobody.</para>
/// </summary>
public sealed class CatalogClaimSet
{
    // Keyed on the CANONICAL id, compared case-insensitively: these become Windows folder names,
    // where "Amethyst" and "amethyst" are one directory and so are "amethyst." and "amethyst" —
    // Windows strips the trailing dot. Either difference would otherwise read as a separate
    // identity here while landing in the same receipt folder, which is exactly the impersonation
    // this gate exists to stop. SafeId.IsValid already refuses such ids at the door; comparing
    // canonically means one that arrives another way still collides rather than slipping past.
    private readonly Dictionary<string, ClaimOwner> _claims = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string id) => SafeId.Canonical(id);

    /// <summary>
    /// Seeds the registry's claims. Call this FIRST and exactly once: the registry winning every
    /// contest is a consequence of it claiming first, so the ordering is the rule rather than an
    /// accident of when a fetch happened to finish.
    /// </summary>
    public void ClaimRegistry(IEnumerable<PluginEntry> registryPlugins)
    {
        ArgumentNullException.ThrowIfNull(registryPlugins);
        foreach (var plugin in registryPlugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.Id)) continue;
            // A registry that lists one id twice is already refused upstream by registry
            // validation; if one ever arrives, the first entry keeps the claim rather than the last.
            _claims.TryAdd(Key(plugin.Id), new ClaimOwner(CatalogSourceKind.Registry, DescribeRegistry(plugin)));
        }
    }

    /// <summary>
    /// Reserves ids that have something installed under them. Takes the plugin ids read from the
    /// receipt store's own folder names, so it costs no receipt format change.
    /// </summary>
    public void ClaimInstalled(IEnumerable<string> installedPluginIds)
    {
        ArgumentNullException.ThrowIfNull(installedPluginIds);
        foreach (var id in installedPluginIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            _claims.TryAdd(Key(id), new ClaimOwner(CatalogSourceKind.Unknown, $"a mod you already have installed ({id})"));
        }
    }

    /// <summary>
    /// Offers a user source's id. Returns true and records the claim when it is free; returns false
    /// and names the current owner when it is not.
    ///
    /// <para>Sources are offered in the order the user added them, so an earlier source keeps its id
    /// against a later one — first come, and a source keeps its claim while it is merely offline.</para>
    /// </summary>
    public bool TryClaimUserSource(UserPluginSource source, out ClaimOwner? existingOwner)
    {
        ArgumentNullException.ThrowIfNull(source);
        existingOwner = null;

        if (string.IsNullOrWhiteSpace(source.PluginId)) return false;

        if (_claims.TryGetValue(Key(source.PluginId), out var owner))
        {
            existingOwner = owner;
            return false;
        }

        _claims[Key(source.PluginId)] = new ClaimOwner(
            CatalogSourceKind.UserAdded, DescribeUserSource(source));
        return true;
    }

    /// <summary>Whether an id is spoken for, without claiming it. For the add-source preview.</summary>
    public bool IsClaimed(string pluginId, out ClaimOwner? owner)
    {
        owner = null;
        if (string.IsNullOrWhiteSpace(pluginId)) return false;
        return _claims.TryGetValue(Key(pluginId), out owner);
    }

    private static string DescribeRegistry(PluginEntry plugin) =>
        string.IsNullOrWhiteSpace(plugin.Name)
            ? $"the built-in catalog ({plugin.Id})"
            : $"{plugin.Name}, in the built-in catalog";

    private static string DescribeUserSource(UserPluginSource source) =>
        string.IsNullOrWhiteSpace(source.DisplayName)
            ? $"a source you added ({source.PluginId})"
            : $"{source.DisplayName}, a source you added";
}
