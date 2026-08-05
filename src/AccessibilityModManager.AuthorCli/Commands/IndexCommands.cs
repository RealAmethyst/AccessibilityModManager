using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class IndexCommands
{
    public static void AddTo(RootCommand root, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(services);

        var writer = services.GetRequiredService<OutcomeWriter>();
        var console = services.GetRequiredService<ICliConsole>();
        var projects = services.GetRequiredService<AuthorProjectContext>();
        var payloads = services.GetRequiredService<JsonPayloadService>();
        var workflow = services.GetRequiredService<IIndexWorkflow>();
        var config = services.GetRequiredService<AuthorConfigService>();
        var registry = services.GetRequiredService<RegistryMembershipChecker>();

        var index = new Command("index", "Inspect, reconcile, save, or publish index.json.");

        var show = new Command("show", "Show the complete current index model.");
        show.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            return CatalogCommandSupport.Complete(
                writer,
                parseResult,
                Success(
                    "indexShown",
                    new { resolved.ProjectPath, index = resolved.Index },
                    $"Loaded index.json for '{resolved.Index.PluginId}'."));
        });

        var validate = new Command("validate", "Validate index.json exactly as the manager will.");
        validate.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var report = workflow.Validate(resolved.Index);
            if (report.PublishBlockers.Count > 0)
            {
                throw new WorkflowException(
                    WorkflowErrorKind.Validation,
                    "indexValidationFailed",
                    new[] { "The index cannot be published." }.Concat(report.PublishBlockers).ToArray());
            }

            return CatalogCommandSupport.Complete(
                writer,
                parseResult,
                Success(
                    "indexValid",
                    new { resolved.ProjectPath, resolved.Index.PluginId, report },
                    "The index is valid for publication."));
        });

        var reconcile = new Command("reconcile", "Compare the local catalog with the verified published catalog.");
        reconcile.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            if (CatalogCommandSupport.GetDryRun(parseResult))
            {
                var preview = await workflow.ReconcileAsync(resolved.ProjectPath, dryRun: true, cancellationToken);
                ThrowIfFailed(preview);
                return CatalogCommandSupport.Complete(writer, parseResult, preview);
            }

            await using var lease = await projects.AcquireWriteLeaseAsync(resolved.ProjectPath, cancellationToken);
            var result = await workflow.ReconcileAsync(resolved.ProjectPath, dryRun: false, cancellationToken);
            ThrowIfFailed(result);
            return CatalogCommandSupport.Complete(writer, parseResult, result);
        });

        var save = new Command("save", "Validate and durably save a complete index model.");
        var saveInput = new Option<string?>(CatalogCommandSupport.InputOptionName)
        {
            Description = "Path to a complete camelCase PluginRepoIndex JSON document, or - for standard input. Uses the current index when omitted."
        };
        save.Options.Add(saveInput);
        save.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var source = parseResult.GetValue(saveInput);
            var candidate = string.IsNullOrWhiteSpace(source)
                ? resolved.Index
                : await CatalogCommandSupport.ReadInputModelAsync<PluginRepoIndex>(
                    payloads,
                    console,
                    source,
                    cancellationToken);

            candidate = CatalogCommandSupport.StampGeneratedAt(candidate);
            if (CatalogCommandSupport.GetDryRun(parseResult))
            {
                var preview = await workflow.SaveAsync(resolved.ProjectPath, candidate, dryRun: true, cancellationToken);
                ThrowIfFailed(preview);
                return CatalogCommandSupport.Complete(writer, parseResult, preview);
            }

            await using var lease = await projects.AcquireWriteLeaseAsync(resolved.ProjectPath, cancellationToken);
            var result = await workflow.SaveAsync(resolved.ProjectPath, candidate, dryRun: false, cancellationToken);
            ThrowIfFailed(result);
            return CatalogCommandSupport.Complete(writer, parseResult, result);
        });

        var destination = new Command("destination", "Read or select the catalog publishing destination.");
        var destinationGet = new Command("get", "Show the saved publishing destination.");
        destinationGet.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var selected = config.GetPublishDestination(resolved.ProjectPath, resolved.Index.PluginId);
            return CatalogCommandSupport.Complete(
                writer,
                parseResult,
                Success(
                    "indexDestinationShown",
                    new { resolved.ProjectPath, resolved.Index.PluginId, destination = FormatDestination(selected) },
                    selected == PublishDestination.Unset
                        ? "No publishing destination is selected."
                        : $"The publishing destination is {FormatDestination(selected)}."));
        });

        var destinationSet = new Command("set", "Select github, server, or unset for this exact project and plugin id.");
        var destinationArgument = new Argument<string>("destination")
        {
            Description = "github, server, or unset."
        };
        destinationSet.Arguments.Add(destinationArgument);
        destinationSet.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var selected = ParseDestination(parseResult.GetValue(destinationArgument)!);
            if (!CatalogCommandSupport.GetDryRun(parseResult))
            {
                await using var lease = await projects.AcquireWriteLeaseAsync(resolved.ProjectPath, cancellationToken);
                config.RecordRecent(
                    resolved.ProjectPath,
                    CatalogCommandSupport.DefaultProjectDisplayName(resolved.ProjectPath));
                config.SetPublishDestination(resolved.ProjectPath, resolved.Index.PluginId, selected);
            }

            return CatalogCommandSupport.Complete(
                writer,
                parseResult,
                Success(
                    CatalogCommandSupport.GetDryRun(parseResult)
                        ? "indexDestinationPreviewed"
                        : "indexDestinationSet",
                    new
                    {
                        resolved.ProjectPath,
                        resolved.Index.PluginId,
                        destination = FormatDestination(selected),
                        dryRun = CatalogCommandSupport.GetDryRun(parseResult)
                    },
                    CatalogCommandSupport.GetDryRun(parseResult)
                        ? $"The destination would be set to {FormatDestination(selected)}."
                        : $"Set the destination to {FormatDestination(selected)}."));
        });
        destination.Subcommands.Add(destinationGet);
        destination.Subcommands.Add(destinationSet);

        var membership = new Command("membership", "Check this plugin in the signed public registry.");
        membership.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var result = await registry.CheckAsync(resolved.Index.PluginId, cancellationToken);
            if (!result.RegistryReachable)
            {
                throw new WorkflowException(
                    WorkflowErrorKind.Conflict,
                    "registryUnavailable",
                    new[] { result.Error ?? "The public registry could not be read." });
            }
            if (result.SignatureFailed)
            {
                throw new WorkflowException(
                    WorkflowErrorKind.Authentication,
                    "registrySignatureInvalid",
                    new[] { result.Error ?? "The public registry signature did not verify." });
            }

            return CatalogCommandSupport.Complete(
                writer,
                parseResult,
                Success(
                    "registryMembershipChecked",
                    new
                    {
                        resolved.Index.PluginId,
                        result.IsListed,
                        result.Entry,
                        registryUrl = RegistryMembershipChecker.RegistryUrl
                    },
                    result.IsListed
                        ? $"'{resolved.Index.PluginId}' is listed in the signed public registry."
                        : $"'{resolved.Index.PluginId}' is not listed in the signed public registry."));
        });

        var publish = new Command("publish", "Validate and publish index.json to the selected destination.");
        var commitMessage = new Option<string?>("--message")
        {
            Description = "Git commit message or server change summary."
        };
        publish.Options.Add(commitMessage);
        publish.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var selected = config.GetPublishDestination(resolved.ProjectPath, resolved.Index.PluginId);
            var request = new IndexPublishRequest(
                resolved.ProjectPath,
                resolved.Index,
                selected,
                parseResult.GetValue(commitMessage) ?? "Update accessibility mod index",
                CatalogCommandSupport.GetDryRun(parseResult));

            if (request.DryRun)
            {
                var preview = await workflow.PreviewPublishAsync(request, cancellationToken);
                ThrowIfFailed(preview);
                return CatalogCommandSupport.Complete(writer, parseResult, preview);
            }

            if (!CatalogCommandSupport.GetYes(parseResult))
            {
                var preview = await workflow.PreviewPublishAsync(request, cancellationToken);
                ThrowIfFailed(preview);
                throw new WorkflowException(
                    WorkflowErrorKind.Conflict,
                    "confirmationRequired",
                    new[] { $"Publishing requires --yes after reviewing this destination: {preview.Value!.DestinationDescription}." });
            }

            await using var lease = await projects.AcquireWriteLeaseAsync(resolved.ProjectPath, cancellationToken);
            var result = await workflow.PublishAsync(request, confirmed: true, cancellationToken);
            ThrowIfFailed(result);
            return CatalogCommandSupport.Complete(writer, parseResult, result);
        });

        var publishLock = new Command("lock", "Inspect or compare-and-break a server publishing lock.");
        var lockShow = new Command("show", "Show the current server publishing lock and fingerprint.");
        lockShow.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var result = await workflow.InspectLockAsync(resolved.Index.PluginId, cancellationToken);
            ThrowIfFailed(result);
            return CatalogCommandSupport.Complete(writer, parseResult, result);
        });

        var lockBreak = new Command("break", "Break only the exact server lock fingerprint previously displayed.");
        var fingerprint = new Option<string>("--fingerprint")
        {
            Description = "Exact fingerprint returned by index lock show.",
            Required = true
        };
        lockBreak.Options.Add(fingerprint);
        lockBreak.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var expected = parseResult.GetValue(fingerprint)!;
            if (CatalogCommandSupport.GetDryRun(parseResult))
            {
                var current = await workflow.InspectLockAsync(resolved.Index.PluginId, cancellationToken);
                ThrowIfFailed(current);
                if (!current.Value!.Present ||
                    !string.Equals(current.Value.Fingerprint, expected, StringComparison.Ordinal))
                {
                    throw new WorkflowException(
                        WorkflowErrorKind.Conflict,
                        "publishLockChanged",
                        new[] { "The publish lock does not match the supplied fingerprint, so it would not be removed." });
                }

                return CatalogCommandSupport.Complete(
                    writer,
                    parseResult,
                    Success(
                        "publishLockBreakPreviewed",
                        new { resolved.Index.PluginId, fingerprint = expected, dryRun = true },
                        "The exact displayed publish lock would be removed."));
            }

            var result = await workflow.BreakLockAsync(
                resolved.Index.PluginId,
                expected,
                CatalogCommandSupport.GetYes(parseResult),
                cancellationToken);
            ThrowIfFailed(result);
            return CatalogCommandSupport.Complete(writer, parseResult, result);
        });
        publishLock.Subcommands.Add(lockShow);
        publishLock.Subcommands.Add(lockBreak);

        index.Subcommands.Add(show);
        index.Subcommands.Add(validate);
        index.Subcommands.Add(reconcile);
        index.Subcommands.Add(save);
        index.Subcommands.Add(destination);
        index.Subcommands.Add(membership);
        index.Subcommands.Add(publish);
        index.Subcommands.Add(publishLock);
        root.Subcommands.Add(index);
    }

    private static PublishDestination ParseDestination(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "github" => PublishDestination.GitHub,
            "server" => PublishDestination.Server,
            "unset" or "none" => PublishDestination.Unset,
            _ => throw CatalogCommandSupport.Usage("Destination must be github, server, or unset.")
        };

    private static string FormatDestination(PublishDestination destination) =>
        destination switch
        {
            PublishDestination.GitHub => "github",
            PublishDestination.Server => "server",
            _ => "unset"
        };

    private static WorkflowResult<object> Success(string status, object? value, string message) =>
        new(status, value, new[] { message });

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
}
