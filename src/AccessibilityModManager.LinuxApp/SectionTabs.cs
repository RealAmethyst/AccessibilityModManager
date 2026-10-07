using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;

namespace AccessibilityModManager.LinuxApp;

internal sealed class SectionTabs : TabControl
{
    protected override Type StyleKeyOverride => typeof(TabControl);

    public SectionTabs()
    {
        ItemsPanel = new FuncTemplate<Panel?>(() =>
        {
            var strip = new WrapPanel();
            KeyboardNavigation.SetTabNavigation(strip, KeyboardNavigationMode.Continue);
            return strip;
        });
        AutomationProperties.SetName(this, "Sections");
        ContainerPrepared += (_, e) =>
        {
            if (e.Container is TabItem item)
                item.Bind(IsTabStopProperty, new Binding(nameof(TabItem.IsSelected)) { Source = item });
        };
    }
}
