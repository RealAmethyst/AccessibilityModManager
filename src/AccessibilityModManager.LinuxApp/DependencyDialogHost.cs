using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.LinuxApp;

internal sealed class DependencyDialogHost(Window owner, Action<string> report, ILogger logger) : IDependencyHost
{
    public Task<bool> ConfirmDependencyInstallAsync(DependencyInstallPrompt prompt, CancellationToken ct)
    {
        var details = string.Join(Environment.NewLine,
            prompt.Items.Select(item => $"{item.Dependency.Id}: {item.KindLabel}. Download: {item.DownloadUrl}"));
        return AskAsync("Install required dependencies",
            $"{prompt.ModName}, version {prompt.Version}, needs these files before the mod can install." +
            Environment.NewLine + details + Environment.NewLine +
            "The manager checks each download's SHA-256 before placing it in the game folder.",
            "Install dependencies", ct);
    }

    public async Task<bool> AwaitManualDependencyAsync(DependencyManualPrompt prompt, CancellationToken ct)
    {
        var dialog = CreateDialog("Install a required dependency",
            $"Install {prompt.DependencyId}, then choose Continue. Download page: {prompt.DownloadUrl}",
            "Continue", out var message, out var buttons);
        var open = new Button { Content = "Open download page" };
        AutomationProperties.SetName(open, "Open dependency download page");
        open.Click += (_, _) =>
        {
            if (!ExternalLink.TryOpen(prompt.DownloadUrl, logger))
            {
                message.Text = "The download page could not be opened. Use the URL shown above.";
                AutomationProperties.SetName(message, message.Text);
                message.Focus();
            }
        };
        buttons.Children.Insert(0, open);
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await dialog.ShowDialog<bool>(owner);
    }

    public void OnDependencyStarting(string dependencyId, string kind, string displayName) =>
        Dispatcher.UIThread.Post(() => report($"Installing {displayName} ({kind})."));

    public void OnDependencyOutputLine(string line) => logger.Information("Dependency installer: {Line}", line);

    public void OnDependencyFinished(string dependencyId, bool succeeded) =>
        Dispatcher.UIThread.Post(() => report(succeeded
            ? $"Installed dependency {dependencyId}."
            : $"Dependency {dependencyId} could not be installed."));

    private async Task<bool> AskAsync(string title, string text, string acceptLabel, CancellationToken ct)
    {
        var dialog = CreateDialog(title, text, acceptLabel, out _, out _);
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await dialog.ShowDialog<bool>(owner);
    }

    private static Window CreateDialog(string title, string text, string acceptLabel,
        out TextBlock message, out StackPanel buttons)
    {
        var dialog = new Window
        {
            Title = title, Width = 640, MinWidth = 420, Height = 330, MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var focusMessage = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Focusable = true };
        message = focusMessage;
        AutomationProperties.SetName(focusMessage, text);
        var accept = new Button { Content = acceptLabel };
        var cancel = new Button { Content = "Cancel" };
        accept.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);
        var content = new StackPanel { Spacing = 16, Margin = new Thickness(20) };
        content.Children.Add(focusMessage);
        content.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = content };
        dialog.Opened += (_, _) => focusMessage.Focus();
        return dialog;
    }
}
