using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class IndexCommands
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var writer = services.GetRequiredService<OutcomeWriter>();
        var console = services.GetRequiredService<ICliConsole>();
        var projects = services.GetRequiredService<AuthorProjectContext>();
        var payloads = services.GetRequiredService<JsonPayloadService>();
        var workflows = services.GetRequiredService<AuthoringWorkflowFacade>();
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
            var report = workflows.ValidateIndex(resolved.Index);
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
                var preview = await workflows.ReconcileIndexAsync(resolved.ProjectPath, dryRun: true, cancellationToken);
                ThrowIfFailed(preview);
                return CatalogCommandSupport.Complete(writer, parseResult, preview);
            }

            var result = await workflows.ReconcileIndexAsync(
                resolved.ProjectPath,
                dryRun: false,
                confirmAdoption: CatalogCommandSupport.GetYes(parseResult),
                cancellationToken);
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
                var preview = await workflows.SaveIndexAsync(resolved.ProjectPath, candidate, dryRun: true, cancellationToken);
                ThrowIfFailed(preview);
                return CatalogCommandSupport.Complete(writer, parseResult, preview);
            }

            var result = await workflows.SaveIndexAsync(resolved.ProjectPath, candidate, dryRun: false, cancellationToken);
            ThrowIfFailed(result);
            return CatalogCommandSupport.Complete(writer, parseResult, result);
        });

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

        var publish = new Command("publish", "Validate and publish index.json to the GitHub repository.");
        var commitMessage = new Option<string?>("--message")
        {
            Description = "Git commit message."
        };
        publish.Options.Add(commitMessage);
        publish.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var selected = PublishDestination.GitHub;
            var request = new IndexPublishRequest(
                resolved.ProjectPath,
                resolved.Index,
                selected,
                parseResult.GetValue(commitMessage) ?? "Update accessibility mod index",
                CatalogCommandSupport.GetDryRun(parseResult));

            if (request.DryRun)
            {
                var preview = await workflows.PreviewIndexPublicationAsync(request, cancellationToken);
                ThrowIfFailed(preview);
                return CatalogCommandSupport.Complete(writer, parseResult, preview);
            }

            if (!CatalogCommandSupport.GetYes(parseResult))
            {
                var preview = await workflows.PreviewIndexPublicationAsync(request, cancellationToken);
                ThrowIfFailed(preview);
                throw new WorkflowException(
                    WorkflowErrorKind.Conflict,
                    "confirmationRequired",
                    new[] { $"Publishing requires --yes after reviewing this destination: {preview.Value!.DestinationDescription}." });
            }

            var result = await workflows.PublishIndexAsync(request, confirmed: true, cancellationToken);
            ThrowIfFailed(result);
            return CatalogCommandSupport.Complete(writer, parseResult, result);
        });

        index.Subcommands.Add(show);
        index.Subcommands.Add(validate);
        index.Subcommands.Add(reconcile);
        index.Subcommands.Add(save);
        index.Subcommands.Add(membership);
        index.Subcommands.Add(publish);
        return index;
    }

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
