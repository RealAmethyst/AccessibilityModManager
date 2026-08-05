using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class RegistryCommands
{
    public static void AddTo(RootCommand root, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(services);

        var writer = services.GetRequiredService<OutcomeWriter>();
        var console = services.GetRequiredService<ICliConsole>();
        var workflow = services.GetRequiredService<IRegistryAdminWorkflow>();
        var registry = new Command(
            "registry",
            "Maintain the signed global plugin registry (admin build required for every operation).");

        var status = new Command("status", "Show registry-admin build and checkout status.");
        status.SetAction(parseResult => Complete(writer, parseResult, workflow.GetStatus()));

        var open = new Command("open", "Open an existing registry checkout or clone the canonical repository.");
        var openRepo = Optional("--repo", "Checkout path; defaults inside the author configuration directory.");
        open.Options.Add(openRepo);
        open.SetAction(async (parseResult, cancellationToken) => Complete(
            writer, parseResult,
            await workflow.OpenAsync(parseResult.GetValue(openRepo), cancellationToken)));

        var refresh = new Command("refresh", "Fast-forward the registry checkout and validate its JSON.");
        var refreshRepo = Required("--repo", "Registry checkout path.");
        refresh.Options.Add(refreshRepo);
        refresh.SetAction(async (parseResult, cancellationToken) => Complete(
            writer, parseResult,
            await workflow.RefreshAsync(parseResult.GetValue(refreshRepo)!, cancellationToken)));

        var json = new Command("json", "Read, validate, or save the registry JSON document.");
        var show = new Command("show", "Return the exact JSON, path, and SHA256.");
        var showPath = Required("--path", "Registry JSON path or checkout directory.");
        show.Options.Add(showPath);
        show.SetAction(parseResult => Complete(
            writer, parseResult, workflow.ShowJson(parseResult.GetValue(showPath)!)));

        var validate = new Command("validate", "Run the exact manager-side registry validation rules.");
        var validatePath = Required("--path", "Registry JSON path.");
        validate.Options.Add(validatePath);
        validate.SetAction(parseResult => Complete(
            writer, parseResult, workflow.Validate(parseResult.GetValue(validatePath)!)));

        var save = new Command("save", "Parse and durably replace the registry JSON without a UTF-8 BOM.");
        var savePath = Required("--path", "Registry JSON path.");
        var saveInput = Required(CatalogCommandSupport.InputOptionName, "JSON file path, or '-' for redirected input.");
        save.Options.Add(savePath);
        save.Options.Add(saveInput);
        save.SetAction(async (parseResult, cancellationToken) =>
        {
            EnsureAdmin(workflow);
            var content = await ReadInputAsync(
                parseResult.GetValue(saveInput)!, console, cancellationToken);
            return Complete(writer, parseResult,
                workflow.Save(parseResult.GetValue(savePath)!, content));
        });
        json.Subcommands.Add(show);
        json.Subcommands.Add(validate);
        json.Subcommands.Add(save);

        var sign = new Command("sign", "Sign the validated JSON with the offline registry private key.");
        var signPath = Required("--path", "Registry JSON path.");
        var privateKey = Required("--private-key", "Encrypted registry private-key PEM path.");
        var passphraseStdin = new Option<bool>("--passphrase-stdin")
        {
            Description = "Read the private-key passphrase from one redirected input line."
        };
        sign.Options.Add(signPath);
        sign.Options.Add(privateKey);
        sign.Options.Add(passphraseStdin);
        sign.SetAction(async (parseResult, cancellationToken) =>
        {
            EnsureAdmin(workflow);
            string passphrase;
            if (parseResult.GetValue(passphraseStdin))
            {
                if (!console.IsInputRedirected)
                    throw CatalogCommandSupport.Usage("--passphrase-stdin requires redirected standard input.");
                passphrase = await SecretReader.ReadAsync(console, cancellationToken);
            }
            else
            {
                if (console.IsInputRedirected)
                    throw CatalogCommandSupport.Usage(
                        "Redirected secret input requires --passphrase-stdin; never put passphrases on the command line.");
                console.WriteStatus("Registry private-key passphrase:");
                passphrase = await SecretReader.ReadAsync(console, cancellationToken);
            }

            return Complete(writer, parseResult, workflow.Sign(
                parseResult.GetValue(signPath)!,
                parseResult.GetValue(privateKey)!,
                passphrase,
                CatalogCommandSupport.GetYes(parseResult)));
        });

        var publish = new Command("publish", "Atomically publish and read back the signed registry pair.");
        var publishRepo = Required("--repo", "Registry checkout path.");
        publish.Options.Add(publishRepo);
        publish.SetAction(async (parseResult, cancellationToken) => Complete(
            writer, parseResult,
            await workflow.PublishAsync(
                parseResult.GetValue(publishRepo)!,
                CatalogCommandSupport.GetYes(parseResult),
                cancellationToken)));

        var commit = new Command("commit", "Stage and commit registry JSON and signature changes locally.");
        var commitRepo = Required("--repo", "Registry checkout path.");
        var message = new Option<string?>("--message")
        {
            Description = "Commit message; defaults to 'Update plugin registry'."
        };
        commit.Options.Add(commitRepo);
        commit.Options.Add(message);
        commit.SetAction(async (parseResult, cancellationToken) =>
        {
            EnsureAdmin(workflow);
            RequireYes(parseResult, "Committing registry changes");
            return Complete(writer, parseResult, await workflow.CommitAsync(
                parseResult.GetValue(commitRepo)!,
                parseResult.GetValue(message) ?? "Update plugin registry",
                cancellationToken));
        });

        var push = new Command("push", "Push committed registry history; this does not publish the live registry.");
        var pushRepo = Required("--repo", "Registry checkout path.");
        push.Options.Add(pushRepo);
        push.SetAction(async (parseResult, cancellationToken) =>
        {
            EnsureAdmin(workflow);
            RequireYes(parseResult, "Pushing registry history");
            return Complete(writer, parseResult,
                await workflow.PushAsync(parseResult.GetValue(pushRepo)!, cancellationToken));
        });

        registry.Subcommands.Add(status);
        registry.Subcommands.Add(open);
        registry.Subcommands.Add(refresh);
        registry.Subcommands.Add(json);
        registry.Subcommands.Add(sign);
        registry.Subcommands.Add(publish);
        registry.Subcommands.Add(commit);
        registry.Subcommands.Add(push);
        root.Subcommands.Add(registry);
    }

    private static Option<string> Required(string name, string description) =>
        new(name) { Description = description, Required = true };

    private static Option<string?> Optional(string name, string description) =>
        new(name) { Description = description };

    private static async Task<string> ReadInputAsync(
        string source,
        ICliConsole console,
        CancellationToken ct)
    {
        if (source == "-")
            return await console.In.ReadToEndAsync().WaitAsync(ct);
        return await File.ReadAllTextAsync(Path.GetFullPath(source), ct);
    }

    private static void EnsureAdmin(IRegistryAdminWorkflow workflow)
    {
        var status = workflow.GetStatus();
        if (status.ErrorKind != WorkflowErrorKind.None)
            throw new WorkflowException(status.ErrorKind, status.Status, status.Messages, status.CompletedPhases);
    }

    private static void RequireYes(ParseResult parseResult, string operation)
    {
        if (!CatalogCommandSupport.GetYes(parseResult))
            throw CatalogCommandSupport.Conflict($"{operation} requires --yes.");
    }

    private static int Complete<T>(OutcomeWriter writer, ParseResult parseResult, WorkflowResult<T> result)
    {
        if (result.ErrorKind != WorkflowErrorKind.None)
            throw new WorkflowException(result.ErrorKind, result.Status, result.Messages, result.CompletedPhases);
        return CatalogCommandSupport.Complete(writer, parseResult, result);
    }
}
