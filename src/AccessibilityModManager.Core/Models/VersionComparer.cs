namespace AccessibilityModManager.Core.Models;

/// <summary>
/// Compares version strings using a SemVer-leaning ordering: numeric parts compared as numbers,
/// non-numeric parts as ordinal strings. Pre-release suffixes (after '-') sort lower than the
/// equivalent stable version.
///
/// Examples (lower → higher):
///   "1.2.0" &lt; "1.10.0" &lt; "1.10.1" &lt; "2.0.0"
///   "1.0.0-alpha" &lt; "1.0.0-beta" &lt; "1.0.0"
/// </summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var (xCore, xPre) = SplitPreRelease(x);
        var (yCore, yPre) = SplitPreRelease(y);

        var coreCmp = CompareDotted(xCore, yCore);
        if (coreCmp != 0) return coreCmp;

        // Per SemVer: a version with a pre-release suffix is lower than one without.
        if (xPre is null && yPre is null) return 0;
        if (xPre is null) return 1;
        if (yPre is null) return -1;
        return CompareDotted(xPre, yPre);
    }

    private static (string core, string? pre) SplitPreRelease(string version)
    {
        var dash = version.IndexOf('-');
        return dash < 0 ? (version, null) : (version[..dash], version[(dash + 1)..]);
    }

    private static int CompareDotted(string a, string b)
    {
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        var len = Math.Max(aParts.Length, bParts.Length);

        for (var i = 0; i < len; i++)
        {
            var ap = i < aParts.Length ? aParts[i] : "0";
            var bp = i < bParts.Length ? bParts[i] : "0";
            var cmp = CompareSegment(ap, bp);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    private static int CompareSegment(string a, string b)
    {
        var aIsNum = int.TryParse(a, out var ai);
        var bIsNum = int.TryParse(b, out var bi);

        if (aIsNum && bIsNum) return ai.CompareTo(bi);
        if (aIsNum) return -1; // numeric < non-numeric per SemVer pre-release ordering
        if (bIsNum) return 1;
        return string.CompareOrdinal(a, b);
    }
}
