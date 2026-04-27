using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AccessibilityModManager.AuthorTool.Controls;

/// <summary>
/// A ComboBox whose own AutomationPeer is hidden from the screen reader. Used for
/// editable ComboBoxes where the outer ComboBox + inner edit TextBox both announce
/// their AutomationProperties.Name on focus — producing a duplicate readout. With this
/// control, the outer is invisible to UIA and only the inner part announces.
/// </summary>
public class FlatComboBox : ComboBox
{
    public FlatComboBox()
    {
        // Tab moves directly into the inner edit TextBox; the outer container is purely
        // visual.
        IsTabStop = false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TransparentPeer(this);

    private sealed class TransparentPeer(ComboBox owner) : ComboBoxAutomationPeer(owner)
    {
        // Hide the ComboBox from the screen reader's control view. The inner editable
        // TextBox stays visible as a control, so focus + announcements still work — just
        // without the duplicate "ComboBox" preamble.
        protected override bool IsControlElementCore() => false;
    }
}
