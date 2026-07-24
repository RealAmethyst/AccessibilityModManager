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

    /// <summary>
    /// Portable apps (emulators) the manager has installed, keyed by the app's exe file name
    /// (lowercased) → its install folder. Lets a second game that runs on the same emulator reuse
    /// the existing install instead of downloading + placing it again (matched by the game's
    /// <c>ExeName</c>). Only manager-driven installs populate this — "Browse for Folder" does not,
    /// since that points at a copy the manager didn't install. Absent in old configs → empty.
    /// See EMULATOR_INSTALL_QUESTIONS.md (F1-B / F3).
    /// </summary>
    public Dictionary<string, string> InstalledEmulators { get; set; } = [];
    public string? LastSelectedGameId { get; set; }
    public List<string> EnabledPlugins { get; set; } = [];

    /// <summary>
    /// Filter selections on the Mods tab. Persisted across sessions so a user who always
    /// browses with "controller-support" filtered on doesn't have to re-check it every launch.
    /// "Reset filters" in the sidebar clears these.
    /// </summary>
    public List<string> SelectedTagFilters { get; set; } = [];
    public List<string> SelectedLanguageFilters { get; set; } = [];
    public List<string> SelectedAuthorFilters { get; set; } = [];
}
