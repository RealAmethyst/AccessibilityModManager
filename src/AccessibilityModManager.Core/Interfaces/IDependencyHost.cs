using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

/// <summary>
/// Manager-supplied callback for the install-time UI around dependency auto-install. The
/// <see cref="IInstallerEngine"/> calls these so the manager can confirm with the user before
/// downloading anything, surface progress per-dep, and pause for manual-only dependencies.
/// Sibling to <see cref="IScriptHost"/> — both are passed to install/update so the engine can
/// drive the deps phase first and then the scripts phase.
/// </summary>
public interface IDependencyHost
{
    /// <summary>
    /// First step of the two-step combined consent (F16=C). Show the user the list of required
    /// deps that will be auto-installed and optional deps they may choose before any download
    /// starts. Skipped when there are no auto-installable dependencies to show.
    /// </summary>
    /// <returns>Whether to proceed and which optional dependencies the user selected.</returns>
    Task<DependencyInstallDecision> ConfirmDependencyInstallAsync(
        DependencyInstallPrompt prompt, CancellationToken ct);

    /// <summary>
    /// A required dependency has no <c>AutoInstall</c> block and the user has to install it
    /// themselves (F8=C). The host opens the download URL in a browser and awaits user
    /// action. Return true to continue the install, false to abort.
    /// </summary>
    Task<bool> AwaitManualDependencyAsync(DependencyManualPrompt prompt, CancellationToken ct);

    /// <summary>Called when a dep auto-install is about to start.</summary>
    void OnDependencyStarting(string dependencyId, string kind, string displayName);

    /// <summary>One line of stdout / stderr from a runInstaller dependency.</summary>
    void OnDependencyOutputLine(string line);

    /// <summary>Called when a dep auto-install has finished. <paramref name="succeeded"/> is true on success.</summary>
    void OnDependencyFinished(string dependencyId, bool succeeded);
}

/// <summary>
/// Bundle the consent dialog renders for the auto-install confirmation.
/// </summary>
public sealed class DependencyInstallPrompt
{
    public required string ModName { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<DependencyInstallPromptItem> Items { get; init; }
}

public sealed class DependencyInstallPromptItem
{
    public required Dependency Dependency { get; init; }

    /// <summary>
    /// Required entries are always installed when the dialog is accepted. Optional entries are
    /// offered unchecked and install only when their id is returned in the decision.
    /// </summary>
    public required bool IsRequired { get; init; }

    /// <summary>"Extract ZIP", "Run installer", or "Copy file" — friendly label for the dialog.</summary>
    public required string KindLabel { get; init; }

    /// <summary>The download URL that will be fetched. Always HTTPS.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>True when the dep needs admin (runInstaller with NeedsAdmin=true).</summary>
    public bool NeedsAdmin { get; init; }
}

/// <summary>
/// Result of the combined dependency prompt. Required dependencies are deliberately absent from
/// the selection set: the engine enforces them independently so a host cannot accidentally omit
/// one. Unknown optional ids are ignored.
/// </summary>
public sealed class DependencyInstallDecision
{
    public bool Accepted { get; init; }

    public IReadOnlySet<string> SelectedOptionalDependencyIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Bundle for the manual-dep pause dialog. The host typically opens the URL in a browser and
/// shows the user a "Continue when done" button.
/// </summary>
public sealed class DependencyManualPrompt
{
    public required string DependencyId { get; init; }
    public required string DownloadUrl { get; init; }
}
