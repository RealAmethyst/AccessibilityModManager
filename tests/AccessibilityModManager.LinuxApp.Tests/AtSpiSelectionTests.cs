#if PATCHED_ATSPI
using System.Reflection;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace AccessibilityModManager.LinuxApp.Tests;

// The packaging build supplies AtSpiBridgePath to test the actual patched bridge.
public sealed class AtSpiSelectionTests
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [AvaloniaFact]
    public void Closed_combo_exposes_selected_child_and_invalidates_when_selection_changes()
    {
        var combo = new ComboBox { ItemsSource = new[] { "stable", "beta" }, SelectedIndex = 0 };
        AutomationProperties.SetName(combo, "Default channel");
        var peer = ControlAutomationPeer.CreatePeerForElement(combo)!;
        var node = CreateNode(peer);
        try
        {
            Assert.Equal("Default channel", peer.GetName());
            AssertSelected(node, peer, "stable");
            SetClean(node);
            combo.SelectedIndex = 1;
            AssertDirty(node);
            AssertSelected(node, peer, "beta");
            SetClean(node);
            combo.SelectedIndex = -1;
            AssertDirty(node);
            Assert.Empty(Children(node));
        }
        finally { Invoke(node, "Detach"); }
    }

    [AvaloniaFact]
    public void Opening_and_closing_combo_switches_between_realized_items_and_selected_child()
    {
        var combo = new ComboBox { ItemsSource = new[] { "stable", "beta" }, SelectedIndex = 1 };
        var window = new Window { Content = combo };
        window.Show();
        var peer = ControlAutomationPeer.CreatePeerForElement(combo)!;
        var node = CreateNode(peer);
        try
        {
            AssertSelected(node, peer, "beta");
            SetClean(node);
            combo.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            AssertDirty(node);
            Assert.Equal(new[] { "stable", "beta" }, Children(node).Select(item => item.GetName()));
            Assert.Contains(((ISelectionProvider)peer).GetSelection().Single(), Children(node));
            SetClean(node);
            combo.IsDropDownOpen = false;
            Dispatcher.UIThread.RunJobs();
            AssertDirty(node);
            AssertSelected(node, peer, "beta");
        }
        finally { Invoke(node, "Detach"); window.Close(); }
    }

    [AvaloniaFact]
    public void Ordinary_list_keeps_all_items_instead_of_only_the_selection()
    {
        var list = new ListBox { ItemsSource = new[] { "one", "two" }, SelectedIndex = 1 };
        var window = new Window { Content = list };
        window.Show();
        var node = CreateNode(ControlAutomationPeer.CreatePeerForElement(list)!);
        try { Assert.Equal(new[] { "one", "two" }, Children(node).Select(item => item.GetName())); }
        finally { Invoke(node, "Detach"); window.Close(); }
    }

    private static object CreateNode(AutomationPeer peer)
    {
        var assembly = Assembly.Load("Avalonia.FreeDesktop.AtSpi");
        var server = Activator.CreateInstance(assembly.GetType("Avalonia.FreeDesktop.AtSpi.AtSpiServer")!, true)!;
        var type = assembly.GetType("Avalonia.FreeDesktop.AtSpi.AtSpiNode")!;
        var node = type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [peer, server])!;
        Invoke(node, "Attach", [null]);
        return node;
    }

    private static void AssertSelected(object node, AutomationPeer peer, string name)
    {
        var selected = Assert.Single(((ISelectionProvider)peer).GetSelection());
        Assert.Same(selected, Assert.Single(Children(node)));
        Assert.Equal(name, selected.GetName());
    }

    private static IReadOnlyList<AutomationPeer> Children(object node) =>
        (IReadOnlyList<AutomationPeer>)Invoke(node, "GetChildPeers")!;
    private static object? Invoke(object target, string name, object?[]? args = null) =>
        target.GetType().GetMethod(name, InstanceMembers)!.Invoke(target, args);
    private static FieldInfo DirtyField(object node) => node.GetType().GetField("_childrenDirty", InstanceMembers)!;
    private static void SetClean(object node) => DirtyField(node).SetValue(node, false);
    private static void AssertDirty(object node) => Assert.True((bool)DirtyField(node).GetValue(node)!);
}
#endif
