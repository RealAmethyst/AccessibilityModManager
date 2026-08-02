using System.Windows.Controls;

namespace AccessibilityModManager.App.Views;

public partial class GamesListView : UserControl
{
    public GamesListView()
    {
        InitializeComponent();

        // The mods list is rebuilt more than once during an ordinary startup: it renders and gets
        // focused, and is then cleared and refilled when the Patreon membership load finishes and
        // asks every view to re-render. That destroys the item the user was on, so focus falls back
        // to the list and the next thing they hear is the list announcing itself instead of a mod.
        ListFocusHelper.RestoreFocusWhenRefilled(GamesList);
    }

    /// <summary>
    /// Focus the games list, landing on the previously-selected game (or the first one if none
    /// is selected). Used when navigating back from the Game Details view so the user returns
    /// exactly where they were.
    /// </summary>
    public void FocusList() => ListFocusHelper.FocusFirstItem(GamesList);
}
