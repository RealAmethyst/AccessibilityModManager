using AccessibilityModManager.Core.Models;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace AccessibilityModManager.LinuxApp;

internal sealed class SourcesDialog : Window
{
    public SourcesDialog(IReadOnlyList<SourceListItem> sources)
    {
        Title = "Sources";
        Width = 560;
        Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = sources.Count == 0 ? "No sources are currently listed." : "Choose a source to add or remove.", Margin = new Thickness(0, 0, 0, 12) });
        var list = new ListBox { Name = "SourcesList", ItemsSource = sources, SelectedIndex = sources.Count > 0 ? 0 : -1 };
        AutomationProperties.SetName(list, "Sources");
        list.ContainerPrepared += (_, e) =>
        {
            if (e.Index >= 0 && e.Index < sources.Count)
                AutomationProperties.SetName(e.Container, sources[e.Index].ToString());
        };
        Grid.SetRow(list, 1);
        root.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var action = new Button { Name = "SourceAction" };
        void UpdateAction()
        {
            action.Content = (list.SelectedItem as SourceListItem)?.ActionLabel ?? "Add";
            action.IsEnabled = list.SelectedItem is SourceListItem;
        }
        list.SelectionChanged += (_, _) => UpdateAction();
        UpdateAction();
        action.Click += (_, _) => Close(list.SelectedItem as SourceListItem);
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(action);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
            else if (e.Key == Key.Enter && list.IsKeyboardFocusWithin && list.SelectedItem is SourceListItem selected)
            { Close(selected); e.Handled = true; }
        };
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (list.ContainerFromIndex(list.SelectedIndex) is Control item) item.Focus();
            else if (sources.Count > 0) list.Focus();
            else cancel.Focus();
        }, DispatcherPriority.Loaded);
    }
}
