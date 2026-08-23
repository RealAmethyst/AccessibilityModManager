using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class ScriptCommands
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

        var script = new Command("script", "Read or update default lifecycle scripts on a game.");

        var show = new Command("show", "Show one lifecycle script slot.");
        var showGameIdArgument = new Argument<string>("game-id")
        {
            Description = "Game id."
        };
        var showSlotArgument = new Argument<string>("slot")
        {
            Description = "Lifecycle slot: pre-install, post-install, or post-uninstall."
        };
        show.Arguments.Add(showGameIdArgument);
        show.Arguments.Add(showSlotArgument);
        show.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projectContext, parseResult, cancellationToken);
            var game = CatalogCommandSupport.FindGame(resolved.Index, parseResult.GetValue(showGameIdArgument)!);
            var slot = CatalogCommandSupport.ParseSlot(parseResult.GetValue(showSlotArgument)!);
            var selectedScript = GetSlot(game, slot);

            var result = CatalogCommandSupport.Success(
                "scriptShown",
                new
                {
                    projectPath = resolved.ProjectPath,
                    pluginId = resolved.Index.PluginId,
                    gameId = game.GameId,
                    slot = ToToken(slot),
                    script = selectedScript
                },
                selectedScript is null
                    ? $"Game '{game.GameId}' has no {ToToken(slot)} script."
                    : $"Loaded the {ToToken(slot)} script for '{game.GameId}'.");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var set = new Command("set", "Replace one lifecycle script slot from a camelCase JSON document.");
        var setGameIdArgument = new Argument<string>("game-id")
        {
            Description = "Game id."
        };
        var setSlotArgument = new Argument<string>("slot")
        {
            Description = "Lifecycle slot: pre-install, post-install, or post-uninstall."
        };
        var inputOption = new Option<string>(CatalogCommandSupport.InputOptionName)
        {
            Description = "Path to a camelCase JSON file, or - for standard input."
        };
        set.Arguments.Add(setGameIdArgument);
        set.Arguments.Add(setSlotArgument);
        set.Options.Add(inputOption);
        set.SetAction(async (parseResult, cancellationToken) =>
        {
            var inputSource = parseResult.GetValue(inputOption);
            if (string.IsNullOrWhiteSpace(inputSource))
            {
                throw CatalogCommandSupport.Usage(
                    "script set requires --input <file-or-dash>.");
            }

            var gameId = parseResult.GetValue(setGameIdArgument)!;
            var slot = CatalogCommandSupport.ParseSlot(parseResult.GetValue(setSlotArgument)!);
            var scriptModel = await CatalogCommandSupport.ReadInputModelAsync<LifecycleScript>(
                jsonPayloads,
                console,
                inputSource,
                cancellationToken);

            var result = await CatalogCommandSupport.SaveMutationAsync(
                parseResult,
                projectContext,
                indexFiles,
                index => catalogWorkflow.SetLifecycleScript(index, gameId, slot, scriptModel),
                "scriptSet",
                $"Saved the {ToToken(slot)} script for '{gameId}'.",
                $"Dry run: would save the {ToToken(slot)} script for '{gameId}'.",
                cancellationToken);

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var clear = new Command("clear", "Remove one lifecycle script slot.");
        var clearGameIdArgument = new Argument<string>("game-id")
        {
            Description = "Game id."
        };
        var clearSlotArgument = new Argument<string>("slot")
        {
            Description = "Lifecycle slot: pre-install, post-install, or post-uninstall."
        };
        clear.Arguments.Add(clearGameIdArgument);
        clear.Arguments.Add(clearSlotArgument);
        clear.SetAction(async (parseResult, cancellationToken) =>
        {
            var gameId = parseResult.GetValue(clearGameIdArgument)!;
            var slot = CatalogCommandSupport.ParseSlot(parseResult.GetValue(clearSlotArgument)!);

            var result = await CatalogCommandSupport.SaveMutationAsync(
                parseResult,
                projectContext,
                indexFiles,
                index => catalogWorkflow.ClearLifecycleScript(index, gameId, slot),
                "scriptCleared",
                $"Cleared the {ToToken(slot)} script for '{gameId}'.",
                $"Dry run: would clear the {ToToken(slot)} script for '{gameId}'.",
                cancellationToken);

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        script.Subcommands.Add(show);
        script.Subcommands.Add(set);
        script.Subcommands.Add(clear);
        return script;
    }

    private static LifecycleScript? GetSlot(GameDefinition game, LifecycleSlot slot) =>
        slot switch
        {
            LifecycleSlot.PreInstall => game.DefaultPreInstall,
            LifecycleSlot.PostInstall => game.DefaultPostInstall,
            LifecycleSlot.PostUninstall => game.DefaultPostUninstall,
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null)
        };

    private static string ToToken(LifecycleSlot slot) =>
        slot switch
        {
            LifecycleSlot.PreInstall => "pre-install",
            LifecycleSlot.PostInstall => "post-install",
            LifecycleSlot.PostUninstall => "post-uninstall",
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null)
        };
}
