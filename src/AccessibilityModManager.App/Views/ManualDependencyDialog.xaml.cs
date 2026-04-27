using System.Diagnostics;
using System.Windows;

namespace AccessibilityModManager.App.Views;

public partial class ManualDependencyDialog : Window
{
    private readonly string _url;

    public bool UserContinued { get; private set; }

    public ManualDependencyDialog(string dependencyId, string downloadUrl)
    {
        InitializeComponent();
        _url = downloadUrl;
        HeadingText.Text = $"Install {dependencyId} manually to continue";
        UrlBox.Text = downloadUrl;
        Loaded += (_, _) =>
        {
            // Open the download page automatically per F8=C — the user can re-open via the
            // button if their browser was closed.
            OpenInBrowser();
            CancelButton.Focus();
        };
    }

    private void OpenUrl_Click(object sender, RoutedEventArgs e) => OpenInBrowser();

    private void OpenInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _url,
                UseShellExecute = true
            });
        }
        catch { /* user can copy the URL out of the box if launch fails */ }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        UserContinued = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        UserContinued = false;
        Close();
    }
}
