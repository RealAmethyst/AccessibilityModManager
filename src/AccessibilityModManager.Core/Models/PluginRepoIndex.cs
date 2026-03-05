namespace AccessibilityModManager.Core.Models;

/// <summary>
/// A plugin author's repo index listing their supported games and mod releases.
/// </summary>
public sealed class PluginRepoIndex
{
    public required string PluginId { get; init; }
    public required string RepoVersion { get; init; }
    public required DateTime GeneratedAt { get; init; }
    public required List<GameDefinition> Games { get; init; }
    public required Dictionary<string, List<ModRelease>> ReleasesByGameId { get; init; }
}
