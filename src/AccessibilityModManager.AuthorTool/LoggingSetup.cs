using System.IO;
using Serilog;

namespace AccessibilityModManager.AuthorTool;

internal static class LoggingSetup
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager-Author",
        "logs");

    public static ILogger CreateLogger()
    {
        Directory.CreateDirectory(LogDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(LogDirectory, "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public static string GetLogDirectory() => LogDirectory;
}
