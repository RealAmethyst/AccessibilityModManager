using System.Windows;
using AccessibilityModManager.AuthorTool.ViewModels;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class BuildPackageDialog : Window
{
    public BuildPackageDialog(BuildPackageDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseDialog = () => Close();

        Loaded += (_, _) => SourceFolderBox.Focus();
    }
}
