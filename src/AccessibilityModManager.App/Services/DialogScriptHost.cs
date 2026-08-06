using System.Windows;
using System.Windows.Threading;
using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.App.Views;
using AccessibilityModManager.Core.Interfaces;

namespace AccessibilityModManager.App.Services;

/// <summary>
/// WPF implementation of both <see cref="IScriptHost"/> and <see cref="IDependencyHost"/>. The
/// engine's install flow is two-phase (deps first, scripts second per F16=C), and a single
/// host instance covers both phases — the manual-pause prompt and the dep consent share the
/// same owner-window provider, and dep + script output both stream into the same
/// <see cref="ProgressDialog"/>. UI work is dispatched onto the UI thread; the installer
/// engine runs background tasks on worker threads.
/// </summary>
public sealed class DialogScriptHost : IScriptHost, IDependencyHost
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<Window?> _ownerProvider;
    private readonly ProgressDialogViewModel _progressVm;

    public DialogScriptHost(Dispatcher dispatcher, Func<Window?> ownerProvider, ProgressDialogViewModel progressVm)
    {
        _dispatcher = dispatcher;
        _ownerProvider = ownerProvider;
        _progressVm = progressVm;
    }

    // -------- IScriptHost --------

    public Task<bool> ConfirmInstallScriptsAsync(LifecycleScriptPrompt prompt, CancellationToken ct)
        => ShowScriptWarningAsync(prompt, isUninstall: false, ct);

    public Task<bool> ConfirmUninstallScriptAsync(LifecycleScriptPrompt prompt, CancellationToken ct)
        => ShowScriptWarningAsync(prompt, isUninstall: true, ct);

    private Task<bool> ShowScriptWarningAsync(LifecycleScriptPrompt prompt, bool isUninstall, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(false);

        var op = _dispatcher.InvokeAsync(() =>
        {
            var vm = new ScriptWarningDialogViewModel(prompt, isUninstall);
            var dialog = new ScriptWarningDialog(vm) { Owner = _ownerProvider() };
            dialog.ShowDialog();
            return dialog.UserAccepted;
        });
        return op.Task;
    }

    public void OnScriptStarting(string hookLabel, string scriptName) =>
        _dispatcher.Invoke(() => _progressVm.OnScriptStarting(hookLabel, scriptName));

    public void OnScriptOutputLine(string line) =>
        _dispatcher.Invoke(() => _progressVm.OnScriptOutputLine(line));

    public void OnScriptFinished(int exitCode, bool succeeded) =>
        _dispatcher.Invoke(() => _progressVm.OnScriptFinished(exitCode, succeeded));

    // -------- IDependencyHost --------

    public Task<DependencyInstallDecision> ConfirmDependencyInstallAsync(
        DependencyInstallPrompt prompt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(new DependencyInstallDecision { Accepted = false });

        var op = _dispatcher.InvokeAsync(() =>
        {
            var vm = new DependencyWarningDialogViewModel(prompt);
            var dialog = new DependencyWarningDialog(vm) { Owner = _ownerProvider() };
            dialog.ShowDialog();
            return dialog.Decision;
        });
        return op.Task;
    }

    public Task<bool> AwaitManualDependencyAsync(DependencyManualPrompt prompt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(false);

        var op = _dispatcher.InvokeAsync(() =>
        {
            var dialog = new ManualDependencyDialog(prompt.DependencyId, prompt.DownloadUrl)
            {
                Owner = _ownerProvider()
            };
            dialog.ShowDialog();
            return dialog.UserContinued;
        });
        return op.Task;
    }

    public void OnDependencyStarting(string dependencyId, string kind, string displayName) =>
        _dispatcher.Invoke(() => _progressVm.OnScriptStarting($"Dep ({kind})", displayName));

    public void OnDependencyOutputLine(string line) =>
        _dispatcher.Invoke(() => _progressVm.OnScriptOutputLine(line));

    public void OnDependencyFinished(string dependencyId, bool succeeded) =>
        _dispatcher.Invoke(() => _progressVm.OnScriptFinished(succeeded ? 0 : 1, succeeded));
}
