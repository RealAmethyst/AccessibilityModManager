namespace AccessibilityModManager.Core.Models;

/// <summary>
/// Defines a supported game: how to find it and identify it.
/// </summary>
public sealed class GameDefinition
{
    public required string GameId { get; init; }
    public required string DisplayName { get; init; }
    public string? ModName { get; init; }
    public string? Description { get; init; }
    public string? SteamAppId { get; init; }
    public string? ExeName { get; init; }
    public List<PathProbeRule> ProbeRules { get; init; } = [];
    public List<Dependency> Dependencies { get; init; } = [];

    /// <summary>
    /// Filter tags (e.g. <c>screen-reader</c>, <c>controller-support</c>, <c>completable</c>).
    /// Used by the manager's filter sidebar. Free-form: registry defines a core set, but
    /// authors can add custom tags too. Optional; empty means no claims.
    /// </summary>
    public List<string> Tags { get; init; } = [];

    /// <summary>
    /// ISO 639-1 language codes (e.g. <c>en</c>, <c>es</c>, <c>ja</c>). Lowercase, no
    /// region suffix. Used by the manager's Languages filter. Optional.
    /// </summary>
    public List<string> Languages { get; init; } = [];

    /// <summary>
    /// Author-side template for lifecycle scripts (per F3=A — authoring template, not
    /// runtime fallback). When the AuthorTool creates a new release for this game, it
    /// pre-fills the release form from these defaults so the author doesn't have to retype.
    /// Once a release manifest is built, it's standalone — the manager only ever reads the
    /// release's manifest.
    /// </summary>
    public LifecycleScript? DefaultPreInstall { get; init; }
    public LifecycleScript? DefaultPostInstall { get; init; }
    public LifecycleScript? DefaultPostUninstall { get; init; }
}

/// <summary>
/// A rule for verifying a game install path (e.g., check that a specific file exists).
/// </summary>
public sealed class PathProbeRule
{
    public required string Type { get; init; } // "fileExists", "folderExists"
    public required string RelativePath { get; init; }
}
