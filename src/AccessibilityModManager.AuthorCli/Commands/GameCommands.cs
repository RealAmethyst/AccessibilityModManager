using System.CommandLine;
using System.CommandLine.Parsing;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class GameCommands
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

        var game = new Command("game", "Read or update games in the project index.");

        var list = new Command("list", "List games in the project.");
        list.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projectContext, parseResult, cancellationToken);
            var games = resolved.Index.Games
                .Select(entry => new
                {
                    id = entry.GameId,
                    displayName = entry.DisplayName,
                    modName = entry.ModName,
                    steamAppId = entry.SteamAppId,
                    exeName = entry.ExeName,
                    dependencyCount = entry.Dependencies.Count,
                    tagCount = entry.Tags.Count,
                    languageCount = entry.Languages.Count
                })
                .ToArray();

            var result = CatalogCommandSupport.Success(
                "gamesListed",
                new
                {
                    projectPath = resolved.ProjectPath,
                    pluginId = resolved.Index.PluginId,
                    games
                },
                $"Found {games.Length} game(s) in '{resolved.Index.PluginId}'.");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var show = new Command("show", "Show one game definition.");
        var showGameIdArgument = new Argument<string>("game-id")
        {
            Description = "Game id to show."
        };
        show.Arguments.Add(showGameIdArgument);
        show.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projectContext, parseResult, cancellationToken);
            var selectedGame = CatalogCommandSupport.FindGame(resolved.Index, parseResult.GetValue(showGameIdArgument)!);

            var result = CatalogCommandSupport.Success(
                "gameShown",
                new
                {
                    projectPath = resolved.ProjectPath,
                    pluginId = resolved.Index.PluginId,
                    game = selectedGame
                },
                $"Loaded game '{selectedGame.GameId}'.");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var add = new Command("add", "Add a new game definition.");
        var addInputOption = CreateInputOption();
        var addIdOption = CreateStringOption("--id", "Game id.");
        var addDisplayNameOption = CreateStringOption("--display-name", "Game display name.");
        var addModNameOption = CreateStringOption("--mod-name", "Displayed mod name.");
        var addDescriptionOption = CreateStringOption("--description", "Game description.");
        var addSteamAppIdOption = CreateStringOption("--steam-app-id", "Steam app id.");
        var addExeNameOption = CreateStringOption("--exe-name", "Primary executable name.");
        var addTagOption = CreateMultiStringOption("--tag", "Game tag. Repeat to add more than one.");
        var addLanguageOption = CreateMultiStringOption("--language", "Language code. Repeat to add more than one.");
        AddFieldOptions(add, addInputOption, addIdOption, addDisplayNameOption, addModNameOption, addDescriptionOption, addSteamAppIdOption, addExeNameOption, addTagOption, addLanguageOption);
        add.SetAction(async (parseResult, cancellationToken) =>
        {
            var inputSource = parseResult.GetValue(addInputOption);
            var idSpecified = CatalogCommandSupport.IsSpecified(parseResult, addIdOption);
            var displayNameSpecified = CatalogCommandSupport.IsSpecified(parseResult, addDisplayNameOption);
            var modNameSpecified = CatalogCommandSupport.IsSpecified(parseResult, addModNameOption);
            var descriptionSpecified = CatalogCommandSupport.IsSpecified(parseResult, addDescriptionOption);
            var steamAppIdSpecified = CatalogCommandSupport.IsSpecified(parseResult, addSteamAppIdOption);
            var exeNameSpecified = CatalogCommandSupport.IsSpecified(parseResult, addExeNameOption);
            var tagsSpecified = CatalogCommandSupport.IsSpecified(parseResult, addTagOption);
            var languagesSpecified = CatalogCommandSupport.IsSpecified(parseResult, addLanguageOption);

            CatalogCommandSupport.RejectMixedInput(
                inputSource,
                idSpecified,
                displayNameSpecified,
                modNameSpecified,
                descriptionSpecified,
                steamAppIdSpecified,
                exeNameSpecified,
                tagsSpecified,
                languagesSpecified);

            GameDefinition newGame;
            if (!string.IsNullOrWhiteSpace(inputSource))
            {
                newGame = await CatalogCommandSupport.ReadInputModelAsync<GameDefinition>(
                    jsonPayloads,
                    console,
                    inputSource,
                    cancellationToken);
            }
            else
            {
                newGame = BuildFlagDrivenGame(
                    parseResult.GetValue(addIdOption),
                    parseResult.GetValue(addDisplayNameOption),
                    modNameSpecified ? parseResult.GetValue(addModNameOption) : null,
                    descriptionSpecified ? parseResult.GetValue(addDescriptionOption) : null,
                    steamAppIdSpecified ? parseResult.GetValue(addSteamAppIdOption) : null,
                    exeNameSpecified ? parseResult.GetValue(addExeNameOption) : null,
                    parseResult.GetValue(addTagOption) ?? Array.Empty<string>(),
                    parseResult.GetValue(addLanguageOption) ?? Array.Empty<string>(),
                    idSpecified,
                    displayNameSpecified);
            }

            var result = await CatalogCommandSupport.SaveMutationAsync(
                parseResult,
                projectContext,
                indexFiles,
                index => catalogWorkflow.AddGame(index, newGame),
                "gameAdded",
                $"Added game '{newGame.GameId}'.",
                $"Dry run: would add game '{newGame.GameId}'.",
                cancellationToken);

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var update = new Command("update", "Replace or partially update a game definition.");
        var currentGameIdArgument = new Argument<string>("current-game-id")
        {
            Description = "Current game id."
        };
        var updateInputOption = CreateInputOption();
        var updateIdOption = CreateStringOption("--id", "New game id.");
        var updateDisplayNameOption = CreateStringOption("--display-name", "Game display name.");
        var updateModNameOption = CreateStringOption("--mod-name", "Displayed mod name.");
        var updateDescriptionOption = CreateStringOption("--description", "Game description.");
        var updateSteamAppIdOption = CreateStringOption("--steam-app-id", "Steam app id.");
        var updateExeNameOption = CreateStringOption("--exe-name", "Primary executable name.");
        var updateTagOption = CreateMultiStringOption("--tag", "Game tag. Repeat to replace the current set.");
        var updateLanguageOption = CreateMultiStringOption("--language", "Language code. Repeat to replace the current set.");
        var rewriteReleaseGameIdOption = new Option<bool>("--rewrite-release-game-id")
        {
            Description = "Rewrite embedded release GameId values when a rename would otherwise break them."
        };

        update.Arguments.Add(currentGameIdArgument);
        AddFieldOptions(update, updateInputOption, updateIdOption, updateDisplayNameOption, updateModNameOption, updateDescriptionOption, updateSteamAppIdOption, updateExeNameOption, updateTagOption, updateLanguageOption);
        update.Options.Add(rewriteReleaseGameIdOption);
        update.SetAction(async (parseResult, cancellationToken) =>
        {
            var currentGameId = parseResult.GetValue(currentGameIdArgument)!;
            var inputSource = parseResult.GetValue(updateInputOption);

            var idSpecified = CatalogCommandSupport.IsSpecified(parseResult, updateIdOption);
            var displayNameSpecified = CatalogCommandSupport.IsSpecified(parseResult, updateDisplayNameOption);
            var modNameSpecified = CatalogCommandSupport.IsSpecified(parseResult, updateModNameOption);
            var descriptionSpecified = CatalogCommandSupport.IsSpecified(parseResult, updateDescriptionOption);
            var steamAppIdSpecified = CatalogCommandSupport.IsSpecified(parseResult, updateSteamAppIdOption);
            var exeNameSpecified = CatalogCommandSupport.IsSpecified(parseResult, updateExeNameOption);
            var tagsSpecified = CatalogCommandSupport.IsSpecified(parseResult, updateTagOption);
            var languagesSpecified = CatalogCommandSupport.IsSpecified(parseResult, updateLanguageOption);

            CatalogCommandSupport.RejectMixedInput(
                inputSource,
                idSpecified,
                displayNameSpecified,
                modNameSpecified,
                descriptionSpecified,
                steamAppIdSpecified,
                exeNameSpecified,
                tagsSpecified,
                languagesSpecified);

            var rewriteReleaseGameIds = parseResult.GetValue(rewriteReleaseGameIdOption);
            GameDefinition? replacementFromInput = null;

            if (!string.IsNullOrWhiteSpace(inputSource))
            {
                replacementFromInput = await CatalogCommandSupport.ReadInputModelAsync<GameDefinition>(
                    jsonPayloads,
                    console,
                    inputSource,
                    cancellationToken);
            }
            else if (!(idSpecified || displayNameSpecified || modNameSpecified || descriptionSpecified || steamAppIdSpecified || exeNameSpecified || tagsSpecified || languagesSpecified))
            {
                throw CatalogCommandSupport.Usage(
                    "game update requires either --input <file-or-dash> or at least one field flag.");
            }

            var result = await CatalogCommandSupport.SaveMutationAsync(
                parseResult,
                projectContext,
                indexFiles,
                index =>
                {
                    var existing = CatalogCommandSupport.FindGame(index, currentGameId);
                    var replacement = replacementFromInput ?? BuildUpdatedGame(
                        existing,
                        parseResult.GetValue(updateIdOption),
                        parseResult.GetValue(updateDisplayNameOption),
                        modNameSpecified ? parseResult.GetValue(updateModNameOption) : null,
                        descriptionSpecified ? parseResult.GetValue(updateDescriptionOption) : null,
                        steamAppIdSpecified ? parseResult.GetValue(updateSteamAppIdOption) : null,
                        exeNameSpecified ? parseResult.GetValue(updateExeNameOption) : null,
                        parseResult.GetValue(updateTagOption) ?? Array.Empty<string>(),
                        parseResult.GetValue(updateLanguageOption) ?? Array.Empty<string>(),
                        idSpecified,
                        displayNameSpecified,
                        tagsSpecified,
                        languagesSpecified);

                    EnsureRenameRewriteChoice(
                        parseResult,
                        index,
                        currentGameId,
                        replacement,
                        rewriteReleaseGameIds);

                    return catalogWorkflow.UpdateGame(index, currentGameId, replacement, rewriteReleaseGameIds);
                },
                "gameUpdated",
                $"Updated game '{currentGameId}'.",
                $"Dry run: would update game '{currentGameId}'.",
                cancellationToken);

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var remove = new Command("remove", "Remove a game and its release bucket.");
        var removeGameIdArgument = new Argument<string>("game-id")
        {
            Description = "Game id to remove."
        };
        remove.Arguments.Add(removeGameIdArgument);
        remove.SetAction(async (parseResult, cancellationToken) =>
        {
            var gameId = parseResult.GetValue(removeGameIdArgument)!;
            CatalogCommandSupport.EnsureYes(
                parseResult,
                $"game remove is destructive. Re-run with --yes to remove '{gameId}'.");

            var result = await CatalogCommandSupport.SaveMutationAsync(
                parseResult,
                projectContext,
                indexFiles,
                index => catalogWorkflow.RemoveGame(index, gameId),
                "gameRemoved",
                $"Removed game '{gameId}'.",
                $"Dry run: would remove game '{gameId}'.",
                cancellationToken);

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        game.Subcommands.Add(list);
        game.Subcommands.Add(show);
        game.Subcommands.Add(add);
        game.Subcommands.Add(update);
        game.Subcommands.Add(remove);
        root.Subcommands.Add(game);
    }

    private static Option<string> CreateInputOption() =>
        new(CatalogCommandSupport.InputOptionName)
        {
            Description = "Path to a camelCase JSON file, or - for standard input."
        };

    private static Option<string> CreateStringOption(string name, string description) =>
        new(name)
        {
            Description = description
        };

    private static Option<string[]> CreateMultiStringOption(string name, string description) =>
        new(name)
        {
            Description = description
        };

    private static void AddFieldOptions(
        Command command,
        Option<string> inputOption,
        Option<string> idOption,
        Option<string> displayNameOption,
        Option<string> modNameOption,
        Option<string> descriptionOption,
        Option<string> steamAppIdOption,
        Option<string> exeNameOption,
        Option<string[]> tagOption,
        Option<string[]> languageOption)
    {
        command.Options.Add(inputOption);
        command.Options.Add(idOption);
        command.Options.Add(displayNameOption);
        command.Options.Add(modNameOption);
        command.Options.Add(descriptionOption);
        command.Options.Add(steamAppIdOption);
        command.Options.Add(exeNameOption);
        command.Options.Add(tagOption);
        command.Options.Add(languageOption);
    }

    private static GameDefinition BuildFlagDrivenGame(
        string? id,
        string? displayName,
        string? modName,
        string? description,
        string? steamAppId,
        string? exeName,
        IEnumerable<string> tags,
        IEnumerable<string> languages,
        bool idSpecified,
        bool displayNameSpecified)
    {
        if (!idSpecified || string.IsNullOrWhiteSpace(id))
        {
            throw CatalogCommandSupport.Usage("game add requires --id when --input is not used.");
        }

        if (!displayNameSpecified || string.IsNullOrWhiteSpace(displayName))
        {
            throw CatalogCommandSupport.Usage(
                "game add requires --display-name when --input is not used.");
        }

        return new GameDefinition
        {
            GameId = id.Trim(),
            DisplayName = displayName.Trim(),
            ModName = NormalizeOptionalText(modName),
            Description = NormalizeOptionalText(description),
            SteamAppId = NormalizeOptionalText(steamAppId),
            ExeName = NormalizeOptionalText(exeName),
            ProbeRules = new List<PathProbeRule>(),
            RegistryProbe = null,
            AsciiPathShim = null,
            Dependencies = new List<Dependency>(),
            Tags = tags.ToList(),
            Languages = languages.ToList(),
            DefaultPreInstall = null,
            DefaultPostInstall = null,
            DefaultPostUninstall = null
        };
    }

    private static GameDefinition BuildUpdatedGame(
        GameDefinition existing,
        string? newId,
        string? newDisplayName,
        string? modName,
        string? description,
        string? steamAppId,
        string? exeName,
        IEnumerable<string> tags,
        IEnumerable<string> languages,
        bool idSpecified,
        bool displayNameSpecified,
        bool tagsSpecified,
        bool languagesSpecified) =>
        new()
        {
            GameId = idSpecified
                ? RequireNonBlank(newId, "--id")
                : existing.GameId,
            DisplayName = displayNameSpecified
                ? RequireNonBlank(newDisplayName, "--display-name")
                : existing.DisplayName,
            ModName = modName is not null ? NormalizeOptionalText(modName) : existing.ModName,
            Description = description is not null ? NormalizeOptionalText(description) : existing.Description,
            SteamAppId = steamAppId is not null ? NormalizeOptionalText(steamAppId) : existing.SteamAppId,
            ExeName = exeName is not null ? NormalizeOptionalText(exeName) : existing.ExeName,
            ProbeRules = existing.ProbeRules,
            RegistryProbe = existing.RegistryProbe,
            AsciiPathShim = existing.AsciiPathShim,
            Dependencies = existing.Dependencies,
            Tags = tagsSpecified ? tags.ToList() : existing.Tags,
            Languages = languagesSpecified ? languages.ToList() : existing.Languages,
            DefaultPreInstall = existing.DefaultPreInstall,
            DefaultPostInstall = existing.DefaultPostInstall,
            DefaultPostUninstall = existing.DefaultPostUninstall
        };

    private static void EnsureRenameRewriteChoice(
        ParseResult parseResult,
        PluginRepoIndex index,
        string currentGameId,
        GameDefinition replacement,
        bool rewriteReleaseGameIds)
    {
        if (string.Equals(currentGameId, replacement.GameId, StringComparison.Ordinal))
        {
            return;
        }

        var releases = CatalogCommandSupport.GetReleasesForGame(index, currentGameId);
        if (releases.Count == 0)
        {
            return;
        }

        var previewItems = releases
            .Take(5)
            .Select(release => $"{release.GameId}/{release.Version}")
            .ToArray();

        var preview = string.Join(", ", previewItems);
        var remainder = releases.Count > previewItems.Length
            ? $" and {releases.Count - previewItems.Length} more"
            : string.Empty;

        if (!rewriteReleaseGameIds)
        {
            throw CatalogCommandSupport.Conflict(
                $"Renaming game '{currentGameId}' to '{replacement.GameId}' would rewrite {releases.Count} release(s): {preview}{remainder}. Re-run with --rewrite-release-game-id and --yes to make that durable.");
        }

        CatalogCommandSupport.EnsureYes(
            parseResult,
            $"Renaming game '{currentGameId}' to '{replacement.GameId}' rewrites {releases.Count} release game id value(s). Re-run with --yes to confirm that rewrite.");
    }

    private static string RequireNonBlank(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw CatalogCommandSupport.Usage($"{optionName} requires a non-blank value.");
        }

        return value.Trim();
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
