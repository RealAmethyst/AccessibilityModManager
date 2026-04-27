using System.Windows;
using System.Windows.Controls;
using AccessibilityModManager.AuthorTool.ViewModels;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class GameEditorView : UserControl
{
    public GameEditorView()
    {
        InitializeComponent();
    }

    private void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GameItemViewModel game) return;
        if (PresetCombo.SelectedItem is not DependencyPreset preset)
        {
            MessageBox.Show("Pick a preset from the dropdown first.", "No preset selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        game.AddDependencyFromPreset(preset);
    }

    private void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GameItemViewModel game) return;
        game.AddCustomDependency();
    }
}
