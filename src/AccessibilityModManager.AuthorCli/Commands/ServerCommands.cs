using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class ServerCommands
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var writer = services.GetRequiredService<OutcomeWriter>();
        var console = services.GetRequiredService<ICliConsole>();
        var payloads = services.GetRequiredService<JsonPayloadService>();
        var projects = services.GetRequiredService<AuthorProjectContext>();
        var workflow = services.GetRequiredService<IServerWorkflow>();
        var server = new Command("server", "Configure and operate the AuthorTool SFTP publishing server.");

        var status = new Command("status", "Show the saved server configuration without exposing its passphrase.");
        status.SetAction(parseResult => Complete(writer, parseResult, workflow.GetStatus()));

        var configure = new Command("configure", "Validate and save a complete server configuration.");
        var configureInput = RequiredInput("A camelCase ServerUploadConfig JSON document. KeyPassphrase must be empty.");
        var passphraseStdin = new Option<bool>("--passphrase-stdin")
        {
            Description = "Read the SSH private-key passphrase from one redirected standard-input line."
        };
        configure.Options.Add(configureInput);
        configure.Options.Add(passphraseStdin);
        configure.SetAction(async (parseResult, cancellationToken) =>
        {
            var model = await CatalogCommandSupport.ReadInputModelAsync<ServerUploadConfig>(
                payloads,
                console,
                parseResult.GetValue(configureInput)!,
                cancellationToken);
            if (!string.IsNullOrEmpty(model.KeyPassphrase))
            {
                throw CatalogCommandSupport.Usage(
                    "Don't put the SSH passphrase in JSON. Leave keyPassphrase empty and use concealed input or --passphrase-stdin.");
            }

            string passphrase;
            if (parseResult.GetValue(passphraseStdin))
            {
                if (!console.IsInputRedirected)
                    throw CatalogCommandSupport.Usage("--passphrase-stdin requires redirected standard input.");
                passphrase = await SecretReader.ReadAsync(console, cancellationToken);
            }
            else if (!console.IsInputRedirected)
            {
                console.WriteStatus("SSH private-key passphrase; press Enter if the key has none:");
                passphrase = await SecretReader.ReadAsync(console, cancellationToken);
            }
            else
            {
                passphrase = string.Empty;
            }

            return Complete(
                writer,
                parseResult,
                workflow.Configure(
                    new ServerConfigurationInput(model, passphrase),
                    CatalogCommandSupport.GetDryRun(parseResult)));
        });

        var clear = new Command("clear", "Remove the saved server configuration.");
        clear.SetAction(parseResult => Complete(
            writer,
            parseResult,
            workflow.Clear(
                CatalogCommandSupport.GetYes(parseResult),
                CatalogCommandSupport.GetDryRun(parseResult))));

        var test = new Command("test", "Connect using the pinned host key and verify writable paths.");
        test.SetAction(async (parseResult, cancellationToken) =>
            Complete(writer, parseResult, await workflow.TestAsync(cancellationToken)));

        var selfTest = new Command("self-test", "Exercise publish locking, SFTP read-back, and a non-live rehearsal.");
        selfTest.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            return Complete(
                writer,
                parseResult,
                await workflow.SelfTestAsync(resolved.Index.PluginId, cancellationToken));
        });

        var release = new Command("release", "Inspect or upload immutable server-hosted packages.");
        var releaseInspect = CreateReleaseCommand("inspect", "Inspect a version folder using the exact validated package bytes.");
        releaseInspect.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var request = await BuildReleaseRequestAsync(parseResult, payloads, console, cancellationToken);
            return Complete(
                writer,
                parseResult,
                await workflow.InspectReleaseAsync(resolved.Index.PluginId, request, cancellationToken));
        });

        var releaseUpload = CreateReleaseCommand("upload", "Upload the exact staged package and optional Patreon gate.");
        releaseUpload.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var request = await BuildReleaseRequestAsync(parseResult, payloads, console, cancellationToken);
            return Complete(
                writer,
                parseResult,
                await workflow.UploadReleaseAsync(
                    resolved.Index.PluginId,
                    request,
                    CatalogCommandSupport.GetYes(parseResult),
                    CatalogCommandSupport.GetDryRun(parseResult),
                    cancellationToken));
        });
        release.Subcommands.Add(releaseInspect);
        release.Subcommands.Add(releaseUpload);

        var gate = new Command("gate", "Update or remove the Patreon gate on an already-published version.");
        var gateSet = new Command("set", "Replace the campaign and tier ids enforced by the server.");
        var gateSetGame = RequiredOption("--game", "Game id.");
        var gateSetVersion = RequiredOption("--version", "Release version.");
        var gateInput = RequiredInput("A camelCase PatreonGate JSON document.");
        gateSet.Options.Add(gateSetGame);
        gateSet.Options.Add(gateSetVersion);
        gateSet.Options.Add(gateInput);
        gateSet.SetAction(async (parseResult, cancellationToken) =>
        {
            var model = await CatalogCommandSupport.ReadInputModelAsync<PatreonGate>(
                payloads,
                console,
                parseResult.GetValue(gateInput)!,
                cancellationToken);
            return Complete(
                writer,
                parseResult,
                await workflow.SetGateAsync(
                    parseResult.GetValue(gateSetGame)!,
                    parseResult.GetValue(gateSetVersion)!,
                    model,
                    CatalogCommandSupport.GetYes(parseResult),
                    CatalogCommandSupport.GetDryRun(parseResult),
                    cancellationToken));
        });

        var gateRemove = new Command("remove", "Remove the gate and make an already-cataloged version public.");
        var gateRemoveGame = RequiredOption("--game", "Game id.");
        var gateRemoveVersion = RequiredOption("--version", "Release version.");
        gateRemove.Options.Add(gateRemoveGame);
        gateRemove.Options.Add(gateRemoveVersion);
        gateRemove.SetAction(async (parseResult, cancellationToken) =>
            Complete(
                writer,
                parseResult,
                await workflow.RemoveGateAsync(
                    parseResult.GetValue(gateRemoveGame)!,
                    parseResult.GetValue(gateRemoveVersion)!,
                    CatalogCommandSupport.GetYes(parseResult),
                    CatalogCommandSupport.GetDryRun(parseResult),
                    cancellationToken)));
        gate.Subcommands.Add(gateSet);
        gate.Subcommands.Add(gateRemove);

        var publishLock = new Command("lock", "Inspect or compare-and-break the server publish lock.");
        var lockShow = new Command("show", "Display the lock and its exact fingerprint.");
        lockShow.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            return Complete(
                writer,
                parseResult,
                await workflow.InspectLockAsync(resolved.Index.PluginId, cancellationToken));
        });

        var lockBreak = new Command("break", "Remove only the exact lock fingerprint previously displayed.");
        var fingerprint = RequiredOption("--fingerprint", "Exact lock fingerprint from server lock show.");
        lockBreak.Options.Add(fingerprint);
        lockBreak.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            return Complete(
                writer,
                parseResult,
                await workflow.BreakLockAsync(
                    resolved.Index.PluginId,
                    parseResult.GetValue(fingerprint)!,
                    CatalogCommandSupport.GetYes(parseResult),
                    CatalogCommandSupport.GetDryRun(parseResult),
                    cancellationToken));
        });
        publishLock.Subcommands.Add(lockShow);
        publishLock.Subcommands.Add(lockBreak);

        server.Subcommands.Add(status);
        server.Subcommands.Add(configure);
        server.Subcommands.Add(clear);
        server.Subcommands.Add(test);
        server.Subcommands.Add(selfTest);
        server.Subcommands.Add(release);
        server.Subcommands.Add(gate);
        server.Subcommands.Add(publishLock);
        return server;
    }

    private static Command CreateReleaseCommand(string name, string description)
    {
        var command = new Command(name, description);
        command.Options.Add(RequiredOption("--game", "Game id."));
        command.Options.Add(RequiredOption("--version", "Release version."));
        command.Options.Add(RequiredOption("--zip", "Wrapped package ZIP."));
        command.Options.Add(new Option<string?>("--asset-name")
        {
            Description = "Published filename. Defaults to the ZIP filename."
        });
        command.Options.Add(new Option<string?>("--gate-input")
        {
            Description = "Optional camelCase PatreonGate JSON document."
        });
        return command;
    }

    private static async Task<ServerReleaseRequest> BuildReleaseRequestAsync(
        ParseResult parseResult,
        JsonPayloadService payloads,
        ICliConsole console,
        CancellationToken cancellationToken)
    {
        var zip = Path.GetFullPath(parseResult.GetValue<string>("--zip")!);
        var gateSource = parseResult.GetValue<string?>("--gate-input");
        var gate = string.IsNullOrWhiteSpace(gateSource)
            ? null
            : await CatalogCommandSupport.ReadInputModelAsync<PatreonGate>(
                payloads,
                console,
                gateSource,
                cancellationToken);
        return new ServerReleaseRequest(
            parseResult.GetValue<string>("--game")!,
            parseResult.GetValue<string>("--version")!,
            parseResult.GetValue<string?>("--asset-name") ?? Path.GetFileName(zip),
            zip,
            gate);
    }

    private static Option<string> RequiredInput(string description) =>
        new(CatalogCommandSupport.InputOptionName)
        {
            Description = description,
            Required = true
        };

    private static Option<string> RequiredOption(string name, string description) =>
        new(name)
        {
            Description = description,
            Required = true
        };

    private static int Complete<T>(
        OutcomeWriter writer,
        ParseResult parseResult,
        WorkflowResult<T> result)
    {
        if (result.ErrorKind != WorkflowErrorKind.None)
        {
            throw new WorkflowException(
                result.ErrorKind,
                result.Status,
                result.Messages,
                result.CompletedPhases);
        }

        return CatalogCommandSupport.Complete(writer, parseResult, result);
    }
}
