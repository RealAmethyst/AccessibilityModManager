using AccessibilityModManager.LinuxApp;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(AccessibilityModManager.LinuxApp.Tests.TestApplication))]

namespace AccessibilityModManager.LinuxApp.Tests;

public sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class NavigationTests
{
    [AvaloniaFact]
    public void Tab_visits_the_selected_header_once_and_arrows_select_other_sections()
    {
        var tabs = new SectionTabs();
        var modsButton = new Button { Content = "Refresh Mods" };
        var authorButton = new Button { Content = "Refresh Authors" };
        var mods = new TabItem { Header = "Mods", Content = modsButton };
        var authors = new TabItem { Header = "Authors", Content = authorButton };
        tabs.Items.Add(mods);
        tabs.Items.Add(authors);
        tabs.Items.Add(new TabItem { Header = "Settings", Content = new Button { Content = "Settings control" } });
        var window = new Window { Content = tabs };
        window.Show();
        try
        {
            mods.Focus();
            Press(window, Key.Tab);
            Assert.True(modsButton.IsFocused);
            Press(window, Key.Tab, RawInputModifiers.Shift);
            Assert.True(mods.IsFocused, "Shift+Tab focused " + window.FocusManager?.GetFocusedElement() + "; tab active " + KeyboardNavigation.GetTabOnceActiveElement(tabs) + "; strip active " + KeyboardNavigation.GetTabOnceActiveElement(tabs.GetVisualDescendants().OfType<WrapPanel>().Single()));
            Press(window, Key.Right);
            Assert.True(authors.IsFocused);
            Assert.Equal(1, tabs.SelectedIndex);
            Press(window, Key.Tab);
            Assert.True(authorButton.IsFocused);
            Press(window, Key.Tab, RawInputModifiers.Shift);
            Assert.True(authors.IsFocused);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Filter_header_is_named_focusable_and_exposes_expand_collapse_state()
    {
        var before = new Button { Content = "Before filters" };
        var check = new CheckBox { Content = "Screen reader" };
        var expander = new FilterExpander("Tags", check);
        var after = new Button { Content = "After filters" };
        var root = new StackPanel();
        root.Children.Add(before); root.Children.Add(expander); root.Children.Add(after);
        var window = new Window { Content = root };
        window.Show();
        try
        {
            before.Focus();
            Press(window, Key.Tab);
            Assert.True(expander.IsFocused);
            var peer = ControlAutomationPeer.CreatePeerForElement(expander)!;
            Assert.Equal("Tags filters", peer.GetName());
            var provider = Assert.IsAssignableFrom<IExpandCollapseProvider>(peer);
            Assert.Equal(ExpandCollapseState.Expanded, provider.ExpandCollapseState);
            Press(window, Key.Space);
            Assert.Equal(ExpandCollapseState.Collapsed, provider.ExpandCollapseState);
            Press(window, Key.Tab);
            Assert.True(after.IsFocused);
            Press(window, Key.Tab, RawInputModifiers.Shift);
            Assert.True(expander.IsFocused);
            Press(window, Key.Enter);
            Assert.Equal(ExpandCollapseState.Expanded, provider.ExpandCollapseState);
            Press(window, Key.Tab);
            Assert.True(check.IsFocused);
        }
        finally { window.Close(); }
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }
}
