using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AccessibilityModManager.App.Controls;

/// <summary>
/// An ItemsControl that does not announce itself as a list to screen readers. Per-item
/// ContentPresenter wrappers stay as normal control elements in the UIA tree — when they
/// were also marked transparent, NVDA's auto focus-mode switching never fired (it looks at
/// "is the focused element a control?") so it stayed in browse mode and Space went to its
/// page-down handler instead of toggling inner CheckBoxes. Trade-off: NVDA may still
/// announce "data item N of M" near each item, but interaction (Space / Enter / arrow
/// keys) behaves normally.
/// </summary>
public class FlatItemsControl : ItemsControl
{
    public FlatItemsControl()
    {
        // Without these, Tab can land on the wrapper itself — and since IsControlElementCore
        // returns false below, the screen reader announces nothing, which is even worse than
        // saying "list". The wrapper is purely structural: skip it for both Tab navigation
        // and the screen reader, so focus flows directly to the cards' children.
        Focusable = false;
        IsTabStop = false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new OuterTransparentPeer(this);

    /// <summary>
    /// Per-item wrapper whose peer is a control (so NVDA flips into focus mode and Space
    /// reaches the inner CheckBox) but is hidden from the UIA <i>content</i> tree (so NVDA
    /// stops announcing "data item N of M" alongside each item).
    /// </summary>
    protected override DependencyObject GetContainerForItemOverride() => new FlatItemPresenter();

    private sealed class OuterTransparentPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }

    private sealed class FlatItemPresenter : ContentPresenter
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new ItemControlPeer(this);
    }

    private sealed class ItemControlPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => false;
    }
}
