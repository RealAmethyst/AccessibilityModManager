namespace AccessibilityModManager.Core.Models;

/// <summary>
/// The central plugin registry fetched from the maintainer's GitHub.
/// </summary>
public sealed class PluginRegistry
{
    public required string RegistryVersion { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public required List<PluginEntry> Plugins { get; init; }
}
