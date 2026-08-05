using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Authoring.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AccessibilityModManager.AuthorCli;

public sealed record CliServiceOverrides(
    ICliConsole? Console = null,
    ILogger? Logger = null,
    string? AuthorConfigDirectory = null,
    string? LogDirectory = null);

public static class CliServices
{
    private static readonly string DefaultLogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager-Author",
        "logs");

    public static ServiceProvider Create(CliServiceOverrides? overrides = null)
    {
        overrides ??= new CliServiceOverrides();
        var services = new ServiceCollection();

        services.AddSingleton<ILogger>(_ =>
            overrides.Logger ?? CreateLogger(overrides.LogDirectory ?? DefaultLogDirectory));
        if (overrides.Console is not null)
        {
            services.AddSingleton(overrides.Console);
        }
        else
        {
            services.AddSingleton<CliConsole>(_ => CliConsole.CreateSystem());
            services.AddSingleton<ICliConsole>(sp => sp.GetRequiredService<CliConsole>());
        }

        services.AddSingleton<OutcomeWriter>();
        services.AddSingleton<AuthorConfigService>(sp =>
            new AuthorConfigService(
                sp.GetRequiredService<ILogger>(),
                overrides.AuthorConfigDirectory));
        services.AddSingleton<GitService>();
        services.AddSingleton<GitHubService>();
        services.AddSingleton<IndexFileService>();

        services.AddSingleton<AuthorProjectContext>();
        services.AddSingleton<JsonPayloadService>();
        services.AddSingleton<CatalogWorkflow>();

        return services.BuildServiceProvider();
    }

    private static ILogger CreateLogger(string logDirectory)
    {
        ArgumentNullException.ThrowIfNull(logDirectory);
        Directory.CreateDirectory(logDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(logDirectory, "amm-author-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
