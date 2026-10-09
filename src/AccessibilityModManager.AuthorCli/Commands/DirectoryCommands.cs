using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class DirectoryCommands
{
    public static Command Create(IServiceProvider services)
    {
        var group = new Command("directory", "Submit your published GitHub catalog for discovery in the manager.");
        var submit = new Command("submit", "Retry directory submission without uploading a release or committing the catalog.");
        submit.SetAction(async (parse, ct) =>
        {
            var project = await CatalogCommandSupport.ResolveProjectAsync(services.GetRequiredService<AuthorProjectContext>(), parse, ct);
            var (target, error) = await services.GetRequiredService<GitHubIndexPublisher>().ResolveTargetAsync(project.ProjectPath, ct);
            if (target is null) throw CatalogCommandSupport.Validation(error ?? "GitHub target is unavailable.");
            var request = new PluginDirectorySubmission($"{target.Owner}/{target.Repo}", target.IndexPathInRepo,
                project.Index.PluginId, target.BranchRawUrl);
            var dryRun = CatalogCommandSupport.GetDryRun(parse);
            if (!dryRun)
            {
                CatalogCommandSupport.EnsureYes(parse, "Directory submission requires --yes.");
                await services.GetRequiredService<PluginDirectoryClient>().SubmitAsync(request, ct);
            }
            return CatalogCommandSupport.Complete(services.GetRequiredService<OutcomeWriter>(), parse,
                CatalogCommandSupport.Success(dryRun ? "directorySubmissionPreviewed" : "directorySubmitted", request,
                    dryRun ? "Would submit this public catalog for discovery. The default branch must contain the published index."
                        : "Directory submission received. No release was uploaded or catalog committed."));
        });
        group.Subcommands.Add(submit);
        return group;
    }
}
