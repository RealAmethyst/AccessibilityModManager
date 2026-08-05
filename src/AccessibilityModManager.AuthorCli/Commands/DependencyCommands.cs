using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class DependencyCommands
{
    public static void AddTo(RootCommand root, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(services);

        var outcomeWriter = services.GetRequiredService<OutcomeWriter>();
        var console = services.GetRequiredService<ICliConsole>();
        var projectContext = services.GetRequiredService<AuthorProjectContext>();
        var indexFiles = services.GetRequiredService<IndexFileService>();
        var jsonPayloads = services.GetRequiredService<JsonPayloadService>();
        var catalogWorkflow = services.GetRequiredService<CatalogWorkflow>();

        var dependency = new Command("dependency", "Read or update dependencies in a game definition.");

        var list = new Command("list", "List dependencies for one game.");
        var listGameIdArgument = new Argument<string>("game-id")
        {
            Description = "Game id."
        };
        list.Arguments.Add(listGameIdArgument);
        list.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projectContext, parseResult, cancellationToken);
            var game = CatalogCommandSupport.FindGame(resolved.Index, parseResult.GetValue(listGameIdArgument)!);

            var result = CatalogCommandSupport.Success(
                "dependenciesListed",
                new
                {
                    projectPath = resolved.ProjectPath,
                    pluginId = resolved.Index.PluginId,
                    gameId = game.GameId,
                    dependencies = game.Dependencies
                },
                $"Loaded {game.Dependencies.Count} dependenc" + (game.Dependencies.Count == 1 ? "y" : "ies") + $" for '{game.GameId}'.");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var show = new Command("show", "Show one dependency.");
        var showGameIdArgument = new Argument<string>("game-id")
        {
            Description = "Game id."
        };
        var showDependencyIdArgument = new Argument<string>("dependency-id")
        {
            Description = "Dependency id."
        };
        show.Arguments.Add(showGameIdArgument);
        show.Arguments.Add(showDependencyIdArgument);
        show.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projectContext, parseResult, cancellationToken);
            var game = CatalogCommandSupport.FindGame(resolved.Index, parseResult.GetValue(showGameIdArgument)!);
            var selectedDependency = CatalogCommandSupport.FindDependency(game, parseResult.GetValue(showDependencyIdArgument)!);

            var result = CatalogCommandSupport.Success(
                "dependencyShown",
                new
                {
                    projectPath = resolved.ProjectPath,
                    pluginId = resolved.Index.PluginId,
                    gameId = game.GameId,
                    dependency = selectedDependency
                },
                $"Loaded dependency '{selectedDependency.Id}' for '{game.GameId}'.");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var set = new Command("set", "Add or replace one dependency from a camelCase JSON document.");
        var setGameIdArgument = new Argument<string>("game-id")
        {
            Description = "Game id."
        };
        var inputOption = new Option<string>(CatalogCommandSupport.InputOptionName)
        {
            Description = "Path to a camelCase JSON file, or - for standard input."
        };
        set.Arguments.Add(setGameIdArgument);
        set.Options.Add(inputOption);
        set.SetAction(async (parseResult, cancellationToken) =>
        {
            var inputSource = parseResult.GetValue(inputOption);
            if (string.IsNullOrWhiteSpace(inputSource))
            {
                throw CatalogCommandSupport.Usage(
                    "dependency set requires --input <file-or-dash>.");
            }

            var replacement = await CatalogCommandSupport.ReadInputModelAsync<Dependency>(
                jsonPayloads,
                console,
                inputSource,
                cancellationToken);

            var gameId = parseResult.GetValue(setGameIdArgument)!;
            var result = await CatalogCommandSupport.SaveMutationAsync(
                parseResult,
                projectContext,
                indexFiles,
                index => catalogWorkflow.UpsertDependency(index, gameId, replacement),
                "dependencySet",
                $"Saved dependency '{replacement.Id}' for '{gameId}'.",
                $"Dry run: would save dependency '{replacement.Id}' for '{gameId}'.",
                cancellationToken);

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var remove = new Command("remove", "Remove one dependency.");
        var removeGameIdArgument = new Argument<string>("game-id")
        {
            Description = "Game id."
        };
        var removeDependencyIdArgument = new Argument<string>("dependency-id")
        {
            Description = "Dependency id."
        };
        remove.Arguments.Add(removeGameIdArgument);
        remove.Arguments.Add(removeDependencyIdArgument);
        remove.SetAction(async (parseResult, cancellationToken) =>
        {
            var gameId = parseResult.GetValue(removeGameIdArgument)!;
            var dependencyId = parseResult.GetValue(removeDependencyIdArgument)!;

            var result = await CatalogCommandSupport.SaveMutationAsync(
                parseResult,
                projectContext,
                indexFiles,
                index => catalogWorkflow.RemoveDependency(index, gameId, dependencyId),
                "dependencyRemoved",
                $"Removed dependency '{dependencyId}' from '{gameId}'.",
                $"Dry run: would remove dependency '{dependencyId}' from '{gameId}'.",
                cancellationToken);

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var presets = new Command("presets", "List built-in dependency presets and any presets stored in the project.");
        presets.SetAction(async (parseResult, cancellationToken) =>
        {
            var project = await TryResolveProjectAsync(projectContext, parseResult, cancellationToken);

            var builtInPresets = DependencyPresetCatalog.All
                .Select(preset => new
                {
                    source = "builtIn",
                    id = preset.Id,
                    displayName = preset.DisplayName,
                    description = preset.Description,
                    dependency = preset.ToDependency()
                });

            var projectPresets = project?.Index.DependencyPresets.Select(preset => new
            {
                source = "project",
                id = preset.Id,
                displayName = preset.DisplayName,
                description = (string?)null,
                dependency = preset.Dependency
            }) ?? Enumerable.Empty<object>();

            var result = CatalogCommandSupport.Success(
                "dependencyPresetsListed",
                new
                {
                    projectPath = project?.ProjectPath,
                    pluginId = project?.Index.PluginId,
                    presets = builtInPresets.Cast<object>().Concat(projectPresets).ToArray()
                },
                project is null
                    ? $"Loaded {DependencyPresetCatalog.All.Count} built-in dependency preset(s)."
                    : $"Loaded {DependencyPresetCatalog.All.Count + project.Index.DependencyPresets.Count} dependency preset(s).");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var applyPreset = new Command("apply-preset", "Clone a preset dependency into a game.");
        var applyGameIdArgument = new Argument<string>("game-id")
        {
            Description = "Game id."
        };
        var applyPresetIdArgument = new Argument<string>("preset-id")
        {
            Description = "Preset id."
        };
        applyPreset.Arguments.Add(applyGameIdArgument);
        applyPreset.Arguments.Add(applyPresetIdArgument);
        applyPreset.SetAction(async (parseResult, cancellationToken) =>
        {
            var gameId = parseResult.GetValue(applyGameIdArgument)!;
            var presetId = parseResult.GetValue(applyPresetIdArgument)!;

            var result = await CatalogCommandSupport.SaveMutationAsync(
                parseResult,
                projectContext,
                indexFiles,
                index =>
                {
                    var dependencyModel = ResolvePresetDependency(index, presetId);
                    return catalogWorkflow.UpsertDependency(index, gameId, dependencyModel);
                },
                "dependencyPresetApplied",
                $"Applied preset '{presetId}' to '{gameId}'.",
                $"Dry run: would apply preset '{presetId}' to '{gameId}'.",
                cancellationToken);

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        dependency.Subcommands.Add(list);
        dependency.Subcommands.Add(show);
        dependency.Subcommands.Add(set);
        dependency.Subcommands.Add(remove);
        dependency.Subcommands.Add(presets);
        dependency.Subcommands.Add(applyPreset);
        root.Subcommands.Add(dependency);
    }

    private static Dependency ResolvePresetDependency(PluginRepoIndex index, string presetId)
    {
        var projectMatches = index.DependencyPresets
            .Where(preset => string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectMatches.Count > 1)
        {
            throw CatalogCommandSupport.Conflict(
                $"Project preset id '{presetId}' is ambiguous because multiple presets match it case-insensitively.");
        }

        if (projectMatches.Count == 1)
        {
            return projectMatches[0].Dependency;
        }

        if (DependencyPresetCatalog.TryGet(presetId, out var builtIn))
        {
            return builtIn.ToDependency();
        }

        throw CatalogCommandSupport.Validation($"Dependency preset '{presetId}' was not found.");
    }

    private static async Task<ResolvedAuthorProject?> TryResolveProjectAsync(
        AuthorProjectContext projectContext,
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CatalogCommandSupport.ResolveProjectAsync(projectContext, parseResult, cancellationToken);
        }
        catch (WorkflowException ex)
            when (ex.ErrorKind == WorkflowErrorKind.Validation &&
                  string.IsNullOrWhiteSpace(CatalogCommandSupport.GetProjectOption(parseResult)) &&
                  ex.Messages.Any(message => message.Contains("No author project could be resolved", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
    }
}
