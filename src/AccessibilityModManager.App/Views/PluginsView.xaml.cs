using System.Windows.Controls;

namespace AccessibilityModManager.App.Views;

public partial class PluginsView : UserControl
{
    public PluginsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Move keyboard focus into the developers list, landing directly on the first item so the
    /// screen reader announces it instead of just saying "Developers list". Falls back to the
    /// list container when items aren't generated yet.
    /// </summary>
    public void FocusList() => ListFocusHelper.FocusFirstItem(DevelopersList);
}
