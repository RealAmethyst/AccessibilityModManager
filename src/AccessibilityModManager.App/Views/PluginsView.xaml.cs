using System.Windows.Controls;
using System.Windows.Controls.Primitives;

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

/// <summary>
/// Shared helper for "focus the first/selected item, or fall back to the list container."
/// Keeps the focus behavior consistent across the developers, games, and mod-releases lists.
/// </summary>
internal static class ListFocusHelper
{
    public static void FocusFirstItem(ListView list)
    {
        if (list.Items.Count == 0)
        {
            list.Focus();
            return;
        }

        var index = list.SelectedIndex >= 0 ? list.SelectedIndex : 0;
        list.SelectedIndex = index;

        if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListViewItem container)
        {
            container.Focus();
        }
        else
        {
            // Containers haven't materialized yet — wait for them and retry once.
            void OnGenerated(object? _, System.EventArgs __)
            {
                if (list.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
                    return;
                list.ItemContainerGenerator.StatusChanged -= OnGenerated;
                if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListViewItem c)
                    c.Focus();
                else
                    list.Focus();
            }
            list.ItemContainerGenerator.StatusChanged += OnGenerated;
        }
    }
}
