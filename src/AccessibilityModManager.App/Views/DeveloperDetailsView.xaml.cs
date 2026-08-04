using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AccessibilityModManager.App.Views;

public partial class DeveloperDetailsView : UserControl
{
    public DeveloperDetailsView()
    {
        InitializeComponent();

        // Focus is NOT taken here on becoming visible. Two different transitions make this page
        // visible — opening it, and returning to it from a mod — and they want focus in different
        // places, so MainWindow drives both explicitly. Self-focusing on visibility raced the
        // reshow path and the winner depended on dispatcher ordering.

        // The mod list is rebuilt by the post-install refresh, which clears the collection and
        // destroys the focused row. Same helper, and same reason, as the Mods tab.
        ListFocusHelper.RestoreFocusWhenRefilled(ModsList);
    }

    /// <summary>
    /// Land on Back, so the user can tab forward through bio, mods and links. Used when the page
    /// is first opened.
    /// </summary>
    public void FocusBack() =>
        _ = Dispatcher.BeginInvoke(
            new Action(() => BackButton.Focus()),
            DispatcherPriority.ApplicationIdle);

    /// <summary>
    /// Move focus to the mods list. Used by MainWindow when returning from Game Details so
    /// the user lands back on the mod they were just looking at.
    /// </summary>
    public void FocusList() => ListFocusHelper.FocusFirstItem(ModsList);
}
