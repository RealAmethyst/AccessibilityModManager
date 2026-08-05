namespace AccessibilityModManager.Core.Models;

/// <summary>
/// Names a user-added source is not allowed to present itself under.
///
/// <para>A source cannot take another developer's plugin id — that is blocked by the claim gate —
/// but the id is not what a listener hears. The mods list announces the developer's DISPLAY name,
/// and that comes from the source's own catalog, which the source controls. So a source with the id
/// <c>buu420</c> could publish an author block reading "Amethyst" and every row of theirs would be
/// announced as hers.</para>
///
/// <para>This applies to user-added sources ONLY. The registry's own entry for Amethyst is exactly
/// the thing being protected and must keep its name.</para>
///
/// <para>It is checked on every refresh rather than once when a source is added, because the name
/// lives in a document the source re-serves each time: a catalog that was innocuous when it was
/// added can be renamed the next day, and an add-time check would never see it.</para>
/// </summary>
public static class ReservedDeveloperNames
{
    /// <summary>
    /// Amethyst's own name, on Amethyst's own platform. Deliberately a list of one rather than a
    /// general anti-impersonation feature — other authors' names are their business, and a manager
    /// that policed every name would be making judgements it has no basis for.
    /// </summary>
    private static readonly string[] Reserved = ["amethyst"];

    /// <summary>
    /// True when <paramref name="displayName"/> would read as a reserved name.
    ///
    /// <para>Compared on a squashed form — case folded, and everything that is not a letter or digit
    /// removed — so <c>Amethyst</c>, <c>AMETHYST</c>, <c>amethyst.</c>, <c>A-M-E-T-H-Y-S-T</c> and
    /// <c>Amethyst Mods</c> all match. Spacing and punctuation are exactly what a spoken
    /// announcement flattens away, so matching on them would be matching on the part the listener
    /// never hears.</para>
    /// </summary>
    public static bool IsReserved(string? displayName)
    {
        var squashed = Squash(displayName);
        if (squashed.Length == 0) return false;

        // Containment, not equality: "Amethyst Mods" and "The Amethyst Project" both read as her.
        return Reserved.Any(reserved => squashed.Contains(reserved, StringComparison.Ordinal));
    }

    private static string Squash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var chars = value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();

        return new string(chars);
    }
}
