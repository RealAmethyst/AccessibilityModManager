using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace AccessibilityModManager.LinuxApp;

internal sealed class FilterExpander : Expander
{
    protected override Type StyleKeyOverride => typeof(Expander);

    public FilterExpander(string title, Control content)
    {
        Header = title;
        Content = content;
        IsExpanded = true;
        Focusable = true;
        IsTabStop = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(this, title + " filters");
        TemplateApplied += (_, e) =>
        {
            // The expander peer exposes Expanded/Collapsed; the template toggle does not.
            if (e.NameScope.Find<Control>("ExpanderHeader") is { } header)
            {
                header.Focusable = false;
                header.IsTabStop = false;
                AutomationProperties.SetAccessibilityView(header, AccessibilityView.Raw);
            }
        };
        KeyDown += (_, e) =>
        {
            if (!IsFocused || e.Key is not (Key.Space or Key.Enter or Key.Left or Key.Right)) return;
            IsExpanded = e.Key switch { Key.Left => false, Key.Right => true, _ => !IsExpanded };
            e.Handled = true;
        };
    }
}
