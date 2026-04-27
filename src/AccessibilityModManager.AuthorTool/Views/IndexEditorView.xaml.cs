using System.Windows.Controls;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class IndexEditorView : UserControl
{
    public IndexEditorView()
    {
        InitializeComponent();
        // The Games list is the primary navigation surface; landing focus there on open
        // means the screen reader announces "<game name>, list" rather than the title bar
        // or whichever control WPF picked by default.
        Loaded += (_, _) => GamesList.Focus();
    }
}
