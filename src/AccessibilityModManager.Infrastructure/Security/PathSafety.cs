namespace AccessibilityModManager.Infrastructure.Security;

/// <summary>
/// Helpers for building filesystem paths from untrusted identifiers (plugin IDs, game IDs,
/// dependency IDs) that come from plugin repo indexes and manifests. Those inputs are not
/// independently signed, so a hostile index could choose IDs like <c>..\escape</c> or an absolute
/// path and, without a containment check, redirect receipt/backup writes outside their intended
/// root.
/// </summary>
public static class PathSafety
{
    /// <summary>
    /// Combines <paramref name="root"/> with <paramref name="segments"/> and returns the full path,
    /// throwing when the result escapes <paramref name="root"/> (a segment contained <c>..</c>, a
    /// path separator that walked out, or an absolute/rooted value). The returned path is always
    /// <paramref name="root"/> itself or something strictly beneath it.
    /// </summary>
    public static string CombineContained(string root, params string[] segments)
    {
        var rootFull = Path.GetFullPath(root);

        var all = new string[segments.Length + 1];
        all[0] = rootFull;
        Array.Copy(segments, 0, all, 1, segments.Length);
        var combined = Path.GetFullPath(Path.Combine(all));

        if (combined != rootFull &&
            !combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsafe path: '{string.Join("/", segments)}' resolves outside '{root}'.");
        }

        return combined;
    }

    /// <summary>
    /// True when <paramref name="candidateFullPath"/> is <paramref name="root"/> or lives beneath it.
    /// Both are normalized with <see cref="Path.GetFullPath(string)"/> first, so <c>..</c> segments
    /// are resolved before the comparison.
    /// </summary>
    public static bool IsContained(string root, string candidateFullPath)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(candidateFullPath);
        return full == rootFull ||
               full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
