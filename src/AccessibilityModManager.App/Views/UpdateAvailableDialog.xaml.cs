using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
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

        // Block writes to the changelog TextBox while keeping it editable so NVDA stays in
        // focus mode and arrow keys move the caret. Same trick the mod ChangelogDialog uses.
        ChangelogBox.PreviewTextInput += (_, e) => e.Handled = true;
        ChangelogBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Back or Key.Delete or Key.Enter or Key.Return)
                e.Handled = true;
        };
        ChangelogBox.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Paste,
            (_, e) => e.Handled = true,
            (_, e) => { e.CanExecute = false; e.Handled = true; }));
        ChangelogBox.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Cut,
            (_, e) => e.Handled = true,
            (_, e) => { e.CanExecute = false; e.Handled = true; }));

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
