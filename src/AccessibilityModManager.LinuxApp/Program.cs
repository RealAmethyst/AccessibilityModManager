using AccessibilityModManager.Infrastructure.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Themes.Fluent;

namespace AccessibilityModManager.LinuxApp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Use the Windows manager on Windows.");
        if (LinuxInstallCommand.TryRun(args, LinuxApplication.Manager, out var installError))
        {
            if (installError is null || args[0] == "--install") return;

        }
        AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .Start((app, _) =>
            {
                app.Name = "Accessibility Mod Manager";
                app.Styles.Add(new FluentTheme());
                Window window;
                if (installError is not null)
                {
                    var body = new TextBox { IsReadOnly = true, AcceptsReturn = true,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Text = installError + "\n\nOpen the manager from the application menu to continue with the installed build." };
                    Avalonia.Automation.AutomationProperties.SetName(body, "Update failure details");
                    var close = new Button { Content = "Close" };
                    var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
                    panel.Children.Add(body);
                    panel.Children.Add(close);
                    window = new Window { Title = "Manager update failed", Width = 640, Height = 360, Content = panel };
                    close.Click += (_, _) => window.Close();
                    window.Opened += (_, _) => body.Focus();
                }
                else window = new MainWindow();
                window.Show();
                app.Run(window);
            }, args);
    }
}
