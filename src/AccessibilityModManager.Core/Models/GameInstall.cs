namespace AccessibilityModManager.Core.Models;

public enum InstallState
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    Unknown
}

/// <summary>
/// A detected game installation on the user's machine, linked to the plugin that defines it.
/// </summary>
public sealed class GameInstall
{
    public required GameDefinition Game { get; init; }
    public required string PluginId { get; init; }
    public required string InstallPath { get; init; }
    /// <summary>The Steam library root that contained this game, when Steam detected it.</summary>
    public string? SteamLibraryPath { get; init; }
    /// <summary>The Steam client root whose library list found this game.</summary>
    public string? SteamRootPath { get; init; }
    /// <summary>
    /// Existing per-game Proton prefix, when found. Presence alone does not establish that Steam
    /// currently launches this game through Proton or that the mod works there.
    /// </summary>
    public string? ProtonPrefixPath { get; init; }
    public bool IsValid { get; set; }
    public string? DetectedVersion { get; set; }
    public InstallState ModState { get; set; } = InstallState.NotInstalled;
}
