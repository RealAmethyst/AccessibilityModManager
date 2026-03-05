namespace AccessibilityModManager.Core.Models;

/// <summary>
/// A specific mod version available for download from a plugin's repo.
/// </summary>
public sealed class ModRelease
{
    public required string GameId { get; init; }
    public required string PluginId { get; init; }
    public required string Version { get; init; }
    public required string Channel { get; init; } // "stable" or "beta"
    public required Uri PackageUrl { get; init; }
    public required string Sha256 { get; init; }
    public string? ChangelogUrl { get; init; }
    public CompatibilityInfo? Compatibility { get; init; }
}

public sealed class CompatibilityInfo
{
    public string? MinGameVersion { get; init; }
    public string? MaxGameVersion { get; init; }
    public string? Notes { get; init; }
}
