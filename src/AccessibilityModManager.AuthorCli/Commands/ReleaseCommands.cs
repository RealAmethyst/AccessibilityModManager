using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class ReleaseCommands
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var writer = services.GetRequiredService<OutcomeWriter>();
        var console = services.GetRequiredService<ICliConsole>();
        var projects = services.GetRequiredService<AuthorProjectContext>();
        var indexFiles = services.GetRequiredService<IndexFileService>();
        var payloads = services.GetRequiredService<JsonPayloadService>();
        var catalog = services.GetRequiredService<CatalogWorkflow>();
        var workflows = services.GetRequiredService<AuthoringWorkflowFacade>();
        var config = services.GetRequiredService<AuthorConfigService>();

        var release = new Command("release", "Read, edit, upload, or publish mod releases.");

        var list = new Command("list", "List releases for one game.");
        var listGame = GameArgument();
        list.Arguments.Add(listGame);
        list.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var gameId = parseResult.GetValue(listGame)!;
            CatalogCommandSupport.FindGame(resolved.Index, gameId);
            var values = CatalogCommandSupport.GetReleasesForGame(resolved.Index, gameId);
            return CatalogCommandSupport.Complete(
                writer,
                parseResult,
                CatalogCommandSupport.Success(
                    "releasesListed",
                    new { resolved.ProjectPath, resolved.Index.PluginId, gameId, releases = values },
                    $"Found {values.Count} release(s) for '{gameId}'."));
        });

        var show = new Command("show", "Show a release by version and channel.");
        var showGame = GameArgument();
        var showVersion = VersionArgument();
        var showChannel = ChannelArgument();
        show.Arguments.Add(showGame);
        show.Arguments.Add(showVersion);
        show.Arguments.Add(showChannel);
        show.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var selected = FindRelease(
                resolved.Index,
                parseResult.GetValue(showGame)!,
                parseResult.GetValue(showVersion)!,
                parseResult.GetValue(showChannel)!);
            return CatalogCommandSupport.Complete(
                writer,
                parseResult,
                CatalogCommandSupport.Success("releaseShown", new { release = selected }, $"Loaded release {selected.Version} ({selected.Channel})."));
        });

        var add = new Command("add", "Add or replace a release record from camelCase JSON.");
        var addGame = GameArgument();
        var addInput = RequiredInputOption();
        add.Arguments.Add(addGame);
        add.Options.Add(addInput);
        add.SetAction(async (parseResult, cancellationToken) =>
        {
            var model = await CatalogCommandSupport.ReadInputModelAsync<ModRelease>(
                payloads,
                console,
                parseResult.GetValue(addInput)!,
                cancellationToken);
            var result = await CatalogCommandSupport.SaveMutationAsync(
                parseResult,
                projects,
                indexFiles,
                index => catalog.AddRelease(index, parseResult.GetValue(addGame)!, model),
                "releaseAdded",
                $"Saved release {model.Version} ({model.Channel}).",
                $"Release {model.Version} ({model.Channel}) is valid and would be saved.",
                cancellationToken);
            return CatalogCommandSupport.Complete(writer, parseResult, result);
        });

        var edit = new Command("edit", "Replace a release record while tracking its original identity.");
        var editGame = GameArgument();
        var editVersion = VersionArgument("current-version");
        var editChannel = ChannelArgument("current-channel");
        var editInput = RequiredInputOption();
        edit.Arguments.Add(editGame);
        edit.Arguments.Add(editVersion);
        edit.Arguments.Add(editChannel);
        edit.Options.Add(editInput);
        edit.SetAction(async (parseResult, cancellationToken) =>
        {
            var model = await CatalogCommandSupport.ReadInputModelAsync<ModRelease>(
                payloads,
                console,
                parseResult.GetValue(editInput)!,
                cancellationToken);
            var result = await CatalogCommandSupport.SaveMutationAsync(
                parseResult,
                projects,
                indexFiles,
                index => catalog.EditRelease(
                    index,
                    parseResult.GetValue(editGame)!,
                    parseResult.GetValue(editVersion)!,
                    parseResult.GetValue(editChannel)!,
                    model),
                "releaseEdited",
                $"Updated release to {model.Version} ({model.Channel}).",
                $"Release edit to {model.Version} ({model.Channel}) is valid and would be saved.",
                cancellationToken);
            return CatalogCommandSupport.Complete(writer, parseResult, result);
        });

        var remove = new Command("remove", "Remove a release record.");
        var removeGame = GameArgument();
        var removeVersion = VersionArgument();
        var removeChannel = ChannelArgument();
        remove.Arguments.Add(removeGame);
        remove.Arguments.Add(removeVersion);
        remove.Arguments.Add(removeChannel);
        remove.SetAction(async (parseResult, cancellationToken) =>
        {
            CatalogCommandSupport.EnsureYes(
                parseResult,
                "Removing a release requires --yes after reviewing the game, version, and channel.");
            var gameId = parseResult.GetValue(removeGame)!;
            var version = parseResult.GetValue(removeVersion)!;
            var channel = parseResult.GetValue(removeChannel)!;
            var result = await CatalogCommandSupport.SaveMutationAsync(
                parseResult,
                projects,
                indexFiles,
                index => catalog.RemoveRelease(index, gameId, version, channel),
                "releaseRemoved",
                $"Removed release {version} ({channel}) from '{gameId}'.",
                $"Release {version} ({channel}) would be removed from '{gameId}'.",
                cancellationToken);
            return CatalogCommandSupport.Complete(writer, parseResult, result);
        });

        var upload = CreateUploadCommand("upload", "Validate and upload a package without changing index.json.");
        upload.SetAction(async (parseResult, cancellationToken) =>
        {
            var request = await BuildPublishRequestAsync(
                parseResult,
                projects,
                config,
                payloads,
                console,
                ReleaseAssetDestination.GitHub,
                patreonGateSource: null,
                cancellationToken);
            if (CatalogCommandSupport.GetDryRun(parseResult))
            {
                var previewResult = await workflows.PreviewReleaseAsync(request, cancellationToken);
                ThrowIfFailed(previewResult);
                return CatalogCommandSupport.Complete(writer, parseResult, previewResult);
            }

            var preparedResult = await workflows.PrepareReleaseAsync(request, cancellationToken);
            ThrowIfFailed(preparedResult);
            await using var prepared = preparedResult.Value!;
            var published = await workflows.PublishReleaseAsync(
                prepared,
                request,
                CatalogCommandSupport.GetYes(parseResult),
                cancellationToken);
            ThrowIfFailed(published);
            config.SetGameSourceRepo(request.ProjectPath, request.GameId, request.SourceRepo);
            return CatalogCommandSupport.Complete(writer, parseResult, published);
        });

        var publish = CreateUploadCommand(
            "publish",
            "Run the complete upload, catalog-save, and index-publication transaction.");
        var indexMessage = new Option<string?>("--index-message")
        {
            Description = "Git commit message or server change summary for index publication."
        };
        publish.Options.Add(indexMessage);
        var assetDestination = new Option<string?>("--asset-destination")
        {
            Description = "Package destination: github (default), server, or patreon-post."
        };
        var patreonGate = new Option<string?>("--patreon-gate")
        {
            Description = "Path to a complete camelCase PatreonGate JSON document, or - for standard input."
        };
        var patreonAttachment = new Option<string?>("--patreon-attachment")
        {
            Description = "Stable attachment selection id returned by 'patreon post validate'."
        };
        publish.Options.Add(assetDestination);
        publish.Options.Add(patreonGate);
        publish.Options.Add(patreonAttachment);
        publish.SetAction(async (parseResult, cancellationToken) =>
        {
            var selectedAssetDestination = ParseAssetDestination(parseResult.GetValue(assetDestination));
            var gateSource = parseResult.GetValue(patreonGate);
            var request = await BuildPublishRequestAsync(
                parseResult,
                projects,
                config,
                payloads,
                console,
                selectedAssetDestination,
                gateSource,
                cancellationToken);
            var destination = config.GetPublishDestination(request.ProjectPath, request.PluginId);
            var completeRequest = new CompleteReleasePublishRequest(
                request,
                destination,
                parseResult.GetValue(indexMessage) ?? $"Publish {request.GameId} {request.Version}",
                CatalogCommandSupport.GetDryRun(parseResult),
                selectedAssetDestination,
                parseResult.GetValue(patreonAttachment));

            if (completeRequest.DryRun)
            {
                var preview = await workflows.PreviewCompleteReleaseAsync(completeRequest, cancellationToken);
                ThrowIfFailed(preview);
                return CatalogCommandSupport.Complete(writer, parseResult, preview);
            }

            if (!CatalogCommandSupport.GetYes(parseResult))
            {
                var preview = await workflows.PreviewCompleteReleaseAsync(completeRequest, cancellationToken);
                ThrowIfFailed(preview);
                throw new WorkflowException(
                    WorkflowErrorKind.Conflict,
                    "confirmationRequired",
                    new[]
                    {
                        $"Complete publication requires --yes after reviewing package destination {preview.Value!.Release.DestinationDescription} and catalog destination {preview.Value.Index.DestinationDescription}."
                    });
            }

            var result = await workflows.PublishCompleteReleaseAsync(
                completeRequest,
                confirmed: true,
                cancellationToken);
            ThrowIfFailed(result);
            if (selectedAssetDestination == ReleaseAssetDestination.GitHub)
                config.SetGameSourceRepo(request.ProjectPath, request.GameId, request.SourceRepo);
            return CatalogCommandSupport.Complete(writer, parseResult, result);
        });

        release.Subcommands.Add(list);
        release.Subcommands.Add(show);
        release.Subcommands.Add(add);
        release.Subcommands.Add(edit);
        release.Subcommands.Add(remove);
        release.Subcommands.Add(upload);
        release.Subcommands.Add(publish);
        return release;
    }

    private static Command CreateUploadCommand(string name, string description)
    {
        var command = new Command(name, description);
        command.Options.Add(RequiredOption("--game", "Game id from the project index."));
        command.Options.Add(RequiredOption("--version", "Release version."));
        command.Options.Add(RequiredOption("--channel", "Release channel, such as stable or beta."));
        command.Options.Add(new Option<string>("--repo") { Description = "GitHub repository in owner/name form. Uses the saved per-game repository when omitted." });
        command.Options.Add(RequiredOption("--zip", "Wrapped package ZIP to upload."));
        command.Options.Add(new Option<string>("--asset-name") { Description = "Published asset filename. Defaults to the ZIP's filename." });
        command.Options.Add(new Option<string>("--notes") { Description = "Release notes." });
        command.Options.Add(new Option<string>("--changelog-url") { Description = "HTTPS changelog URL." });
        return command;
    }

    private static async Task<ReleasePublishRequest> BuildPublishRequestAsync(
        ParseResult parseResult,
        AuthorProjectContext projects,
        AuthorConfigService config,
        JsonPayloadService payloads,
        ICliConsole console,
        ReleaseAssetDestination assetDestination,
        string? patreonGateSource,
        CancellationToken cancellationToken)
    {
        var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
        var gameId = parseResult.GetValue<string>("--game")!;
        var game = CatalogCommandSupport.FindGame(resolved.Index, gameId);
        var repo = parseResult.GetValue<string>("--repo")
                   ?? config.GetGameSourceRepo(resolved.ProjectPath, game.GameId);
        if (assetDestination == ReleaseAssetDestination.GitHub && string.IsNullOrWhiteSpace(repo))
        {
            throw CatalogCommandSupport.Validation(
                $"No GitHub repository is set for '{game.GameId}'. Pass --repo owner/name or save the per-game source repository first.");
        }

        PatreonGate? gate = null;
        if (!string.IsNullOrWhiteSpace(patreonGateSource))
        {
            gate = await payloads.ReadAsync<PatreonGate>(
                patreonGateSource,
                console.In,
                cancellationToken);
        }
        if (assetDestination == ReleaseAssetDestination.GitHub && gate is not null)
        {
            throw CatalogCommandSupport.Validation(
                "Patreon-gated bytes cannot be published to a public GitHub release.");
        }
        if (assetDestination == ReleaseAssetDestination.PatreonPost && gate is null)
        {
            throw CatalogCommandSupport.Validation(
                "Patreon-post delivery requires --patreon-gate with campaign, tier, and post metadata.");
        }

        return new ReleasePublishRequest(
            resolved.ProjectPath,
            resolved.Index.PluginId,
            game.GameId,
            parseResult.GetValue<string>("--version")!,
            parseResult.GetValue<string>("--channel")!,
            assetDestination == ReleaseAssetDestination.GitHub
                ? CatalogCommandSupport.NormalizeGitHubRepo(repo!)
                : string.Empty,
            parseResult.GetValue<string>("--zip")!,
            parseResult.GetValue<string>("--asset-name"),
            parseResult.GetValue<string>("--notes"),
            parseResult.GetValue<string>("--changelog-url"),
            Patreon: gate);
    }

    private static ReleaseAssetDestination ParseAssetDestination(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "github" => ReleaseAssetDestination.GitHub,
            "server" => ReleaseAssetDestination.Server,
            "patreon" or "patreon-post" => ReleaseAssetDestination.PatreonPost,
            _ => throw CatalogCommandSupport.Validation(
                "Asset destination must be github, server, or patreon-post.")
        };

    private static ModRelease FindRelease(PluginRepoIndex index, string gameId, string version, string channel)
    {
        CatalogCommandSupport.FindGame(index, gameId);
        var matches = CatalogCommandSupport.GetReleasesForGame(index, gameId)
            .Where(candidate =>
                string.Equals(candidate.Version, version, StringComparison.Ordinal) &&
                string.Equals(candidate.Channel, channel, StringComparison.Ordinal))
            .ToList();
        return matches.Count switch
        {
            0 => throw CatalogCommandSupport.Validation($"Release {version} ({channel}) was not found for '{gameId}'."),
            1 => matches[0],
            _ => throw CatalogCommandSupport.Conflict($"Multiple releases use identity {version} ({channel}) for '{gameId}'.")
        };
    }

    private static void ThrowIfFailed<T>(WorkflowResult<T> result)
    {
        if (result.ErrorKind == WorkflowErrorKind.None)
            return;
        throw new WorkflowException(
            result.ErrorKind,
            result.Status,
            result.Messages,
            result.CompletedPhases);
    }

    private static Argument<string> GameArgument() => new("game-id") { Description = "Game id." };
    private static Argument<string> VersionArgument(string name = "version") => new(name) { Description = "Release version." };
    private static Argument<string> ChannelArgument(string name = "channel") => new(name) { Description = "Release channel." };

    private static Option<string> RequiredInputOption() =>
        new(CatalogCommandSupport.InputOptionName)
        {
            Description = "Path to a complete camelCase ModRelease JSON document, or - for standard input.",
            Required = true
        };

    private static Option<string> RequiredOption(string name, string description) =>
        new(name) { Description = description, Required = true };
}
