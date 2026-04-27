using System.Windows;
using AccessibilityModManager.AuthorTool.ViewModels;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class AddGameDialog : Window
{
    public AddGameDialog(AddGameDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseDialog = () => Close();

        Loaded += (_, _) => DisplayNameBox.Focus();
    }
}
