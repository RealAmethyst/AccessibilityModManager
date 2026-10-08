using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.LinuxApp;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
using Xunit;

namespace AccessibilityModManager.LinuxApp.Tests;

public sealed class UpdateDialogTests
{
    [AvaloniaFact]
    public void UpdateOfferFocusesReadableNotesAndDecliningDoesNotRestart()
    {
        using var http = new HttpClient();
        var checker = new UpdateChecker(http, new LoggerConfiguration().CreateLogger(), "linux-x64");
        var info = new UpdateInfo(new Version(1, 20, 0), "v1.20.0", "Manager", "Useful release notes",
            new Uri("https://example.org/update"), "", null, new Uri("https://example.org"), "linux-x64");
        var restart = false;
        var previousFocus = new Button { Content = "Check for manager updates" };
        var owner = new Window { Content = previousFocus };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        previousFocus.Focus();
        var dialog = new ManagerUpdateDialog(checker, info, () => restart = true);
        var closed = dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        try
        {
            var notes = dialog.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.True(notes.IsFocused);
            var root = Assert.IsAssignableFrom<WindowAutomationPeer>(ControlAutomationPeer.CreatePeerForElement(dialog));
            Assert.Same(ControlAutomationPeer.CreatePeerForElement(notes), root.GetFocus());
            Assert.True(notes.IsReadOnly);
            Assert.Contains("Useful release notes", notes.Text);
            var cancel = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Not now");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.IsVisible);
            Assert.True(closed.IsCompleted);
            Assert.True(previousFocus.IsFocused);
            Assert.False(restart);
        }
        finally { dialog.Close(); owner.Close(); }
    }
}
