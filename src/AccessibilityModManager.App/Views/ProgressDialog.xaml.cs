using System.Windows;
using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.App.Views;

public partial class ProgressDialog : Window
{
    private readonly ProgressDialogViewModel _viewModel;

    public ProgressDialog(ProgressDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public ProgressDialogViewModel ViewModel => _viewModel;

    public void Start(string title, string message, CancellationTokenSource cts, IProgress<ProgressInfo> progress)
    {
        _viewModel.Start(title, message, cts);

        if (progress is Progress<ProgressInfo> typedProgress)
        {
            typedProgress.ProgressChanged += (_, info) =>
            {
                Dispatcher.Invoke(() => _viewModel.OnProgress(info));
            };
        }
    }

    private void OutputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Keep the latest line visible — script output grows from the bottom.
        OutputBox.ScrollToEnd();
    }
}
