namespace AccessibilityModManager.Core.Models;

/// <summary>
/// Application configuration persisted to AppData.
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// The plugin registry URL is intentionally NOT user-editable. It's the trust anchor the
    /// signed registry hangs off; allowing it to be redirected at runtime would let a malicious
    /// config or social-engineering attack point users at an attacker-controlled registry.
    /// Read-only computed property so deserializing an old config that includes this field is
    /// silently ignored (System.Text.Json skips read-only properties on deserialization).
    /// </summary>
    public string PluginRegistryUrl => "https://github.com/RealAmethyst/accessibility-mod-manager-registry/releases/latest/download/plugin-registry.json";

    public string DefaultChannel { get; set; } = "stable";
    public Dictionary<string, string> KnownGameOverrides { get; set; } = [];
    public string? LastSelectedGameId { get; set; }
    public List<string> EnabledPlugins { get; set; } = [];
}
