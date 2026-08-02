using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace AccessibilityModManager.App.Views;

/// <summary>
/// "Land on the first (or previously selected) item, not on the list itself."
///
/// <para>The difference is the whole experience. Focus on a <see cref="ListView"/> gets announced as
/// the list — its name and its type — and the user then has to press a key to hear what is actually
/// in it. Focus on an item announces the item. Everything here exists to make the second one happen
/// reliably.</para>
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

        if (TryFocusContainer(list, index)) return;

        // Containers may not exist yet. Realising them is what makes the difference between
        // announcing a mod and announcing "Mods list", so ask for it rather than hoping: scrolling
        // the item into view forces the virtualising panel to generate it, and UpdateLayout runs
        // that generation now instead of at the next render.
        list.ScrollIntoView(list.Items[index]);
        list.UpdateLayout();

        if (TryFocusContainer(list, index)) return;

        if (list.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
        {
            // Generated, and still no container for this index. Waiting on StatusChanged here would
            // wait forever — it has already fired — so this is the end of the road and focus goes to
            // the list rather than nowhere at all.
            list.Focus();
            return;
        }

        void OnGenerated(object? _, EventArgs __)
        {
            if (list.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated) return;
            list.ItemContainerGenerator.StatusChanged -= OnGenerated;
            if (!TryFocusContainer(list, index)) list.Focus();
        }

        list.ItemContainerGenerator.StatusChanged += OnGenerated;
    }

    private static bool TryFocusContainer(ListView list, int index) =>
        list.ItemContainerGenerator.ContainerFromIndex(index) is ListViewItem container && container.Focus();

    /// <summary>
    /// Puts focus back on an item when the list's contents are REPLACED underneath it.
    ///
    /// <para>A refresh clears the collection and refills it, which destroys the focused item — so
    /// focus falls back to the list, and the next thing the user hears is the list announcing itself
    /// rather than the mod they were on. That happens on startup as a matter of course: the games
    /// list renders, gets focused, and is then rebuilt a moment later when the Patreon membership
    /// load completes and fires its "re-render" notification.</para>
    ///
    /// <para><b>It only ever restores focus the list already had.</b> Moving focus to a list because
    /// its contents changed, while the user is somewhere else entirely, is the rudest thing a
    /// screen-reader application can do — and it would fire on every filter toggle, where focus
    /// belongs on the filter the user just pressed.</para>
    /// </summary>
    public static void RestoreFocusWhenRefilled(ListView list)
    {
        var ownedFocus = false;
        var pending = false;

        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, _) =>
        {
            if (list.Items.Count == 0)
            {
                // Captured while the list is being emptied, not after: clearing it destroys the
                // focused item, and WPF may drop focus outright rather than parking it on the list,
                // so asking afterwards gets the wrong answer.
                ownedFocus |= list.IsKeyboardFocusWithin;
                return;
            }

            if (!ownedFocus && !list.IsKeyboardFocusWithin) return;
            if (pending) return; // one refocus per refill — an ObservableCollection adds one item at
                                 // a time, and focusing repeatedly re-announces each time.

            pending = true;
            _ = list.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    pending = false;
                    ownedFocus = false;
                    if (list.Items.Count > 0) FocusFirstItem(list);
                }),
                DispatcherPriority.Input);
        };
    }
}
