using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.App.ViewModels;

/// <summary>
/// The one place that answers "what do we call this developer?".
///
/// <para>Three documents can name the same person: the developer's own plugin index (which they
/// control directly), the signed registry listing, and — as a last resort — the plugin id, which is
/// a slug like <c>digimon-tools</c> and not a name at all. Showing the id was the original
/// complaint: the mods list, the author filter and a mod's own page each reached for whichever
/// value was nearest.</para>
///
/// <para>Every check is whitespace-aware rather than a null check. Registry validation does not
/// require author or name to be non-empty, so <c>??</c> would happily settle on <c>" "</c> and
/// leave a row announcing nothing.</para>
/// </summary>
public static class DeveloperNames
{
    /// <summary>
    /// Resolves the display name for <paramref name="pluginId"/>. Either source may be null — the
    /// index is absent before it loads or when it was refused, and the registry entry is absent on
    /// views that never fetched one. The id is always returned rather than an empty string, so a
    /// row never announces a mod with no author at all.
    /// </summary>
    public static string Resolve(PluginRepoIndex? index, PluginEntry? entry, string pluginId)
        => Resolve(index, entry, userSourceName: null, pluginId);

    /// <summary>
    /// As above, plus the name the user saw when they added a source. A user-added source has no
    /// registry listing to fall back to, so without this its rows would announce a slug like
    /// <c>buu420</c> instead of a person. It sits BELOW the index's own author block, which the
    /// author still controls and keeps current, and above the bare id.
    /// </summary>
    public static string Resolve(
        PluginRepoIndex? index, PluginEntry? entry, string? userSourceName, string pluginId)
        => Trimmed(index?.Author?.DisplayName)
           ?? Trimmed(entry?.Author)
           ?? Trimmed(entry?.Name)
           ?? Trimmed(userSourceName)
           ?? pluginId;

    /// <summary>
    /// The name for a USER-ADDED source, which is not allowed to present itself under a reserved
    /// name (see <see cref="ReservedDeveloperNames"/>).
    ///
    /// <para>The plugin id is the fallback when it tries. That is deliberately not a refusal of the
    /// whole catalog: the name lives in a document the source re-serves on every refresh, so a
    /// source could rename itself into a ban at any moment and simply vanish from the user's mods
    /// list, which is a confusing outcome for something they chose to install. Announcing it by its
    /// id instead is equally effective — the impersonation is what fails, not the source.</para>
    ///
    /// <para><paramref name="wasReserved"/> reports that it happened, so the refusal can be said out
    /// loud rather than looking like the source simply having a scruffy name.</para>
    /// </summary>
    public static string ResolveUserSource(
        PluginRepoIndex? index, string? savedName, string pluginId, out bool wasReserved)
    {
        var candidate = Trimmed(index?.Author?.DisplayName) ?? Trimmed(savedName);

        wasReserved = ReservedDeveloperNames.IsReserved(candidate);
        if (wasReserved) return pluginId;

        return candidate ?? pluginId;
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
