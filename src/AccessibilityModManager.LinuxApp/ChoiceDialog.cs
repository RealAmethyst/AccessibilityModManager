using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AccessibilityModManager.LinuxApp;

internal static class ChoiceDialog
{
    public static async Task<T?> ChooseAsync<T>(Window owner, string title,
        string explanation, IReadOnlyList<T> choices) where T : class
    {
        var dialog = new Window
        {
            Title = title, Width = 520, Height = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = explanation,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var list = new ComboBox { ItemsSource = choices, SelectedIndex = 0 };
        AutomationProperties.SetName(list, title);
        root.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var choose = new Button { Content = "Continue" };
        choose.Click += (_, _) => dialog.Close(list.SelectedItem as T);
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => dialog.Close(null);
        buttons.Children.Add(choose);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        dialog.Content = root;
        dialog.Opened += (_, _) => list.Focus();
        return await dialog.ShowDialog<T?>(owner);
    }
}
