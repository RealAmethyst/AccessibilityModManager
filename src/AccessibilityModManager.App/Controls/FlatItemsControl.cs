using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AccessibilityModManager.App.Controls;

/// <summary>
/// An ItemsControl that does not announce itself as a list to screen readers. The default
/// ItemsControlAutomationPeer reports its automation role as <c>List</c>, so even with one
/// item NVDA says "list" before reading the contents — confusing in places like a single-mod
/// card on the Game Details page. We replace the items-aware peer with a plain
/// FrameworkElementAutomationPeer that marks itself non-control, so the wrapper is skipped
/// from the screen reader's control view. Children stay reachable via tab and keyboard
/// navigation as usual.
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

    protected override AutomationPeer OnCreateAutomationPeer() => new TransparentPeer(this);

    private sealed class TransparentPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;
    }
}
