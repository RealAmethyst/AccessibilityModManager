namespace AccessibilityModManager.Core.Models;

public enum ChangeType
{
    Added,
    Replaced,
    Patched
}

/// <summary>
/// Tracks a file that was changed during install, for rollback/uninstall.
/// </summary>
public sealed class FileChange
{
    public required ChangeType Type { get; init; }
    public required string RelativePath { get; init; }
    public string? BackupRelativePath { get; init; }
}

/// <summary>
/// A receipt recording exactly what was installed, by which plugin, for which game.
/// Used for uninstall and rollback.
/// </summary>
public sealed class InstallReceipt
{
    public required string GameId { get; init; }
    public required string PluginId { get; init; }
    public required string InstalledVersion { get; init; }
    public required DateTime InstalledAt { get; init; }
    public required List<FileChange> Changes { get; init; }
    public required string BackupFolder { get; init; }
    public required string ManifestHash { get; init; }

    /// <summary>
    /// If the installed manifest declared a post-uninstall script, the absolute path to the
    /// cached executable (copied during install per F4=A). Null if no post-uninstall hook
    /// was set. The manager runs this on uninstall, then deletes it from disk.
    /// </summary>
    public string? CachedPostUninstallExecutable { get; init; }

    /// <summary>
    /// SHA256 of the cached post-uninstall executable at install time. Verified immediately before
    /// the script runs at uninstall; if the cached file was swapped on disk the hash won't match and
    /// the script is refused (the consent dialog described the original, not a replacement). Null for
    /// receipts written before this field existed (back-compat) — those skip the check.
    /// </summary>
    public string? CachedPostUninstallSha256 { get; init; }

    /// <summary>
    /// The PostUninstall script metadata captured at install time. Used to re-display the
    /// What/Why/Modifies fields in the uninstall warning dialog and to determine
    /// FailureFatal / NeedsAdmin.
    /// </summary>
    public LifecycleScript? PostUninstall { get; init; }

    /// <summary>
    /// A hash of the lifecycle scripts the user consented to at install (their bytes + flags), or
    /// null if the mod declared no scripts. On update, the manager re-shows the script warning when
    /// this differs from the new version's scripts — i.e. a script was added or changed since the
    /// user last agreed. Null on receipts written before this field existed (they just re-warn once).
    /// </summary>
    public string? ScriptsFingerprint { get; init; }

    /// <summary>
    /// True once the cached post-uninstall script was handled (run, or consent declined) during an
    /// uninstall of this receipt. Persisted so a retry after a downstream failure — a locked file
    /// blocking rollback, a dependency-release problem — doesn't run the author's cleanup script
    /// and its side effects a second time.
    /// </summary>
    public bool PostUninstallScriptRan { get; set; }
}

/// <summary>
/// Outcome of a rollback: which changes could NOT be undone. An empty list means every change was
/// verified restored or removed. Callers must not delete receipts or backups unless
/// <see cref="AllRestored"/> is true — a swallowed restore failure followed by receipt deletion
/// leaves the game modified with the repair evidence gone.
/// </summary>
public sealed record RollbackReport(List<string> FailedPaths)
{
    public bool AllRestored => FailedPaths.Count == 0;
    public static RollbackReport Clean { get; } = new(new List<string>());
}

/// <summary>
/// Records what a single dependency auto-install added to / replaced in the game folder.
/// Scoped per (gameId, dependencyId) — multiple plugins requiring the same dep share one
/// receipt, with <see cref="DependentPluginIds"/> acting as a refcount so the dep stays
/// installed as long as at least one plugin still needs it (F10=C).
/// </summary>
public sealed class DependencyReceipt
{
    public required string GameId { get; init; }
    public required string DependencyId { get; init; }

    /// <summary>
    /// "extractZip", "runInstaller", or "copyFile" — captured at install time so a future
    /// uninstall knows what to undo. RunInstaller writes no FileChange entries (the installer
    /// owns its own files) and is recorded purely for audit.
    /// </summary>
    public required string Kind { get; init; }

    public required DateTime InstalledAt { get; init; }
    public required string Sha256 { get; init; }

    /// <summary>
    /// Files the auto-install added or replaced, mirroring <see cref="InstallReceipt.Changes"/>.
    /// Empty for runInstaller.
    /// </summary>
    public required List<FileChange> Changes { get; init; }

    /// <summary>
    /// Per-dep backup folder under the receipt directory. Same restore semantics as the mod
    /// install backup.
    /// </summary>
    public required string BackupFolder { get; init; }

    /// <summary>
    /// Plugin ids that have currently-installed mods depending on this dep. The list grows
    /// when a mod that needs this dep is installed, and shrinks on uninstall. When it hits
    /// empty, the dep can safely be removed (currently never auto-removed in v1).
    /// </summary>
    public required List<string> DependentPluginIds { get; init; }
}
