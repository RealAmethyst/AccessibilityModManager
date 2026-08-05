using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Authoring.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AccessibilityModManager.AuthorCli;

public static class CliServices
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager-Author",
        "logs");

    public static ServiceProvider Create()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ILogger>(_ => CreateLogger());
        services.AddSingleton<CliConsole>(_ => CliConsole.CreateSystem());
        services.AddSingleton<ICliConsole>(sp => sp.GetRequiredService<CliConsole>());

        services.AddSingleton<OutcomeWriter>();
        services.AddSingleton<AuthorConfigService>();
        services.AddSingleton<IndexFileService>();
        services.AddSingleton<AuthorProjectContext>();
        services.AddSingleton<JsonPayloadService>();

        return services.BuildServiceProvider();
    }

    private static ILogger CreateLogger()
    {
        Directory.CreateDirectory(LogDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(LogDirectory, "amm-author-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
