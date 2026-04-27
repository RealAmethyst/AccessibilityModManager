using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AccessibilityModManager.AuthorTool.Controls;

/// <summary>
/// Mirror of the manager's FlatItemsControl. Hides the OUTER "list" announcement so screen
/// readers don't say "list" before reading each focused control inside, but keeps the
/// per-item ContentPresenter wrappers as normal control elements in the UIA tree. That
/// matters because NVDA's auto focus-mode switching looks at "is the focused element a
/// control?" — when the wrappers reported IsControlElement=false, NVDA stayed in browse
/// mode and Space went to its page-down handler instead of toggling the inner CheckBox.
/// Trade-off: NVDA will still announce "data item N of M" alongside each item, but Space /
/// Enter / arrow keys behave normally.
/// </summary>
public class FlatItemsControl : ItemsControl
{
    public FlatItemsControl()
    {
        Focusable = false;
        IsTabStop = false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new OuterTransparentPeer(this);

    /// <summary>
    /// Per-item wrapper whose peer is a control (so NVDA flips into focus mode and Space
    /// reaches the inner CheckBox) but is hidden from the UIA <i>content</i> tree (so NVDA
    /// stops announcing "data item N of M" alongside each item). The previous fully
    /// transparent variant put NVDA in browse mode and broke Space; the previous default
    /// wrapper announced verbose position info. This is the middle ground that satisfies
    /// both.
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
        // Keep the wrapper visible to UIA as a control so NVDA's auto focus-mode switch
        // still fires for the inner CheckBox / Button / etc. — without this, Space goes to
        // NVDA's browse-mode handler instead of toggling the control.
        protected override bool IsControlElementCore() => true;

        // ...but hide it from the content tree, which is what NVDA enumerates for the
        // "data item N of M" position announcement.
        protected override bool IsContentElementCore() => false;
    }
}
