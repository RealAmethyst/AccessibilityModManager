using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.LinuxApp;

internal sealed class ManagerUpdateDialog : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private bool applying;

    public ManagerUpdateDialog(UpdateChecker checker, UpdateInfo update, Action closeManager)
    {
        Title = "Manager update available";
        Width = 640;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        var notes = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = $"Version {update.Version} is available. Install it and restart the manager? Your settings and installed mods will be kept.\n\n" +
                (update.ReleaseNotes ?? "No release notes were supplied.") };
        AutomationProperties.SetName(notes, "Update details and release notes");
        root.Children.Add(notes);
        var status = new TextBlock { Focusable = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 12) };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        Grid.SetRow(status, 1);
        root.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var install = new Button { Content = "Install update and restart" };
        var cancel = new Button { Content = "Not now" };
        cancel.Click += (_, _) => Close();
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            cancel.Content = "Cancel download";
            string? archive = null;
            var handedOff = false;
            try
            {
                if (Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) !=
                    LinuxApplicationInstaller.InstallDirectory(LinuxApplication.Manager))
                    throw new InvalidOperationException("Run install.sh from your downloaded package first, then open the manager from the application menu to use in-app updates.");
                status.Text = "Downloading update…";
                status.Focus();
                archive = await checker.DownloadAsync(update, null, cancellation.Token);
                status.Text = "Verifying and preparing update…";
                var package = await LinuxApplicationInstaller.ExtractUpdateAsync(archive, update, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo(Path.Combine(package, LinuxApplication.Manager.Executable))
                {
                    UseShellExecute = false,
                    ArgumentList = { "--apply-update", Environment.ProcessId.ToString() }
                };
                _ = Process.Start(start) ?? throw new IOException("Could not start the update installer.");
                handedOff = true;
                applying = true;
                Close();
                closeManager();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                status.Text = "Update not installed. " + ex.Message;
                status.Focus();
                cancel.Content = "Close";
            }
            finally
            {
                if (!handedOff && archive is not null)
                {
                    try { Directory.Delete(Path.GetDirectoryName(archive)!, true); }
                    catch (IOException) { }
                }
            }
        };
        buttons.Children.Add(install);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
        Opened += (_, _) => notes.Focus();
        Closing += (_, _) => { if (!applying) cancellation.Cancel(); };
    }
}
