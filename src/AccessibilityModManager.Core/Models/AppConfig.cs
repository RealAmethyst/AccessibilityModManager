namespace AccessibilityModManager.Core.Models;

/// <summary>
/// Application configuration persisted to AppData.
/// </summary>
public sealed class AppConfig
{
    public string PluginRegistryUrl { get; set; } = "https://raw.githubusercontent.com/PLACEHOLDER/accessibility-mod-manager/main/plugin-registry.json";
    public string DefaultChannel { get; set; } = "stable";
    public Dictionary<string, string> KnownGameOverrides { get; set; } = [];
    public string? LastSelectedGameId { get; set; }
    public List<string> EnabledPlugins { get; set; } = [];
}
