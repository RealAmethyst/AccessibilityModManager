using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AccessibilityModManager.App.Views;

public partial class DeveloperDetailsView : UserControl
{
    public DeveloperDetailsView()
    {
        InitializeComponent();
        // Same focus pattern as GameDetailsView: when the page becomes visible, land on Back
        // so the user can tab forward through the layout.
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => BackButton.Focus()),
                DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>
    /// Move focus to the mods list. Used by MainWindow when returning from Game Details so
    /// the user lands back on the mod they were just looking at.
    /// </summary>
    public void FocusList() => ListFocusHelper.FocusFirstItem(ModsList);
}
