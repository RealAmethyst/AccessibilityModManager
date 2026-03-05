namespace AccessibilityModManager.Core.Models;

public enum ChangeType
{
    Added,
    Replaced,
    Patched
}

/// <summary>
/// Tracks a file that was changed during install, for rollback/uninstall.
/// </summary>
public sealed class FileChange
{
    public required ChangeType Type { get; init; }
    public required string RelativePath { get; init; }
    public string? BackupRelativePath { get; init; }
}

/// <summary>
/// A receipt recording exactly what was installed, by which plugin, for which game.
/// Used for uninstall and rollback.
/// </summary>
public sealed class InstallReceipt
{
    public required string GameId { get; init; }
    public required string PluginId { get; init; }
    public required string InstalledVersion { get; init; }
    public required DateTime InstalledAt { get; init; }
    public required List<FileChange> Changes { get; init; }
    public required string BackupFolder { get; init; }
    public required string ManifestHash { get; init; }
}
