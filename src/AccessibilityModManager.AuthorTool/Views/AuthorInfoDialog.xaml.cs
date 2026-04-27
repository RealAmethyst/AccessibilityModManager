using System.Windows;
using AccessibilityModManager.AuthorTool.ViewModels;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class AuthorInfoDialog : Window
{
    public AuthorInfoDialog(AuthorInfoDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseDialog = () => Close();

        Loaded += (_, _) => DisplayNameBox.Focus();
    }
}
