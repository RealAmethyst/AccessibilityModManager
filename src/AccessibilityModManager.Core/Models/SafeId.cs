namespace AccessibilityModManager.Core.Models;

/// <summary>
/// The one rule for what a plugin, game, source or dependency id may be, and what makes two of them
/// the same identity.
///
/// <para>These ids become Windows directory names — receipts live at
/// <c>{root}/{pluginId}/{gameId}/</c>, with cached uninstall scripts and rollback backups beneath —
/// so "different id" has to mean "different folder". On Windows it does not, for two reasons the
/// character whitelist alone misses:</para>
///
/// <list type="bullet">
/// <item>Windows silently STRIPS trailing dots and spaces from a name, so <c>amethyst.</c> and
/// <c>amethyst</c> are one directory while being different strings. A source publishing as
/// <c>amethyst.</c> would pass an id-uniqueness check and then write into the registry plugin's
/// receipts.</item>
/// <item>Device names like <c>CON</c>, <c>NUL</c> and <c>COM1</c> are reserved at every level and
/// keep their meaning even with an extension (<c>NUL.txt</c>), so a path built from one does not
/// behave like a folder at all.</item>
/// </list>
///
/// <para>Both are refused here, and <see cref="Canonical"/> is what identity comparisons use, so an
/// id that somehow reaches a comparison without passing <see cref="IsValid"/> still COLLIDES with
/// its equivalent rather than slipping past as distinct. Rejecting at the door and comparing
/// canonically are different defences and this keeps both.</para>
///
/// <para>Lives in Core because the claim gate compares ids and Core cannot see Infrastructure.
/// <c>PathSafety.EnsureSafeId</c> delegates here rather than keeping a second copy — the two rules
/// had already drifted once.</para>
/// </summary>
public static class SafeId
{
    public const int MaxLength = 64;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// True when <paramref name="id"/> is one safe path segment that means only itself.
    /// <paramref name="reason"/> is written to be read aloud when it is not.
    /// </summary>
    public static bool IsValid(string? id, out string reason)
    {
        reason = "";

        if (string.IsNullOrEmpty(id))
        {
            reason = "it is empty";
            return false;
        }

        if (id.Length > MaxLength)
        {
            reason = $"it is longer than {MaxLength} characters";
            return false;
        }

        foreach (var c in id)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_') continue;
            reason = "it contains characters other than letters, digits, dashes, dots and underscores";
            return false;
        }

        // "." and ".." are the current and parent directory, not names.
        if (id.All(c => c == '.'))
        {
            reason = "it is made only of dots";
            return false;
        }

        // The trailing-dot case. Windows drops it, so this id and the one without it are the same
        // folder — which would make two "different" developers share one receipt directory.
        if (id.EndsWith('.'))
        {
            reason = "it ends with a dot, which Windows removes — that would make it the same folder as the id without it";
            return false;
        }

        if (ReservedDeviceNames.Contains(BaseName(id)))
        {
            reason = $"'{BaseName(id)}' is a name Windows reserves for hardware devices";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The form two ids are compared as. Strips what Windows would strip, so an id that reached a
    /// comparison without being validated still collides with its filesystem equivalent instead of
    /// reading as a separate identity.
    ///
    /// <para>Case is NOT folded here — callers compare with an ordinal-ignore-case comparer, and
    /// folding twice would hide which of the two behaviours a caller was relying on.</para>
    /// </summary>
    public static string Canonical(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        return id.TrimEnd('.', ' ');
    }

    /// <summary>The part before the first dot — what Windows tests against its device names.</summary>
    private static string BaseName(string id)
    {
        var dot = id.IndexOf('.');
        return dot < 0 ? id : id[..dot];
    }
}
