using System.Windows;
using AccessibilityModManager.AuthorTool.ViewModels;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class ReleaseDialog : Window
{
    private readonly ReleaseDialogViewModel _viewModel;

    public ReleaseDialog(ReleaseDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseDialog = () => Close();

        // Land focus on Version (the field most likely to be filled first) so the
        // screen reader speaks the dialog title and then the focused field.
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(viewModel.SourceRepo))
                SourceRepoBox.Focus();
            else
                VersionBox.Focus();
        };
    }

    public ReleaseDialogViewModel ViewModel => _viewModel;
}
