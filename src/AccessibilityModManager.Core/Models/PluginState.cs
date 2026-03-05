namespace AccessibilityModManager.Core.Models;

/// <summary>
/// Local user state for a plugin (enabled/disabled, last fetch time).
/// </summary>
public sealed class PluginState
{
    public required string PluginId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastFetchedAt { get; set; }
}
