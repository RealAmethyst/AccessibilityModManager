using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

/// <summary>
/// Creates and locates the ASCII-named NTFS junction described by a
/// <see cref="AsciiPathShim"/>. Pure path/junction mechanics — it does not decide whether a
/// shim applies, ask for consent, verify the game, or persist any override; the caller owns
/// that orchestration.
/// </summary>
public interface IAsciiPathShimService
{
    /// <summary>
    /// Computes the junction path for a given real install path: the junction lives at the
    /// drive root of <paramref name="realInstallPath"/> with the shim's
    /// <see cref="AsciiPathShim.JunctionName"/> — e.g. real path on C: with name
    /// <c>PokemonTCGLive</c> → <c>C:\PokemonTCGLive</c>.
    /// </summary>
    string GetJunctionPath(AsciiPathShim shim, string realInstallPath);

    /// <summary>True if a directory (junction or otherwise) already exists at the path.</summary>
    bool JunctionPathExists(string junctionPath);

    /// <summary>
    /// Returns the real target a junction points at (its reparse target), or <c>null</c> if the
    /// path isn't a link or can't be read. Reads the link's own reparse data — it does not walk
    /// into the target — so it answers "does this junction point where I expect?" without
    /// depending on the target being reachable at that instant. Used to validate a freshly-created
    /// junction without racing a still-settling install.
    /// </summary>
    string? GetJunctionTarget(string junctionPath);

    /// <summary>
    /// Creates an NTFS junction at <paramref name="junctionPath"/> pointing at
    /// <paramref name="realTargetPath"/> via <c>cmd /c mklink /J</c> (no elevation needed).
    /// Throws on failure. Does not move or copy any files.
    /// </summary>
    Task CreateJunctionAsync(string junctionPath, string realTargetPath, CancellationToken ct = default);

    /// <summary>
    /// Removes the junction (reparse point) at <paramref name="junctionPath"/> with a
    /// <em>non-recursive</em> delete, so only the link is removed — the real files it pointed at are
    /// never touched. No-op if nothing exists there. Used to re-point a stale junction at a newly
    /// detected install location. Never call a recursive delete on a junction: it walks into and
    /// destroys the target.
    /// </summary>
    void RemoveJunctionLink(string junctionPath);
}
