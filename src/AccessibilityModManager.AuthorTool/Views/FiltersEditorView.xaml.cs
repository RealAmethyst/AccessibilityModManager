using System.Windows;
using System.Windows.Controls;
using AccessibilityModManager.AuthorTool.ViewModels;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class FiltersEditorView : UserControl
{
    public FiltersEditorView()
    {
        InitializeComponent();
    }

    private void AddCustomTag_Click(object sender, RoutedEventArgs e)
    {
        var name = CustomTagBox.Text;
        if (string.IsNullOrWhiteSpace(name)) return;

        switch (DataContext)
        {
            case GameItemViewModel game:
                game.AddCustomTag(name);
                break;
            case AddGameDialogViewModel addDialog:
                addDialog.AddCustomTag(name);
                break;
        }

        CustomTagBox.Text = string.Empty;
        CustomTagBox.Focus();
    }

    private void RemoveCustomTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.DataContext is not TagSelection tag) return;

        switch (DataContext)
        {
            case GameItemViewModel game:
                game.RemoveCustomTag(tag);
                break;
            case AddGameDialogViewModel addDialog:
                addDialog.RemoveCustomTag(tag);
                break;
        }
    }
}
