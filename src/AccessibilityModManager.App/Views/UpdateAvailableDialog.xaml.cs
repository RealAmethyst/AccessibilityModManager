using System.Diagnostics;
using System.Windows;
using AccessibilityModManager.App.ViewModels;

namespace AccessibilityModManager.App.Views;

public partial class UpdateAvailableDialog : Window
{
    private readonly UpdateAvailableDialogViewModel _vm;

    /// <summary>True when the user clicked Install. False on Skip / Cancel / window close.</summary>
    public bool UserChoseInstall { get; private set; }

    public UpdateAvailableDialog(UpdateAvailableDialogViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // Input blocking lives on the TextBox itself now (controls:ReadOnlyText.IsEnabled).
        Loaded += (_, _) => SkipButton.Focus();
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        UserChoseInstall = true;
        Close();
    }

    private void ViewOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _vm.Update.ReleasePageUrl.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch { /* best effort */ }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        UserChoseInstall = false;
        Close();
    }
}
