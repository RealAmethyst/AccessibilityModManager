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

    /// <summary>
    /// Optional author info: bio + social links. Shown to manager users on the Authors page
    /// so they can find the author's discord/patreon/website. Lives in the per-plugin index
    /// so the author controls it directly without needing a registry-maintainer change.
    /// </summary>
    public PluginAuthorInfo? Author { get; init; }

    /// <summary>
    /// Reusable dependency definitions the AuthorTool's Add-Preset dropdown reads. Each
    /// preset is a named, ready-made <see cref="Dependency"/> entry — picking one copies the
    /// dependency into a game so the author doesn't have to retype URL/SHA/check fields. The
    /// list is purely an authoring convenience; the manager doesn't read it at runtime
    /// (it only reads the <c>Dependencies</c> baked into each <c>GameDefinition</c>).
    /// </summary>
    public List<DependencyPreset> DependencyPresets { get; init; } = [];
}

public sealed class DependencyPreset
{
    /// <summary>Stable identifier for the preset, e.g. <c>melonloader-x64-0.6.6</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable label shown in the AuthorTool's dropdown.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The actual dependency entry to clone into a game when the user picks this preset.</summary>
    public required Dependency Dependency { get; init; }
}

public sealed class PluginAuthorInfo
{
    public string? DisplayName { get; init; }
    public string? Bio { get; init; }
    public string? WebsiteUrl { get; init; }
    public string? DiscordUrl { get; init; }
    public string? PatreonUrl { get; init; }
    public string? GitHubUrl { get; init; }
    public string? DonationUrl { get; init; }
}
