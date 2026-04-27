using System.Windows;
using AccessibilityModManager.App.ViewModels;

namespace AccessibilityModManager.App.Views;

public partial class DependencyWarningDialog : Window
{
    public bool UserAccepted { get; private set; }

    public DependencyWarningDialog(DependencyWarningDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => CancelButton.Focus();
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
