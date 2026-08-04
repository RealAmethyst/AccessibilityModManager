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
        => Trimmed(index?.Author?.DisplayName)
           ?? Trimmed(entry?.Author)
           ?? Trimmed(entry?.Name)
           ?? pluginId;

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
