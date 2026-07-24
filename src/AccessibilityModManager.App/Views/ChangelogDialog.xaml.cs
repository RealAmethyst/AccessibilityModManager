using System.Windows;
using System.Windows.Input;
using AccessibilityModManager.App.Services;

namespace AccessibilityModManager.App.Views;

public partial class ChangelogDialog : Window
{
    private string? _externalUrl;

    public ChangelogDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            DocumentViewer.Focus();
            DocumentViewer.CaretIndex = 0;
        };

        // Block writes to the TextBox while keeping it "editable" for the screen reader so
        // NVDA stays in focus mode and arrow keys move the caret as expected.
        DocumentViewer.PreviewTextInput += (_, e) => e.Handled = true;
        DocumentViewer.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Back or Key.Delete or Key.Enter or Key.Return)
                e.Handled = true;
        };
        DocumentViewer.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Paste,
            (_, e) => e.Handled = true,
            (_, e) => { e.CanExecute = false; e.Handled = true; }));
        DocumentViewer.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Cut,
            (_, e) => e.Handled = true,
            (_, e) => { e.CanExecute = false; e.Handled = true; }));
    }

    public void Show(string modName, string version, string? notes, string? externalUrl)
    {
        Title = $"Changelog — {modName} v{version}";
        HeaderText.Text = $"{modName} v{version}";

        if (!string.IsNullOrWhiteSpace(notes))
        {
            DocumentViewer.Text = notes;
        }
        else if (!string.IsNullOrWhiteSpace(externalUrl))
        {
            DocumentViewer.Text =
                $"No release notes were included in the registry.\n\n" +
                $"The author published the changelog at:\n{externalUrl}\n\n" +
                $"Click 'Open in browser' below to view it.";
        }
        else
        {
            DocumentViewer.Text = "No changelog available for this release.";
        }

        // Only offer the "Open in browser" button for an https changelog URL — the changelog URL
        // is untrusted author metadata, and this button hands it to ShellExecute.
        _externalUrl = ExternalLink.IsAllowed(externalUrl) ? externalUrl : null;
        OpenInBrowserButton.Visibility = _externalUrl is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        ExternalLink.TryOpen(_externalUrl);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
