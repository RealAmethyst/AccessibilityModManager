using System.Windows;
using AccessibilityModManager.App.ViewModels;

namespace AccessibilityModManager.App.Views;

public partial class ScriptWarningDialog : Window
{
    private readonly ScriptWarningDialogViewModel _vm;

    public bool UserAccepted { get; private set; }

    public ScriptWarningDialog(ScriptWarningDialogViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        Loaded += (_, _) =>
        {
            // Cancel by default — user has to make a deliberate choice to run scripts.
            CancelButton.Focus();
        };
    }

    private void Proceed_Click(object sender, RoutedEventArgs e)
    {
        UserAccepted = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        UserAccepted = false;
        Close();
    }
}
