using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.LinuxApp;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
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
        var dialog = new ManagerUpdateDialog(checker, info, () => restart = true);
        dialog.Show();
        try
        {
            var notes = dialog.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.True(notes.IsFocused);
            Assert.True(notes.IsReadOnly);
            Assert.Contains("Useful release notes", notes.Text);
            var cancel = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Not now");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(dialog.IsVisible);
            Assert.False(restart);
        }
        finally { dialog.Close(); }
    }
}
