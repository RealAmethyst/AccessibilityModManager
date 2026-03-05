namespace AccessibilityModManager.Core.Models;

/// <summary>
/// An entry in the central plugin registry. Represents one plugin author's listing.
/// </summary>
public sealed class PluginEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string Description { get; init; }
    public required Uri RepoIndexUrl { get; init; }
    public Uri? Website { get; init; }
    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// Flexible links the plugin author wants to share — Discord, GitHub, Patreon,
    /// donation pages, documentation, or anything else.
    /// Key = label (e.g., "Discord", "GitHub", "Donate"), Value = URL.
    /// </summary>
    public Dictionary<string, Uri> Links { get; init; } = [];

    /// <summary>
    /// Open-ended metadata for future use. Plugin authors can include any extra
    /// key-value info here without needing a schema change.
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = [];
}
