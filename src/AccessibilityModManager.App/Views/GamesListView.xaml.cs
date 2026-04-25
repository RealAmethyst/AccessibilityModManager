using System.Windows.Controls;

namespace AccessibilityModManager.App.Views;

public partial class GamesListView : UserControl
{
    public GamesListView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Focus the games list, landing on the previously-selected game (or the first one if none
    /// is selected). Used when navigating back from the Game Details view so the user returns
    /// exactly where they were.
    /// </summary>
    public void FocusList() => ListFocusHelper.FocusFirstItem(GamesList);
}
