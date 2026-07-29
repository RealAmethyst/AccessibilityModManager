using System.IO;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>What happened when a project's index.json was to be replaced.</summary>
public enum AdoptionResult
{
    /// <summary>Replaced. The folder now holds the new document.</summary>
    Replaced,

    /// <summary>
    /// The folder is no longer what it was compared against, so it was left alone. Not a failure —
    /// somebody got there first, and what they wrote is worth more than what was about to replace it.
    /// </summary>
    Superseded,

    /// <summary>The replacement failed and the folder is unchanged.</summary>
    Failed
}

/// <summary>
/// Replacing the author's <c>index.json</c> with a catalog adopted from the server.
///
/// <para>Its own class because it is the one place in reconciliation that writes, and because a
/// view model with eighteen constructor arguments and a network call in its constructor is not
/// somewhere a write can be tested. Everything here takes paths and bytes, so the compare, the
/// replacement and both failure boundaries can be exercised against a real folder.</para>
/// </summary>
public static class LocalIndexAdoption
{
    /// <summary>
    /// Replaces the file only if it still holds exactly the bytes the caller last compared against.
    ///
    /// <para>The compare and the replacement are two filesystem operations, and another process can
    /// write between them. That is deliberate, and it is a trade rather than an oversight: closing
    /// it means holding the file open exclusively across the replacement, which rules out the atomic
    /// temp-and-rename below, because a rename cannot replace a file this process is holding. The
    /// choice is between a microsecond in which another program's write could be lost, and a power
    /// cut during an in-place rewrite that leaves the author with a half-written index.json and a
    /// project that will not open. The second is worse and likelier, so the write stays atomic and
    /// the window stays open. Stronger formulations exist on Windows — a guard handle denying write
    /// and delete, with a POSIX-semantics rename over it — and are not portable enough to adopt
    /// without testing them properly.</para>
    /// </summary>
    /// <param name="error">Why it failed, when it did. Null otherwise.</param>
    public static AdoptionResult ReplaceIfUnchanged(
        string indexPath, byte[] expected, byte[] replacement, out string? error)
    {
        error = null;

        byte[] current;
        try
        {
            current = File.ReadAllBytes(indexPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return AdoptionResult.Failed;
        }

        if (!current.AsSpan().SequenceEqual(expected)) return AdoptionResult.Superseded;

        try
        {
            DurableFile.Write(indexPath, replacement);
            return AdoptionResult.Replaced;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return AdoptionResult.Failed;
        }
    }
}
