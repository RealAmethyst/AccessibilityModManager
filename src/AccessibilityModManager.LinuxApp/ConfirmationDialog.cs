using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AccessibilityModManager.LinuxApp;

internal static class ConfirmationDialog
{
    public static async Task<bool> ShowAsync(Window owner, string title, string message,
        string acceptLabel)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 620,
            Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        AutomationProperties.SetName(dialog, title);
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var explanation = new TextBox
        {
            Text = message,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        AutomationProperties.SetName(explanation, title + " details");
        root.Children.Add(explanation);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var accept = new Button { Content = acceptLabel };
        AutomationProperties.SetName(accept, acceptLabel);
        accept.Click += (_, _) => dialog.Close(true);
        var cancel = new Button { Content = "Cancel" };
        AutomationProperties.SetName(cancel, "Cancel");
        cancel.Click += (_, _) => dialog.Close(false);
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        dialog.Content = root;
        cancel.Loaded += (_, _) => cancel.Focus();
        return await dialog.ShowDialog<bool>(owner);
    }
}
