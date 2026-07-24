using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

/// <summary>
/// Resolves a non-Steam game's real install path from its <see cref="GameDefinition.RegistryProbe"/>.
/// Read-only — never writes the registry.
/// </summary>
public interface IRegistryGameDetector
{
    /// <summary>
    /// Returns the verified real install path for the game, or null if the registry value is
    /// missing/empty or no path (including probed subfolders) passes verification. The game's
    /// <see cref="GameDefinition.AsciiPathShim"/>, if any, is NOT applied here — this is the
    /// real path; the junction is created later, at install time.
    /// </summary>
    string? ResolveInstallPath(GameDefinition game);
}
