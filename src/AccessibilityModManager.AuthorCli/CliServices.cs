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
    string? LogDirectory = null,
    IGitHubService? GitHubService = null,
    IPublishedAssetProbe? PublishedAssetProbe = null,
    IReleaseWorkflow? ReleaseWorkflow = null,
    IIndexWorkflow? IndexWorkflow = null,
    ICompleteReleasePublishWorkflow? CompleteReleasePublishWorkflow = null,
    IPatreonAuthorSession? PatreonAuthorSession = null,
    IPatreonWorkflow? PatreonWorkflow = null,
    IServerAuthorTransport? ServerAuthorTransport = null,
    IServerWorkflow? ServerWorkflow = null,
    HttpClient? HttpClient = null);

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
        services.AddSingleton(overrides.HttpClient ?? new HttpClient());
        services.AddSingleton<AuthorConfigService>(sp =>
            new AuthorConfigService(
                sp.GetRequiredService<ILogger>(),
                overrides.AuthorConfigDirectory));
        services.AddSingleton<GitService>();
        if (overrides.GitHubService is not null)
        {
            services.AddSingleton(overrides.GitHubService);
        }
        else
        {
            services.AddSingleton<GitHubService>();
            services.AddSingleton<IGitHubService>(sp => sp.GetRequiredService<GitHubService>());
        }

        if (overrides.PublishedAssetProbe is not null)
        {
            services.AddSingleton(overrides.PublishedAssetProbe);
        }
        else
        {
            services.AddSingleton<IPublishedAssetProbe, PublishedAssetProbe>();
        }
        services.AddSingleton<IndexFileService>();
        services.AddSingleton<ManifestBuilderService>();
        services.AddSingleton<Sha256HashService>();
        services.AddSingleton<RegistryMembershipChecker>();
        services.AddSingleton<ServerUploadService>();
        services.AddSingleton<PatreonAuthorService>();
        services.AddSingleton<PublisherHeadStore>();
        services.AddSingleton<ClaimSigningKeyStore>();
        services.AddSingleton<IndexProofService>();
        services.AddSingleton<ProjectReconciler>();
        services.AddSingleton<IndexPublishCoordinator>();
        services.AddSingleton<GitHubIndexPublisher>();
        services.AddSingleton<UnsignedPublishGate>();

        services.AddSingleton<AuthorProjectContext>();
        services.AddSingleton<JsonPayloadService>();
        services.AddSingleton<CatalogWorkflow>();
        services.AddSingleton<PackageWorkflow>();
        if (overrides.ReleaseWorkflow is not null)
        {
            services.AddSingleton(overrides.ReleaseWorkflow);
        }
        else
        {
            services.AddSingleton<ReleaseWorkflow>();
            services.AddSingleton<IReleaseWorkflow>(sp => sp.GetRequiredService<ReleaseWorkflow>());
        }

        if (overrides.IndexWorkflow is not null)
        {
            services.AddSingleton(overrides.IndexWorkflow);
        }
        else
        {
            services.AddSingleton<IndexWorkflow>();
            services.AddSingleton<IIndexWorkflow>(sp => sp.GetRequiredService<IndexWorkflow>());
        }

        if (overrides.CompleteReleasePublishWorkflow is not null)
        {
            services.AddSingleton(overrides.CompleteReleasePublishWorkflow);
        }
        else
        {
            services.AddSingleton<CompleteReleasePublishWorkflow>();
            services.AddSingleton<ICompleteReleasePublishWorkflow>(
                sp => sp.GetRequiredService<CompleteReleasePublishWorkflow>());
        }

        if (overrides.PatreonAuthorSession is not null)
        {
            services.AddSingleton(overrides.PatreonAuthorSession);
        }
        else
        {
            services.AddSingleton<PatreonAuthorSession>();
            services.AddSingleton<IPatreonAuthorSession>(
                sp => sp.GetRequiredService<PatreonAuthorSession>());
        }

        if (overrides.PatreonWorkflow is not null)
        {
            services.AddSingleton(overrides.PatreonWorkflow);
        }
        else
        {
            services.AddSingleton<PatreonWorkflow>();
            services.AddSingleton<IPatreonWorkflow>(sp => sp.GetRequiredService<PatreonWorkflow>());
        }

        if (overrides.ServerAuthorTransport is not null)
        {
            services.AddSingleton(overrides.ServerAuthorTransport);
        }
        else
        {
            services.AddSingleton<ServerAuthorTransport>();
            services.AddSingleton<IServerAuthorTransport>(
                sp => sp.GetRequiredService<ServerAuthorTransport>());
        }

        if (overrides.ServerWorkflow is not null)
        {
            services.AddSingleton(overrides.ServerWorkflow);
        }
        else
        {
            services.AddSingleton<ServerWorkflow>();
            services.AddSingleton<IServerWorkflow>(sp => sp.GetRequiredService<ServerWorkflow>());
        }

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
