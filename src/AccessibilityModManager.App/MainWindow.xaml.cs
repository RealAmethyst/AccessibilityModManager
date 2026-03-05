using System.Windows;
using AccessibilityModManager.App.ViewModels;

namespace AccessibilityModManager.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
