using System.Windows;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.App.Views;

public partial class ChangelogDialog : Window
{
    private string? _externalUrl;

    public ChangelogDialog()
    {
        InitializeComponent();
        // Input blocking lives on the TextBox itself now (controls:ReadOnlyText.IsEnabled),
        // shared with the mod description and the update dialog.
        Loaded += (_, _) =>
        {
            DocumentViewer.Focus();
            DocumentViewer.CaretIndex = 0;
        };
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
