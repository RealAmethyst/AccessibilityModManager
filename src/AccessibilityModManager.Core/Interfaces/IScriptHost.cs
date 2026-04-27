using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

/// <summary>
/// Manager-supplied callback for the install-time UI around lifecycle scripts. The
/// <see cref="IInstallerEngine"/> calls these methods to confirm with the user before running
/// author-supplied scripts and to surface their stdout/stderr stream while running.
/// </summary>
public interface IScriptHost
{
    /// <summary>
    /// Show the user a warning describing every lifecycle script the manifest declares, and
    /// ask permission to proceed with the install. Called once per install, after manifest
    /// parsing but before any backup or file copy. Skipped when no scripts are declared.
    /// </summary>
    /// <returns>True to proceed with the install, false to abort.</returns>
    Task<bool> ConfirmInstallScriptsAsync(LifecycleScriptPrompt prompt, CancellationToken ct);

    /// <summary>
    /// Show the user a warning before the cached post-uninstall script runs. Called once per
    /// uninstall, before file removal. Skipped when no post-uninstall script is cached.
    /// </summary>
    /// <returns>True to run the script, false to skip it (uninstall continues either way).</returns>
    Task<bool> ConfirmUninstallScriptAsync(LifecycleScriptPrompt prompt, CancellationToken ct);

    /// <summary>
    /// Called when a script process is about to start. Manager updates the progress dialog's
    /// label.
    /// </summary>
    void OnScriptStarting(string hookLabel, string scriptName);

    /// <summary>
    /// One line of stdout or stderr emitted by the running script. Called per-line on the
    /// process's I/O threads.
    /// </summary>
    void OnScriptOutputLine(string line);

    /// <summary>
    /// Called when the script process exits. <paramref name="succeeded"/> is
    /// <c>true</c> if exit code was zero.
    /// </summary>
    void OnScriptFinished(int exitCode, bool succeeded);
}

/// <summary>
/// Bundle of metadata the script-host UI uses to render the warning dialog: which mod is
/// being installed, who made it, and which lifecycle hooks are declared. The hook order in
/// <see cref="Hooks"/> matches the order the manager will run them.
/// </summary>
public sealed class LifecycleScriptPrompt
{
    public required string ModName { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public required IReadOnlyList<LifecycleScriptHookInfo> Hooks { get; init; }
}

public sealed class LifecycleScriptHookInfo
{
    /// <summary>"Pre-install", "Post-install", or "Post-uninstall".</summary>
    public required string HookLabel { get; init; }
    public required LifecycleScript Script { get; init; }
}
