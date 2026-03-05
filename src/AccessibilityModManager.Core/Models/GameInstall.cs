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
    public bool IsValid { get; set; }
    public string? DetectedVersion { get; set; }
    public InstallState ModState { get; set; } = InstallState.NotInstalled;
}
