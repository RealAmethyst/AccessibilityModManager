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

    /// <summary>
    /// Non-Steam detection: locate the install path by reading a Windows registry value. For
    /// games that aren't on Steam but record their install location in the registry (e.g. the
    /// PTC launcher for Pokémon TCG Live). Optional; null means registry detection isn't used.
    /// </summary>
    public RegistryProbe? RegistryProbe { get; init; }

    /// <summary>
    /// Optional. When set, the game's real install path can't be used directly by the mod
    /// loader (e.g. MelonLoader can't bootstrap from a non-ASCII path) but also can't be moved
    /// (e.g. the path is registered for OAuth login). The manager creates an ASCII-named NTFS
    /// junction pointing at the real install and uses the junction as the install path.
    /// </summary>
    public AsciiPathShim? AsciiPathShim { get; init; }

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

/// <summary>
/// Non-Steam detection via a Windows registry value. Detection is read-only — the manager never
/// writes the registry.
/// </summary>
public sealed class RegistryProbe
{
    /// <summary>"HKCU" (HKEY_CURRENT_USER) or "HKLM" (HKEY_LOCAL_MACHINE).</summary>
    public required string Hive { get; init; }

    /// <summary>
    /// Subkey path under the hive, e.g.
    /// <c>Software\The Pokémon Company International\Pokémon Trading Card Game Live</c>.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>The value name holding the path, e.g. <c>Path</c>.</summary>
    public required string Value { get; init; }

    /// <summary>
    /// The registry value sometimes points at a parent/publisher folder rather than the game
    /// folder itself (the PTC launcher records the publisher folder, with the game in a
    /// subfolder). When true (default), if the value's path doesn't itself verify, the manager
    /// probes its immediate child directories and uses the first one that passes the game's
    /// verification (<see cref="GameDefinition.ExeName"/> / <see cref="GameDefinition.ProbeRules"/>).
    /// </summary>
    public bool ProbeSubfolders { get; init; } = true;
}

/// <summary>
/// Tells the manager to install/launch through an ASCII-named NTFS junction instead of the
/// game's real (problematic) install path. The junction is created on the same volume as the
/// real install, on first install, with the user's consent. The real files never move — the
/// junction is just an additional ASCII name for the same directory, which keeps a non-ASCII-
/// intolerant loader (MelonLoader) happy while leaving an OAuth-registered path intact. See
/// PTCGL_INSTALL_QUESTIONS.md for the full rationale.
/// </summary>
public sealed class AsciiPathShim
{
    /// <summary>
    /// Leaf name of the junction, created at the drive root of the real install — e.g.
    /// <c>PokemonTCGLive</c> becomes <c>C:\PokemonTCGLive</c> (or <c>D:\PokemonTCGLive</c> if the
    /// game lives on D:). ASCII only; that's the whole point of the shim.
    /// </summary>
    public required string JunctionName { get; init; }

    /// <summary>
    /// Plain-language explanation shown in the one-time consent prompt before the junction is
    /// created — why the link is needed and a reassurance that the game itself isn't moved.
    /// </summary>
    public required string Reason { get; init; }
}
