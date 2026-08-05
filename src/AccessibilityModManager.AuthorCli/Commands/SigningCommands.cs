using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class SigningCommands
{
    public static void AddTo(RootCommand root, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(services);

        var writer = services.GetRequiredService<OutcomeWriter>();
        var console = services.GetRequiredService<ICliConsole>();
        var projects = services.GetRequiredService<AuthorProjectContext>();
        var workflow = services.GetRequiredService<ISigningWorkflow>();
        var signing = new Command("signing", "Manage catalog signing keys, claims, and publisher-head recovery.");

        var status = new Command("status", "Show the public identity and local state of one signing key.");
        var statusPlugin = Required("--plugin", "Plugin id.");
        status.Options.Add(statusPlugin);
        status.SetAction(parseResult => Complete(
            writer, parseResult, workflow.GetStatus(parseResult.GetValue(statusPlugin)!)));

        var create = new Command("create", "Create a new per-plugin signing key and store it encrypted.");
        var createPlugin = Required("--plugin", "Plugin id.");
        var createStdin = SecretOption("--passphrase-stdin", "Read the new key passphrase from redirected input.");
        create.Options.Add(createPlugin);
        create.Options.Add(createStdin);
        create.SetAction(async (parseResult, cancellationToken) =>
        {
            var passphrase = await ReadSecretAsync(
                console, parseResult.GetValue(createStdin),
                "Signing-key passphrase:", "--passphrase-stdin", cancellationToken);
            return Complete(writer, parseResult,
                workflow.Create(parseResult.GetValue(createPlugin)!, passphrase));
        });

        var export = new Command("export", "Write an encrypted portable key and publisher-state backup.");
        var exportPlugin = Required("--plugin", "Plugin id.");
        var destination = Required("--destination", "Destination backup JSON path.");
        var exportStdin = SecretOption("--passphrase-stdin", "Read the separate backup passphrase from redirected input.");
        export.Options.Add(exportPlugin);
        export.Options.Add(destination);
        export.Options.Add(exportStdin);
        export.SetAction(async (parseResult, cancellationToken) =>
        {
            var passphrase = await ReadSecretAsync(
                console, parseResult.GetValue(exportStdin),
                "Backup passphrase (use a different passphrase from the local key):",
                "--passphrase-stdin", cancellationToken);
            return Complete(writer, parseResult, workflow.Export(
                parseResult.GetValue(exportPlugin)!,
                parseResult.GetValue(destination)!,
                passphrase));
        });

        var import = new Command("import", "Restore an encrypted key backup and its publisher history.");
        var source = Required("--source", "Source backup JSON path.");
        var importStdin = SecretOption("--passphrase-stdin", "Read the backup passphrase from redirected input.");
        import.Options.Add(source);
        import.Options.Add(importStdin);
        import.SetAction(async (parseResult, cancellationToken) =>
        {
            var passphrase = await ReadSecretAsync(
                console, parseResult.GetValue(importStdin),
                "Backup passphrase:", "--passphrase-stdin", cancellationToken);
            return Complete(writer, parseResult,
                workflow.Import(parseResult.GetValue(source)!, passphrase));
        });

        var change = new Command("change-passphrase", "Re-encrypt a local key without changing its public identity.");
        var changePlugin = Required("--plugin", "Plugin id.");
        var passphrasesStdin = SecretOption(
            "--passphrases-stdin",
            "Read current and new passphrases from two redirected input lines, in that order.");
        change.Options.Add(changePlugin);
        change.Options.Add(passphrasesStdin);
        change.SetAction(async (parseResult, cancellationToken) =>
        {
            string current;
            string replacement;
            if (parseResult.GetValue(passphrasesStdin))
            {
                RequireRedirected(console, "--passphrases-stdin");
                current = await SecretReader.ReadAsync(console, cancellationToken);
                replacement = await SecretReader.ReadAsync(console, cancellationToken);
            }
            else
            {
                if (console.IsInputRedirected)
                    throw CatalogCommandSupport.Usage(
                        "Redirected passphrases require --passphrases-stdin; never put secrets on the command line.");
                console.WriteStatus("Current signing-key passphrase:");
                current = await SecretReader.ReadAsync(console, cancellationToken);
                console.WriteStatus("New signing-key passphrase:");
                replacement = await SecretReader.ReadAsync(console, cancellationToken);
            }

            return Complete(writer, parseResult, workflow.ChangePassphrase(
                parseResult.GetValue(changePlugin)!, current, replacement));
        });

        var claims = new Command("claims", "Preview or sign the exact claims represented by index.json.");
        var claimsPreview = new Command("preview", "Preview the next signed publish without opening a key or writing a journal.");
        claimsPreview.SetAction(async (parseResult, cancellationToken) =>
        {
            var project = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            return Complete(writer, parseResult,
                await workflow.PreviewClaimsAsync(project.ProjectPath, cancellationToken));
        });
        var claimsSign = new Command("sign", "Sign and journal the reviewed publish without uploading it.");
        var deletionToken = new Option<string?>("--deletions-token")
        {
            Description = "Exact permanent-removal token returned by claims preview."
        };
        claimsSign.Options.Add(deletionToken);
        claimsSign.SetAction(async (parseResult, cancellationToken) =>
        {
            var project = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            return Complete(writer, parseResult, await workflow.SignClaimsAsync(
                project.ProjectPath,
                parseResult.GetValue(deletionToken) ?? "",
                CatalogCommandSupport.GetYes(parseResult),
                cancellationToken));
        });
        claims.Subcommands.Add(claimsPreview);
        claims.Subcommands.Add(claimsSign);

        var head = new Command("head", "Inspect and safely settle the publisher journal.");
        var headStatus = new Command("status", "Show every publisher-head record for one plugin.");
        var headPlugin = Required("--plugin", "Plugin id.");
        headStatus.Options.Add(headPlugin);
        headStatus.SetAction(parseResult => Complete(
            writer, parseResult, workflow.GetHeadStatus(parseResult.GetValue(headPlugin)!)));

        var headConfirm = new Command("confirm", "Confirm that the exact pending bytes are already live.");
        headConfirm.SetAction(async (parseResult, cancellationToken) =>
        {
            RequireYes(parseResult, "Confirming a publisher head");
            var project = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            return Complete(writer, parseResult,
                await workflow.ConfirmHeadAsync(project.ProjectPath, cancellationToken));
        });

        var commitPending = new Command("commit-pending", "Commit a pending head only after proving it landed.");
        commitPending.SetAction(async (parseResult, cancellationToken) =>
        {
            var project = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            return Complete(writer, parseResult, await workflow.CommitPendingAsync(
                project.ProjectPath, CatalogCommandSupport.GetYes(parseResult), cancellationToken));
        });

        var resume = new Command("resume", "Publish the exact journalled bytes for an interrupted attempt.");
        resume.SetAction(async (parseResult, cancellationToken) =>
        {
            var project = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            return Complete(writer, parseResult, await workflow.ResumeHeadAsync(
                project.ProjectPath, CatalogCommandSupport.GetYes(parseResult), cancellationToken));
        });

        head.Subcommands.Add(headStatus);
        head.Subcommands.Add(headConfirm);
        head.Subcommands.Add(commitPending);
        head.Subcommands.Add(resume);
        signing.Subcommands.Add(status);
        signing.Subcommands.Add(create);
        signing.Subcommands.Add(export);
        signing.Subcommands.Add(import);
        signing.Subcommands.Add(change);
        signing.Subcommands.Add(claims);
        signing.Subcommands.Add(head);
        root.Subcommands.Add(signing);
    }

    private static Option<string> Required(string name, string description) =>
        new(name) { Description = description, Required = true };

    private static Option<bool> SecretOption(string name, string description) =>
        new(name) { Description = description };

    private static async Task<string> ReadSecretAsync(
        ICliConsole console,
        bool fromStdin,
        string prompt,
        string stdinOption,
        CancellationToken ct)
    {
        if (fromStdin)
        {
            RequireRedirected(console, stdinOption);
            return await SecretReader.ReadAsync(console, ct);
        }

        if (console.IsInputRedirected)
            throw CatalogCommandSupport.Usage(
                $"Redirected secret input requires {stdinOption}; never put passphrases on the command line.");
        console.WriteStatus(prompt);
        return await SecretReader.ReadAsync(console, ct);
    }

    private static void RequireRedirected(ICliConsole console, string option)
    {
        if (!console.IsInputRedirected)
            throw CatalogCommandSupport.Usage($"{option} requires redirected standard input.");
    }

    private static void RequireYes(ParseResult parseResult, string operation)
    {
        if (!CatalogCommandSupport.GetYes(parseResult))
            throw CatalogCommandSupport.Conflict($"{operation} requires --yes after reviewing the pending state.");
    }

    private static int Complete<T>(OutcomeWriter writer, ParseResult parseResult, WorkflowResult<T> result)
    {
        if (result.ErrorKind != WorkflowErrorKind.None)
            throw new WorkflowException(result.ErrorKind, result.Status, result.Messages, result.CompletedPhases);
        return CatalogCommandSupport.Complete(writer, parseResult, result);
    }
}
