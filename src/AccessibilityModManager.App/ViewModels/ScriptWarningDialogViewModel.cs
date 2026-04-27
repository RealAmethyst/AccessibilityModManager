using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessibilityModManager.App.ViewModels;

/// <summary>
/// View-model for <see cref="Views.ScriptWarningDialog"/>. Renders the manifest's lifecycle
/// scripts as a list of clearly-described items so the user knows what they're agreeing to
/// before any code runs. Used at install (<see cref="IScriptHost.ConfirmInstallScriptsAsync"/>)
/// and at uninstall (<see cref="IScriptHost.ConfirmUninstallScriptAsync"/>).
/// </summary>
public sealed class ScriptWarningDialogViewModel : ObservableObject
{
    public string Headline { get; }
    public string Subheading { get; }
    public string ProceedButtonText { get; }
    public IReadOnlyList<ScriptHookViewModel> Hooks { get; }

    public ScriptWarningDialogViewModel(LifecycleScriptPrompt prompt, bool isUninstall)
    {
        var modLabel = string.IsNullOrWhiteSpace(prompt.Author) || prompt.Author == prompt.ModName
            ? $"{prompt.ModName} v{prompt.Version}"
            : $"{prompt.ModName} v{prompt.Version} by {prompt.Author}";

        Headline = isUninstall
            ? $"Uninstall {modLabel} — author wants to run a script."
            : $"Install {modLabel} — author included scripts.";

        Subheading = isUninstall
            ? "The mod's author packaged a post-uninstall script. The manager will only run it if you click Run."
            : "Lifecycle scripts run code on your machine. Read each item below carefully. The manager will only run them if you click Install.";

        ProceedButtonText = isUninstall ? "Run script and uninstall" : "Install with scripts";

        Hooks = prompt.Hooks.Select(h => new ScriptHookViewModel(h)).ToList();
    }
}

public sealed class ScriptHookViewModel
{
    private readonly LifecycleScriptHookInfo _info;

    public ScriptHookViewModel(LifecycleScriptHookInfo info)
    {
        _info = info;
    }

    public string HookHeading => _info.HookLabel;
    public string ExecutableLine => $"Runs: {_info.Script.Executable}";
    public string WhatLine => $"What it does: {_info.Script.What}";
    public string WhyLine => $"Why it's needed: {_info.Script.Why}";
    public string ModifiesLine => $"What it modifies: {_info.Script.Modifies}";

    public string FailureLine => _info.Script.FailureFatal
        ? "If this script fails the install is rolled back."
        : "If this script fails the install continues; the failure is logged.";

    public bool NeedsAdmin => _info.Script.NeedsAdmin;
    public Visibility NeedsAdminVisibility => NeedsAdmin ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Single string the screen reader reads when this row gets focus. We prepend the hook
    /// label and admin warning so the listener knows immediately whether the script is risky.
    /// </summary>
    public string AnnouncementText
    {
        get
        {
            var admin = NeedsAdmin ? " (needs administrator)" : "";
            return $"{HookHeading}{admin}. {ExecutableLine}. {WhatLine}. {WhyLine}. {ModifiesLine}. {FailureLine}";
        }
    }

    public override string ToString() => AnnouncementText;
}
