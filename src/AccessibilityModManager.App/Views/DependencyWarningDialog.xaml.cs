using System.Windows;
using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.Core.Interfaces;

namespace AccessibilityModManager.App.Views;

public partial class DependencyWarningDialog : Window
{
    private readonly DependencyWarningDialogViewModel _viewModel;

    public DependencyInstallDecision Decision { get; private set; } = new();

    public DependencyWarningDialog(DependencyWarningDialogViewModel vm)
    {
        InitializeComponent();
        _viewModel = vm;
        DataContext = vm;
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Proceed_Click(object sender, RoutedEventArgs e)
    {
        Decision = new DependencyInstallDecision
        {
            Accepted = true,
            SelectedOptionalDependencyIds = new HashSet<string>(
                _viewModel.SelectedOptionalDependencyIds, StringComparer.OrdinalIgnoreCase)
        };
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Decision = new DependencyInstallDecision { Accepted = false };
        Close();
    }
}
