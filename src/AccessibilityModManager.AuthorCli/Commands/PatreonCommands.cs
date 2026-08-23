using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class PatreonCommands
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var writer = services.GetRequiredService<OutcomeWriter>();
        var workflow = services.GetRequiredService<IPatreonWorkflow>();
        var patreon = new Command("patreon", "Manage the AuthorTool Patreon session and inspect creator posts.");

        var status = new Command("status", "Show whether the author session is signed in.");
        status.SetAction(async (parseResult, cancellationToken) =>
            Complete(writer, parseResult, await workflow.GetStatusAsync(cancellationToken)));

        var login = new Command("login", "Open Patreon's author OAuth sign-in flow.");
        login.SetAction(async (parseResult, cancellationToken) =>
        {
            if (CatalogCommandSupport.GetDryRun(parseResult))
            {
                var current = await workflow.GetStatusAsync(cancellationToken);
                ThrowIfFailed(current);
                return CatalogCommandSupport.Complete(
                    writer,
                    parseResult,
                    new WorkflowResult<PatreonSessionStatus>(
                        "patreonLoginPreviewed",
                        current.Value,
                        new[] { "Patreon OAuth would open; no browser or session was changed." }));
            }

            return Complete(writer, parseResult, await workflow.SignInAsync(cancellationToken));
        });

        var logout = new Command("logout", "Revoke and remove the saved Patreon author session.");
        logout.SetAction(async (parseResult, cancellationToken) =>
        {
            if (CatalogCommandSupport.GetDryRun(parseResult))
            {
                return CatalogCommandSupport.Complete(
                    writer,
                    parseResult,
                    new WorkflowResult<bool>(
                        "patreonLogoutPreviewed",
                        true,
                        new[] { "The Patreon author session would be revoked and removed." }));
            }

            return Complete(writer, parseResult, await workflow.SignOutAsync(cancellationToken));
        });

        var tiers = new Command("tiers", "Refresh and list tiers from the signed-in creator campaign.");
        tiers.SetAction(async (parseResult, cancellationToken) =>
            Complete(writer, parseResult, await workflow.GetTiersAsync(cancellationToken)));

        var post = new Command("post", "Inspect creator posts used for gated release attachments.");
        var validate = new Command("validate", "Validate a Patreon post URL and list every attachment.");
        var url = new Option<string>("--url")
        {
            Description = "Full Patreon post URL.",
            Required = true
        };
        validate.Options.Add(url);
        validate.SetAction(async (parseResult, cancellationToken) =>
            Complete(
                writer,
                parseResult,
                await workflow.InspectPostAsync(parseResult.GetValue(url)!, cancellationToken)));
        post.Subcommands.Add(validate);

        patreon.Subcommands.Add(status);
        patreon.Subcommands.Add(login);
        patreon.Subcommands.Add(logout);
        patreon.Subcommands.Add(tiers);
        patreon.Subcommands.Add(post);
        return patreon;
    }

    private static int Complete<T>(
        OutcomeWriter writer,
        ParseResult parseResult,
        WorkflowResult<T> result)
    {
        ThrowIfFailed(result);
        return CatalogCommandSupport.Complete(writer, parseResult, result);
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
}
