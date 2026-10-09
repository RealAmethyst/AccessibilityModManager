using AccessibilityModManager.Core.Models;
using AccessibilityModManager.LinuxApp;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AccessibilityModManager.LinuxApp.Tests;

public sealed class SourcesDialogTests
{
    [AvaloniaFact]
    public async Task Sources_are_a_keyboard_list_and_the_action_matches_saved_membership()
    {
        var available = new PluginDirectoryEntry("new", "new-author", "New author", "new-author", 43,
            "new-author/mods", "https://raw.githubusercontent.com/new-author/mods/main/index.json", []);
        var added = new UserPluginSource { PluginId = "buu420", DisplayName = "Buu420",
            IndexUrl = "https://raw.githubusercontent.com/buu420/buu-s-mods/main/index.json" };
        var owner = new Window();
        owner.Show();
        var dialog = new SourcesDialog([new(available, null), new(null, added)]);
        var result = dialog.ShowDialog<SourceListItem?>(owner);
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Empty(dialog.GetVisualDescendants().OfType<ComboBox>());
            var list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.Equal("Sources", ControlAutomationPeer.CreatePeerForElement(list)!.GetName());
            Assert.True(list.IsKeyboardFocusWithin);
            var action = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SourceAction");
            Assert.Equal("Add", action.Content);
            dialog.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
            dialog.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, list.SelectedIndex);
            Assert.Equal("Remove", action.Content);
            Assert.Contains("Buu420, added", ControlAutomationPeer.CreatePeerForElement((Control)list.ContainerFromIndex(1)!)!.GetName());
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(added, (await result)!.Saved);
        }
        finally { dialog.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public void Empty_sources_disable_add_and_focus_cancel()
    {
        var owner = new Window();
        owner.Show();
        var dialog = new SourcesDialog([]);
        _ = dialog.ShowDialog<SourceListItem?>(owner);
        Dispatcher.UIThread.RunJobs();
        try
        {
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.False(buttons.Single(button => button.Name == "SourceAction").IsEnabled);
            Assert.True(buttons.Single(button => button.Content as string == "Cancel").IsFocused);
        }
        finally { dialog.Close(); owner.Close(); }
    }
}
