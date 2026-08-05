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
    public string PluginRegistryUrl => "https://accessibilitymods.com/registry/plugin-registry.json";

    /// <summary>
    /// Catalogs the user added themselves, in the order they added them. That order is the rule for
    /// who owns a plugin id when two sources want the same one, so nothing may sort this list in
    /// place — display code sorts a copy.
    ///
    /// <para>Unlike <see cref="PluginRegistryUrl"/> this is genuinely user data and is
    /// deserialized. That is safe because a source is only ever one author's INDEX: it contributes
    /// a single plugin id and cannot introduce a second author or move the trust anchor. Adding a
    /// source and redirecting the registry stay different operations.</para>
    /// </summary>
    public List<UserPluginSource> UserPluginSources { get; set; } = [];

    /// <summary>
    /// The last index address the SIGNED registry gave for each plugin id, recorded every time one
    /// of its catalogs is read.
    ///
    /// <para>This is what makes it possible to keep a developer working when they leave the
    /// registry: their mods are still installed, but nothing names where their catalog lives any
    /// more. The address is written down while the registry still vouches for it, so the record is
    /// something the signed registry said, not something inferred later.</para>
    ///
    /// <para>It lives here rather than in the index cache — which also holds it — because clearing
    /// the cache is a routine recovery step, and it must not be able to strand a developer whose
    /// mods are installed.</para>
    /// </summary>
    public Dictionary<string, string> KnownPluginAddresses { get; set; } = [];

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
