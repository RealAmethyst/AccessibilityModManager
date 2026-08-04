using System.Reflection;
using System.Windows.Documents;
using System.Windows.Input;
using AccessibilityModManager.App.Controls;
using Xunit;

namespace AccessibilityModManager.Tests.ViewModels;

/// <summary>
/// You must always be able to leave a read-only text field by keyboard.
///
/// <para>This exists because the opposite shipped. Blocking every editor command that inserts text
/// swept up <c>EditingCommands.TabForward</c> and <c>TabBackward</c> — and in a TextBox that does
/// not accept tabs, Tab is not text entry, it is how focus leaves the control. Moving into a mod
/// description or a developer's bio trapped the keyboard completely.</para>
///
/// <para>The list is read by reflection rather than exposed: it is an implementation detail, and
/// widening its visibility just to be testable would be a worse trade than this. What matters is
/// that the invariant is checked at all.</para>
/// </summary>
public class ReadOnlyTextEscapeTests
{
    private static RoutedUICommand[] BlockedCommands()
    {
        var field = typeof(ReadOnlyText).GetField(
            "BlockedCommands", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (RoutedUICommand[])field!.GetValue(null)!;
    }

    [Fact]
    public void TabIsNeverBlocked_SoFocusCanAlwaysLeaveTheField()
    {
        var blocked = BlockedCommands();

        Assert.DoesNotContain(EditingCommands.TabForward, blocked);
        Assert.DoesNotContain(EditingCommands.TabBackward, blocked);
    }

    /// <summary>
    /// The commands that genuinely change text are still refused — the protection this class exists
    /// for has to survive the fix that unblocked Tab.
    /// </summary>
    [Fact]
    public void TheEditingCommandsThatChangeTextAreStillBlocked()
    {
        var blocked = BlockedCommands();

        Assert.Contains(ApplicationCommands.Paste, blocked);
        Assert.Contains(ApplicationCommands.Cut, blocked);
        Assert.Contains(ApplicationCommands.Undo, blocked);
        Assert.Contains(ApplicationCommands.Redo, blocked);
        Assert.Contains(EditingCommands.Backspace, blocked);
        Assert.Contains(EditingCommands.Delete, blocked);
        Assert.Contains(EditingCommands.EnterParagraphBreak, blocked);
    }
}
