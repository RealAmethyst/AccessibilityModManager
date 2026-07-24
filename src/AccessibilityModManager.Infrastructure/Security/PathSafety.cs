namespace AccessibilityModManager.Infrastructure.Security;

/// <summary>
/// Helpers for building filesystem paths from untrusted input: identifiers (plugin IDs, game IDs,
/// dependency IDs) and author-written relative folder paths (auto-install target dirs) that come
/// from plugin repo indexes and manifests. Those inputs are not independently signed, so a hostile
/// index could choose values like <c>..\escape</c> or an absolute path and, without a containment
/// check, redirect writes outside their intended root.
///
/// All containment comparisons use Windows path semantics: case-insensitive, and immune to
/// trailing-separator aliases ("C:\Games\" vs "C:\Games"). Building a prefix by blindly appending
/// a separator produced doubled-separator prefixes that falsely rejected every legitimate child —
/// the "extracting to D:\ fails as zip slip" class of bug.
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

        if (!IsContained(rootFull, combined))
        {
            throw new InvalidOperationException(
                $"Unsafe path: '{string.Join("/", segments)}' resolves outside '{root}'.");
        }

        return combined;
    }

    /// <summary>
    /// True when <paramref name="candidateFullPath"/> is <paramref name="root"/> or lives beneath
    /// it. Both are normalized with <see cref="Path.GetFullPath(string)"/> first, so <c>..</c>
    /// segments are resolved before the comparison. Case-insensitive, and correct for roots
    /// written with or without a trailing separator (including bare drive roots like "D:\").
    /// </summary>
    public static bool IsContained(string root, string candidateFullPath)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateFullPath));

        if (string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
            return true;

        // TrimEndingDirectorySeparator leaves a bare drive root as "D:\", which already ends in a
        // separator — appending another would rebuild the doubled-prefix bug this class exists to
        // kill.
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <paramref name="candidateFullPath"/> resolved to a full path when it is contained in
    /// <paramref name="root"/>; throws otherwise. <paramref name="description"/> names the value in
    /// the error ("AutoInstall targetDir", "install target", ...).
    /// </summary>
    public static string EnsureContained(string root, string candidateFullPath, string description)
    {
        var full = Path.GetFullPath(candidateFullPath);
        if (!IsContained(root, full))
        {
            throw new InvalidOperationException(
                $"{description} '{candidateFullPath}' resolves outside '{root}'.");
        }
        return full;
    }

    /// <summary>
    /// Normalizes an author-written folder path that is meant to be relative to some root (an
    /// auto-install <c>targetDir</c>). Authors write these by hand, so leading and trailing
    /// separators are treated as noise, not intent: <c>/Updater/1.5.0/</c> means
    /// <c>Updater\1.5.0</c>. Empty, null, whitespace, and bare <c>.</c> mean the root itself and
    /// return an empty string. Absolute values (drive-qualified or UNC) and <c>..</c> segments are
    /// rejected — the value must name a place inside the root, and a traversal can only be an
    /// authoring mistake or an attack.
    /// </summary>
    public static string NormalizeRelativeDir(string? relativeDir, string description)
    {
        if (string.IsNullOrWhiteSpace(relativeDir))
            return string.Empty;

        var value = relativeDir.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        // Check for absolute forms BEFORE trimming separators — "\\server\share" must not survive
        // as "server\share", and "C:\x" must not survive as "C:\x" minus nothing.
        if (value.Contains(':') || value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{description} '{relativeDir}' must be a folder path relative to the game folder, not an absolute path.");
        }

        var parts = value.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".") continue; // harmless authoring noise
            if (part == "..")
            {
                throw new InvalidOperationException(
                    $"{description} '{relativeDir}' must not contain '..' segments.");
            }
            kept.Add(part);
        }

        return string.Join(Path.DirectorySeparatorChar, kept);
    }

    /// <summary>
    /// Validates that <paramref name="value"/> is a bare Windows file name — no folders, no root,
    /// no <c>.</c>/<c>..</c>, and no characters Windows forbids in file names (which includes the
    /// colon, closing off NTFS alternate-data-stream syntax like <c>tool.dll:payload</c>).
    /// Returns the trimmed name; throws with <paramref name="description"/> in the message.
    /// </summary>
    public static string EnsureLeafFileName(string? value, string description)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name) || name == "." || name == ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{description} '{value}' must be a plain file name with no folders in it.");
        }
        return name;
    }

    /// <summary>
    /// Validates that an identifier (plugin id, game id, dependency id) is one safe path segment:
    /// letters, digits, dash, dot, underscore — nothing that becomes a separator, a root, or a
    /// traversal when the id is used as a folder name. Ids come from unsigned indexes and the
    /// signed registry alike; values like <c>author\plugin</c> would nest folders the receipt
    /// enumeration then misreads. Returns the trimmed id; throws with a clear message otherwise.
    /// </summary>
    public static string EnsureSafeId(string? value, string description)
    {
        // Deliberately NO trimming: callers persist the original value, so a padded id that
        // "passes after trim" would still reach dictionaries and folder names with whitespace
        // in it. Whitespace fails the charset check like any other unsafe character.
        if (string.IsNullOrEmpty(value) || value == "." || value == ".." ||
            !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
        {
            throw new InvalidOperationException(
                $"{description} '{value}' must use only letters, digits, dashes, dots, and underscores " +
                "(no spaces).");
        }
        return value;
    }

    /// <summary>
    /// Rejects a path whose components BELOW <paramref name="root"/> include a reparse point
    /// (junction, symlink, mount point). Containment checks compare path TEXT, so a link sitting
    /// inside the game folder — say a <c>Mods</c> folder that is secretly a junction elsewhere —
    /// would let writes, restores, and deletes walk outside the folder the text says they stay in.
    ///
    /// The root itself is deliberately exempt: an install root that IS a junction is a supported
    /// setup (the ASCII path shim, e.g. <c>C:\PokemonTCGLive</c>). The policy is "the root may be
    /// a link; nothing deeper may be". Components that don't exist yet are fine — they're about to
    /// be created as ordinary files/folders by the very write being validated.
    ///
    /// Call this at write/restore/delete time on the exact path being touched (not as a whole-tree
    /// scan): only paths actually mutated matter, and an unrelated cloud-placeholder subtree the
    /// mod never touches must not block an install.
    /// </summary>
    public static void EnsureNoReparseTraversal(string root, string candidateFullPath, string description)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateFullPath));

        if (string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
            return;

        if (!IsContained(rootFull, candidate))
        {
            throw new InvalidOperationException(
                $"{description} '{candidateFullPath}' resolves outside '{root}'.");
        }

        var current = rootFull;
        var remainder = candidate[rootFull.Length..].TrimStart(Path.DirectorySeparatorChar);
        foreach (var part in remainder.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                return; // this component (and everything below it) doesn't exist yet
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0 && IsTraversalLink(current, attributes))
            {
                throw new InvalidOperationException(
                    $"{description} '{candidateFullPath}' passes through '{current}', which is a link " +
                    "(reparse point) inside the game folder. Refusing to touch files through it — a link " +
                    "here can redirect writes outside the game folder.");
            }
        }
    }

    /// <summary>
    /// True when a reparse point actually REDIRECTS path traversal. Cloud placeholders (OneDrive
    /// Files On-Demand, dedup) carry the reparse attribute but stay in place — rejecting them
    /// would break installs into perfectly ordinary synced folders. <see cref="FileSystemInfo.LinkTarget"/>
    /// is non-null exactly for the traversal-capable kinds (symbolic links and junctions/mount
    /// points), which are what can send a write somewhere else. Unreadable link metadata counts
    /// as a link — fail closed.
    ///
    /// Note this is a check-then-act guard: it stops planted links at validation time, not a
    /// same-user process swapping the path between the check and the write. That adversary
    /// already owns the user account and is outside this app's protection boundary.
    /// </summary>
    private static bool IsTraversalLink(string path, FileAttributes attributes)
    {
        try
        {
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Non-throwing <see cref="NormalizeRelativeDir"/>. False (with the original value echoed back)
    /// when the value is absolute or contains traversal — callers that can't surface an exception
    /// keep the raw value and let the manager-side check produce the error.
    /// </summary>
    public static bool TryNormalizeRelativeDir(string? relativeDir, out string normalized)
    {
        try
        {
            normalized = NormalizeRelativeDir(relativeDir, "path");
            return true;
        }
        catch (InvalidOperationException)
        {
            normalized = relativeDir ?? string.Empty;
            return false;
        }
    }
}
