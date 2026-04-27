using System.Text.Json.Serialization;

namespace AccessibilityModManager.Core.Models;

/// <summary>
/// The manifest.json inside a mod package ZIP. Drives the entire install process.
/// </summary>
public sealed class Manifest
{
    public required string GameId { get; init; }
    public required string PluginId { get; init; }
    public required string ModVersion { get; init; }
    public List<InstallAction> InstallActions { get; init; } = [];
    public List<Dependency> Dependencies { get; init; } = [];
    public List<VerifyRule> Verify { get; init; } = [];

    /// <summary>
    /// Optional script run after backup is taken but before the file-copy install actions.
    /// If <see cref="LifecycleScript.FailureFatal"/> is true, a non-zero exit aborts the
    /// install and rolls back from the backup.
    /// </summary>
    public LifecycleScript? PreInstall { get; init; }

    /// <summary>
    /// Optional script run after install actions complete successfully. Same failure
    /// semantics as <see cref="PreInstall"/>.
    /// </summary>
    public LifecycleScript? PostInstall { get; init; }

    /// <summary>
    /// Optional script run during uninstall, before file removal and backup restore. Cached
    /// alongside the receipt at install time so it can run offline. Best-effort — failure
    /// logs but doesn't block the uninstall.
    /// </summary>
    public LifecycleScript? PostUninstall { get; init; }
}

/// <summary>
/// A script the manager runs at one of the install/uninstall lifecycle points. Author-
/// supplied, arbitrary code — the manager warns the user before running. See
/// POST_INSTALL_SCRIPT_DESIGN.md.
/// </summary>
public sealed class LifecycleScript
{
    /// <summary>
    /// Path inside the wrapped ZIP to the executable. Must end in .exe, .ps1, .cmd, or
    /// .bat — the manager picks the runner by extension.
    /// </summary>
    public required string Executable { get; init; }

    /// <summary>
    /// True if the script needs admin rights. Manager surfaces a red "needs admin" warning
    /// and spawns the process via UAC.
    /// </summary>
    public bool NeedsAdmin { get; init; }

    /// <summary>
    /// If true, a non-zero exit aborts the install and triggers a rollback. If false, the
    /// manager logs the failure but keeps the install in place.
    /// </summary>
    public bool FailureFatal { get; init; } = true;

    public required string What { get; init; }
    public required string Why { get; init; }
    public required string Modifies { get; init; }

    /// <summary>
    /// When true, the manifest builder also emits a copyFile install action so this script
    /// lands in the game folder root permanently. The script still runs from the temp staging
    /// folder during install — the installed copy is for users who want to re-run it later
    /// or for scripts that other game files reference relatively. Default false: scripts are
    /// transient install-time utilities and don't survive into the game folder.
    /// </summary>
    public bool InstallToGameFolder { get; init; }

    /// <summary>
    /// Pre/post-install hooks: when true, the script runs on every install AND every update
    /// of this mod. When false (default), it only runs on a true first install — the update
    /// path skips it. Post-uninstall is unaffected; it always runs on uninstall regardless
    /// of this flag.
    /// </summary>
    public bool RunOnUpdate { get; init; }

    /// <summary>
    /// When true, the manager copies the script to the game folder before running and points
    /// the process at that copy. Use this when the script reads files relative to its own
    /// location (e.g. <c>Assembly.GetExecutingAssembly().Location</c>) — passing
    /// <c>--gameFolder</c> as an arg or setting the working directory doesn't help in that
    /// case, the script's own folder has to be the game folder. After the script runs, the
    /// manager removes the copy unless <see cref="InstallToGameFolder"/> is also true.
    /// </summary>
    public bool RunFromGameFolder { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CopyFileAction), "copyFile")]
[JsonDerivedType(typeof(CopyFolderAction), "copyFolder")]
[JsonDerivedType(typeof(ReplaceFileAction), "replaceFile")]
public abstract class InstallAction;

public sealed class CopyFileAction : InstallAction
{
    public required string Source { get; init; }
    public required string Target { get; init; }
}

public sealed class CopyFolderAction : InstallAction
{
    public required string SourceDir { get; init; }
    public required string TargetDir { get; init; }
}

public sealed class ReplaceFileAction : InstallAction
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public bool Backup { get; init; } = true;
}

public sealed class Dependency
{
    public required string Id { get; init; }
    public required string Type { get; init; } // "system" or "framework"
    public string? MinVersion { get; init; }
    public DependencyCheck? Check { get; init; }
    public DependencyFix? Fix { get; init; }
    public bool Required { get; init; } = true;

    /// <summary>
    /// Optional rule the AuthorTool uses to refresh <see cref="Fix"/>.AutoInstall to the
    /// latest upstream version. Has no effect at runtime — it's purely an authoring hint.
    /// </summary>
    public DependencyVersionDiscovery? VersionDiscovery { get; init; }
}

public sealed class DependencyCheck
{
    public string? RegistryKey { get; init; }
    public string? RegistryValue { get; init; }
    public string? FilePath { get; init; }
}

public sealed class DependencyFix
{
    public string? DownloadUrl { get; init; }
    public string? BundledPath { get; init; }

    /// <summary>
    /// When set, the manager will download + apply this dependency automatically as a step in
    /// the mod install flow instead of relying on the user to follow <see cref="DownloadUrl"/>.
    /// SHA256 verification is mandatory and HTTPS is enforced — same security model as the
    /// mod ZIP itself.
    /// </summary>
    public DependencyAutoInstall? AutoInstall { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ExtractZipAutoInstall), "extractZip")]
[JsonDerivedType(typeof(RunInstallerAutoInstall), "runInstaller")]
[JsonDerivedType(typeof(CopyFileAutoInstall), "copyFile")]
public abstract class DependencyAutoInstall
{
    /// <summary>
    /// Mandatory SHA256 of the downloaded artifact. Hard gate — mismatched downloads abort.
    /// </summary>
    public required string Sha256 { get; init; }
}

public sealed class ExtractZipAutoInstall : DependencyAutoInstall
{
    /// <summary>
    /// Where inside the game folder to extract the ZIP. Empty / null means game root. Path
    /// traversal outside the game folder is rejected at install time.
    /// </summary>
    public string? TargetDir { get; init; }

    /// <summary>
    /// Optional list of entry-path globs to skip when extracting. Useful for stripping
    /// READMEs, examples, etc. that some loader ZIPs include. Matched case-insensitively.
    /// </summary>
    public List<string> Blocklist { get; init; } = [];
}

public sealed class RunInstallerAutoInstall : DependencyAutoInstall
{
    /// <summary>
    /// Optional command-line arguments passed to the installer executable.
    /// </summary>
    public List<string> Args { get; init; } = [];

    /// <summary>
    /// True when the installer requires elevation. Manager spawns it via UAC (Verb=runas) and
    /// surfaces it in the warning dialog. Same semantics as <see cref="LifecycleScript.NeedsAdmin"/>.
    /// </summary>
    public bool NeedsAdmin { get; init; }
}

public sealed class CopyFileAutoInstall : DependencyAutoInstall
{
    /// <summary>
    /// Where inside the game folder to drop the downloaded file. Empty / null means game root.
    /// </summary>
    public string? TargetDir { get; init; }

    /// <summary>
    /// File name to use inside <see cref="TargetDir"/>. If null, the URL's last path segment
    /// is used.
    /// </summary>
    public string? TargetFileName { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GitHubReleaseVersionDiscovery), "githubRelease")]
[JsonDerivedType(typeof(GitHubReleaseAssetVersionDiscovery), "githubReleaseAsset")]
[JsonDerivedType(typeof(StaticVersionDiscovery), "static")]
public abstract class DependencyVersionDiscovery;

/// <summary>
/// "Refresh to latest" walks the GitHub release list for <see cref="Repo"/> and uses the
/// newest non-prerelease tag — useful when the dependency is the release page itself.
/// </summary>
public sealed class GitHubReleaseVersionDiscovery : DependencyVersionDiscovery
{
    public required string Repo { get; init; }
}

/// <summary>
/// Same as <see cref="GitHubReleaseVersionDiscovery"/> but resolves to a specific asset on
/// the release whose filename matches <see cref="AssetGlob"/> — the typical loader case.
/// </summary>
public sealed class GitHubReleaseAssetVersionDiscovery : DependencyVersionDiscovery
{
    public required string Repo { get; init; }
    public required string AssetGlob { get; init; }
}

/// <summary>
/// Author has pinned the URL/SHA manually and doesn't want the AuthorTool's "refresh to
/// latest" button to touch it. Equivalent to omitting VersionDiscovery — included as an
/// explicit kind so the editor can distinguish "intentionally pinned" from "never set".
/// </summary>
public sealed class StaticVersionDiscovery : DependencyVersionDiscovery;

public sealed class VerifyRule
{
    public required string Type { get; init; } // "fileExists", "folderExists", "hashEquals"
    public required string Path { get; init; }
    public string? Sha256 { get; init; }
}
