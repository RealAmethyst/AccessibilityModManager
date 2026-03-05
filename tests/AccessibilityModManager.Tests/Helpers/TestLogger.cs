using Serilog;

namespace AccessibilityModManager.Tests.Helpers;

/// <summary>
/// Silent logger for tests.
/// </summary>
public static class TestLogger
{
    public static ILogger Create() =>
        new LoggerConfiguration().MinimumLevel.Debug().CreateLogger();
}
