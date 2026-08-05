namespace AccessibilityModManager.Core.Models;

/// <summary>A developer carried over from the registry, so their installed mods keep working.</summary>
public sealed record CarriedOverSource(UserPluginSource Source, string Describe);

/// <summary>
/// Keeps a developer working when they leave the signed registry with mods still installed.
///
/// <para>Without this, removing someone from the registry orphans everyone who installed their
/// mods: the files stay on disk, but nothing tells the manager where that developer's catalog lives
/// any more, so there are no updates and no way back. The mods do not break — uninstall works from
/// receipts — but the developer simply disappears.</para>
///
/// <para><b>Only when their mods are installed.</b> A developer the user never installed anything
/// from is not carried over; they simply leave, which is what removing them from the registry
/// means. The install is the whole justification: the registry vouched for this developer, the user
/// installed on that basis, and this keeps working what already worked rather than starting
/// something new. That is why it needs no prompt — but it IS announced, because a source appearing
/// in someone's list with no explanation is worse than the problem it solves.</para>
///
/// <para><b>The address is one the signed registry gave.</b> It comes from the record written while
/// that registry still listed the developer, never from the catalog itself and never guessed.</para>
/// </summary>
public static class RegistryDepartureMigration
{
    /// <param name="registryPlugins">
    /// Entries from a registry that was ACCEPTED — signature verified. Never call this with a
    /// registry that failed to load: every plugin would look absent at once, and the manager would
    /// convert an outage into a pile of user sources.
    /// </param>
    /// <param name="existingSources">Sources already configured, so nobody is carried over twice.</param>
    /// <param name="installedPluginIds">Plugin ids with something installed under them.</param>
    /// <param name="knownAddresses">Last index address the registry gave, by plugin id.</param>
    public static IReadOnlyList<CarriedOverSource> FindDepartures(
        IEnumerable<PluginEntry> registryPlugins,
        IEnumerable<UserPluginSource> existingSources,
        IEnumerable<string> installedPluginIds,
        IReadOnlyDictionary<string, string> knownAddresses,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(registryPlugins);
        ArgumentNullException.ThrowIfNull(existingSources);
        ArgumentNullException.ThrowIfNull(installedPluginIds);
        ArgumentNullException.ThrowIfNull(knownAddresses);

        var inRegistry = registryPlugins
            .Select(p => SafeId.Canonical(p.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var alreadyASource = existingSources
            .Select(s => SafeId.Canonical(s.PluginId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var carried = new List<CarriedOverSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pluginId in installedPluginIds)
        {
            if (string.IsNullOrWhiteSpace(pluginId)) continue;

            var key = SafeId.Canonical(pluginId);
            if (!seen.Add(key)) continue;

            // Still listed: nothing to do. This is the ordinary case for everyone.
            if (inRegistry.Contains(key)) continue;

            // Already carried over, or the user added them back themselves.
            if (alreadyASource.Contains(key)) continue;

            // No address on record — the manager never saw this developer under a registry that
            // named where their catalog lives, so there is nothing to point at. Their mods stay
            // installed and uninstallable; there is just no catalog. Guessing an address would be
            // inventing a download location for someone else's mods.
            if (!TryFindAddress(knownAddresses, pluginId, out var address)) continue;

            // The id has to be usable as an identity in its own right before it becomes a source.
            if (!SafeId.IsValid(pluginId, out _)) continue;
            if (!Uri.TryCreate(address, UriKind.Absolute, out var url)) continue;
            if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) continue;

            carried.Add(new CarriedOverSource(
                new UserPluginSource
                {
                    PluginId = pluginId,
                    IndexUrl = url.AbsoluteUri,
                    // No display name: the catalog's own author block supplies it on the next
                    // refresh. Copying a name from anywhere else would be asserting something about
                    // a developer this record only knows the address of.
                    DisplayName = null,
                    AddedUtc = now,
                    // Deliberately NOT NoticeAcceptedUtc — the user never saw a notice for this one.
                    MigratedFromRegistryUtc = now,
                    AcceptedFor = UserPluginSource.AcceptanceKey(pluginId, url.AbsoluteUri)
                },
                pluginId));
        }

        return carried;
    }

    private static bool TryFindAddress(
        IReadOnlyDictionary<string, string> knownAddresses, string pluginId, out string address)
    {
        // Recorded under whatever spelling the registry used, so the lookup matches the way ids are
        // compared everywhere else rather than exactly.
        foreach (var (id, value) in knownAddresses)
        {
            if (!string.Equals(SafeId.Canonical(id), SafeId.Canonical(pluginId),
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(value)) continue;

            address = value;
            return true;
        }

        address = "";
        return false;
    }
}
