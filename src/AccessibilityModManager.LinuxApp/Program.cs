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
        AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .Start((app, _) =>
            {
                app.Name = "Accessibility Mod Manager";
                app.Styles.Add(new FluentTheme());
                var window = new MainWindow();
                window.Show();
                app.Run(window);
            }, args);
    }
}
