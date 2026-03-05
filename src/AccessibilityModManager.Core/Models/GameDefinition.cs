namespace AccessibilityModManager.Core.Models;

/// <summary>
/// Defines a supported game: how to find it and identify it.
/// </summary>
public sealed class GameDefinition
{
    public required string GameId { get; init; }
    public required string DisplayName { get; init; }
    public string? SteamAppId { get; init; }
    public string? ExeName { get; init; }
    public List<PathProbeRule> ProbeRules { get; init; } = [];
    public List<Dependency> Dependencies { get; init; } = [];
}

/// <summary>
/// A rule for verifying a game install path (e.g., check that a specific file exists).
/// </summary>
public sealed class PathProbeRule
{
    public required string Type { get; init; } // "fileExists", "folderExists"
    public required string RelativePath { get; init; }
}
