using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace AccessibilityModManager.LinuxAuthorTool;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args) => AppBuilder.Configure<AuthorApp>()
        .UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}

internal sealed class AuthorApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new AuthorWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
