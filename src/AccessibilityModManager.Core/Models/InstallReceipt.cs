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
    /// The PostUninstall script metadata captured at install time. Used to re-display the
    /// What/Why/Modifies fields in the uninstall warning dialog and to determine
    /// FailureFatal / NeedsAdmin.
    /// </summary>
    public LifecycleScript? PostUninstall { get; init; }
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
