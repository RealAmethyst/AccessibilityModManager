using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class AuthorCommands
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var outcomeWriter = services.GetRequiredService<OutcomeWriter>();
        var console = services.GetRequiredService<ICliConsole>();
        var projectContext = services.GetRequiredService<AuthorProjectContext>();
        var indexFiles = services.GetRequiredService<IndexFileService>();
        var jsonPayloads = services.GetRequiredService<JsonPayloadService>();
        var catalogWorkflow = services.GetRequiredService<CatalogWorkflow>();

        var author = new Command("author", "Read or update the author block.");

        var show = new Command("show", "Show the current author block.");
        show.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projectContext, parseResult, cancellationToken);
            var result = CatalogCommandSupport.Success(
                "authorShown",
                new
                {
                    projectPath = resolved.ProjectPath,
                    pluginId = resolved.Index.PluginId,
                    author = resolved.Index.Author
                },
                resolved.Index.Author is null
                    ? $"Project '{resolved.Index.PluginId}' has no author block."
                    : $"Loaded the author block for '{resolved.Index.PluginId}'.");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var set = new Command("set", "Replace the author block from a camelCase JSON document.");
        var inputOption = new Option<string>(CatalogCommandSupport.InputOptionName)
        {
            Description = "Path to a camelCase JSON file, or - for standard input."
        };
        set.Options.Add(inputOption);
        set.SetAction(async (parseResult, cancellationToken) =>
        {
            var inputSource = parseResult.GetValue(inputOption);
            if (string.IsNullOrWhiteSpace(inputSource))
            {
                throw CatalogCommandSupport.Usage(
                    "author set requires --input <file-or-dash>.");
            }

            var replacement = await CatalogCommandSupport.ReadInputModelAsync<PluginAuthorInfo>(
                jsonPayloads,
                console,
                inputSource,
                cancellationToken);

            var result = await CatalogCommandSupport.SaveMutationAsync(
                parseResult,
                projectContext,
                indexFiles,
                index => catalogWorkflow.SetAuthor(index, replacement),
                "authorUpdated",
                "Updated the author block.",
                "Dry run: would update the author block.",
                cancellationToken);

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        author.Subcommands.Add(show);
        author.Subcommands.Add(set);
        return author;
    }
}
