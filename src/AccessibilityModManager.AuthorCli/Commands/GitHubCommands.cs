using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.AuthorTool.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class GitHubCommands
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var gitHub = services.GetRequiredService<IGitHubService>();
        var writer = services.GetRequiredService<OutcomeWriter>();
        var command = new Command("github", "Inspect GitHub CLI authentication, repositories, and releases.");

        var status = new Command("status", "Check GitHub CLI availability and authentication.");
        status.SetAction(async (parseResult, cancellationToken) =>
        {
            var available = await gitHub.IsAvailableAsync(cancellationToken);
            var authenticated = available && await gitHub.IsAuthenticatedAsync(cancellationToken);
            var result = CatalogCommandSupport.Success(
                "githubStatus",
                new { available, authenticated },
                available
                    ? authenticated ? "GitHub CLI is available and authenticated." : "GitHub CLI is available but not authenticated."
                    : "GitHub CLI is not available.");
            return CatalogCommandSupport.Complete(writer, parseResult, result);
        });

        var repos = new Command("repos", "List repositories the signed-in user can push to.");
        repos.SetAction(async (parseResult, cancellationToken) =>
        {
            await EnsureReadyAsync(gitHub, cancellationToken);
            var values = await gitHub.ListReposAsync(ct: cancellationToken);
            return CatalogCommandSupport.Complete(
                writer,
                parseResult,
                CatalogCommandSupport.Success(
                    "githubReposListed",
                    new { repositories = values },
                    $"Found {values.Count} writable GitHub repository or repositories."));
        });

        var releases = new Command("releases", "List releases in a GitHub repository.");
        var repoOption = new Option<string>("--repo")
        {
            Description = "GitHub repository in owner/name form.",
            Required = true
        };
        releases.Options.Add(repoOption);
        releases.SetAction(async (parseResult, cancellationToken) =>
        {
            await EnsureReadyAsync(gitHub, cancellationToken);
            var repo = CatalogCommandSupport.NormalizeGitHubRepo(parseResult.GetValue(repoOption)!);
            var values = await gitHub.ListReleasesAsync(repo, ct: cancellationToken);
            return CatalogCommandSupport.Complete(
                writer,
                parseResult,
                CatalogCommandSupport.Success(
                    "githubReleasesListed",
                    new { repository = repo, releases = values },
                    $"Found {values.Count} release(s) in '{repo}'."));
        });

        command.Subcommands.Add(status);
        command.Subcommands.Add(repos);
        command.Subcommands.Add(releases);
        return command;
    }

    private static async Task EnsureReadyAsync(IGitHubService gitHub, CancellationToken cancellationToken)
    {
        CatalogCommandSupport.EnsureGitAvailable(
            await gitHub.IsAvailableAsync(cancellationToken),
            "GitHub CLI (gh)");
        CatalogCommandSupport.EnsureGitHubAuthenticated(
            await gitHub.IsAuthenticatedAsync(cancellationToken));
    }
}
