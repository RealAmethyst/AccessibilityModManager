using System.Text;
using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccessibilityModManager.App.ViewModels;

public partial class ProgressDialogViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private readonly StringBuilder _outputBuffer = new();

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private string _stepDescription = string.Empty;

    [ObservableProperty]
    private bool _isCancellable = true;

    /// <summary>
    /// Visible only while a lifecycle script is running. Holds combined stdout + stderr
    /// captured by <see cref="LifecycleScriptRunner"/>. The view scrolls this to the bottom on
    /// every update so the user always sees the latest line.
    /// </summary>
    [ObservableProperty]
    private string _scriptOutput = string.Empty;

    /// <summary>
    /// True while a script is executing. Drives the visibility of the script-output panel; we
    /// keep it hidden during normal download/install steps so the dialog stays compact.
    /// </summary>
    [ObservableProperty]
    private bool _isScriptRunning;

    /// <summary>
    /// "Running pre-install script: foo.ps1" — set on <see cref="OnScriptStarting"/> and cleared
    /// on <see cref="OnScriptFinished"/>. Bound to a heading TextBlock above the output area.
    /// </summary>
    [ObservableProperty]
    private string _scriptStatusHeader = string.Empty;

    public void Start(string title, string message, CancellationTokenSource cts)
    {
        Title = title;
        Message = message;
        Percentage = 0;
        StepDescription = string.Empty;
        _cts = cts;
        IsCancellable = true;
        IsScriptRunning = false;
        ScriptOutput = string.Empty;
        ScriptStatusHeader = string.Empty;
        _outputBuffer.Clear();
    }

    public void OnProgress(ProgressInfo info)
    {
        Percentage = info.Percentage;
        Message = info.StatusText;
        StepDescription = info.StepDescription ?? string.Empty;
    }

    /// <summary>
    /// Called by <see cref="DialogScriptHost"/> just before a lifecycle script starts. Switches
    /// the dialog into "script running" mode, clears any previous output, and announces the new
    /// script's name + hook label.
    /// </summary>
    public void OnScriptStarting(string hookLabel, string scriptName)
    {
        _outputBuffer.Clear();
        ScriptOutput = string.Empty;
        ScriptStatusHeader = $"Running {hookLabel.ToLowerInvariant()} script: {scriptName}";
        Message = ScriptStatusHeader;
        // Starting a script IS a phase change, and StepDescription is the dialog's only spoken
        // line — without this the switch from downloading to running a script would be silent.
        StepDescription = ScriptStatusHeader;
        IsScriptRunning = true;
    }

    /// <summary>
    /// Appends a single line of stdout/stderr from the running script. The output area is bound
    /// to <see cref="ScriptOutput"/>; the view will scroll to the bottom whenever it changes.
    /// </summary>
    public void OnScriptOutputLine(string line)
    {
        _outputBuffer.AppendLine(line);
        ScriptOutput = _outputBuffer.ToString();
    }

    /// <summary>
    /// Marks the script run finished. Leaves the output visible so the user can read the tail
    /// before the dialog closes (the install flow may proceed with more steps after this).
    /// </summary>
    public void OnScriptFinished(int exitCode, bool succeeded)
    {
        var status = succeeded ? "completed" : $"failed (exit code {exitCode})";
        ScriptStatusHeader = $"{ScriptStatusHeader} — {status}";
        StepDescription = ScriptStatusHeader;
        // Keep IsScriptRunning true so the output stays visible until the dialog closes.
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        IsCancellable = false;
        Message = "Cancelling...";
        StepDescription = "Cancelling";
    }
}
