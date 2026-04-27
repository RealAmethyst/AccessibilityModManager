using System.Windows;
using AccessibilityModManager.AuthorTool.ViewModels;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class InputDialog : Window
{
    private readonly InputDialogViewModel _viewModel;

    public InputDialog(InputDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseDialog = () => Close();

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void OK_Click(object sender, RoutedEventArgs e) => _viewModel.Confirm();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _viewModel.Cancel();
}
