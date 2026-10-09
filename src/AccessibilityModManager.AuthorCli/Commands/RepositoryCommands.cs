using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class RepositoryCommands
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("create", "Create a public GitHub plugin repository and initialize a local catalog. Existing repositories use project clone.");
        var repo = new Argument<string>("repo") { Description = "New GitHub repository as owner/name." };
        var plugin = new Option<string>("--plugin-id") { Required = true, Description = "Stable plugin identifier." };
        command.Arguments.Add(repo);
        command.Options.Add(plugin);
        command.SetAction(async (parse, ct) =>
        {
            var name = CatalogCommandSupport.NormalizeGitHubRepo(parse.GetValue(repo)!);
            var target = CatalogCommandSupport.GetProjectOption(parse);
            if (string.IsNullOrWhiteSpace(target))
                throw CatalogCommandSupport.Usage("project create requires --project <new-folder>.");
            target = Path.GetFullPath(target);
            if (File.Exists(target) || Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                throw CatalogCommandSupport.Conflict("Choose an empty local folder. Existing projects must use project open.");
            var candidate = services.GetRequiredService<CatalogWorkflow>().CreateProject(parse.GetValue(plugin)!);
            CatalogCommandSupport.ValidateIndexCandidate(candidate);
            var writer = services.GetRequiredService<OutcomeWriter>();
            if (CatalogCommandSupport.GetDryRun(parse))
                return CatalogCommandSupport.Complete(writer, parse, CatalogCommandSupport.Success(
                    "repositoryCreatePreviewed", new { repository = name, projectPath = target, candidate.PluginId },
                    $"Would create public repository {name} and initialize {target}. Repository availability has not been checked."));

            CatalogCommandSupport.EnsureYes(parse, "Creating a public GitHub repository requires --yes.");
            var github = services.GetRequiredService<IGitHubService>();
            var git = services.GetRequiredService<GitService>();
            CatalogCommandSupport.EnsureGitAvailable(await git.IsAvailableAsync(ct), "Git");
            CatalogCommandSupport.EnsureGitAvailable(await github.IsAvailableAsync(ct), "GitHub CLI ('gh')");
            CatalogCommandSupport.EnsureGitHubAuthenticated(await github.IsAuthenticatedAsync(ct));
            var created = await github.CreateRepositoryAsync(name, ct);
            if (!created.Success)
                throw CatalogCommandSupport.Conflict($"Repository creation failed: {created.Combined}. If it already exists, use project clone.");
            var cloned = await git.CloneAsync($"https://github.com/{name}.git", target, ct);
            if (!cloned.Success)
                throw new WorkflowException(WorkflowErrorKind.Conflict, "repositoryCreatedCloneFailed",
                    [$"Public repository {name} was created, but cloning failed: {cloned.Combined}. Retry with project clone."],
                    ["repositoryCreated"]);
            await using var lease = await services.GetRequiredService<AuthorProjectContext>().AcquireWriteLeaseAsync(target, ct);
            services.GetRequiredService<IndexFileService>().Save(target, CatalogCommandSupport.StampGeneratedAt(candidate));
            var config = services.GetRequiredService<AuthorConfigService>();
            config.RecordRecent(target, name, name);
            config.SetPublishDestination(target, candidate.PluginId, PublishDestination.GitHub);
            return CatalogCommandSupport.Complete(writer, parse, CatalogCommandSupport.Success(
                "repositoryCreated", new { repository = name, projectPath = target, candidate.PluginId },
                $"Created {name} and initialized the local catalog. Add your author information and games, then run index publish --yes."));
        });
        return command;
    }
}
