using System.CommandLine;
using AccessibilityModManager.AuthorCli.Commands;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        using var services = CliServices.Create();
        return await RunAsync(args, services);
    }

    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(services);

        var console = services.GetRequiredService<ICliConsole>();

        if (IsExactVersionRequest(args))
        {
            await console.Out.WriteLineAsync(RootCommands.Version);
            await console.Out.FlushAsync();
            return (int)CliExitCode.Success;
        }

        var outcomeWriter = services.GetRequiredService<OutcomeWriter>();
        var root = RootCommands.Create();

        ProjectCommands.AddTo(root, services);
        AuthorCommands.AddTo(root, services);
        GameCommands.AddTo(root, services);
        DependencyCommands.AddTo(root, services);
        ScriptCommands.AddTo(root, services);

        var parseInputs = args.Length == 0 ? new[] { "--help" } : args;
        var parseResult = root.Parse(parseInputs);

        var json = HasFlag(args, RootCommands.JsonOptionName) || GetBooleanOption(parseResult, RootCommands.JsonOptionName);
        var verbose = HasFlag(args, RootCommands.VerboseOptionName) || GetBooleanOption(parseResult, RootCommands.VerboseOptionName);

        if (console is CliConsole cliConsole)
        {
            cliConsole.Quiet = GetBooleanOption(parseResult, RootCommands.QuietOptionName);
        }

        if (parseResult.Errors.Count > 0)
        {
            var messages = parseResult.Errors.Select(error => error.Message).ToArray();
            outcomeWriter.Write(
                new WorkflowResult<object?>("usage", null, messages, WorkflowErrorKind.Usage),
                json);
            return (int)ExitCodes.From(WorkflowErrorKind.Usage);
        }

        try
        {
            return await parseResult.InvokeAsync(new InvocationConfiguration
            {
                Output = console.Out,
                Error = console.Error,
                EnableDefaultExceptionHandler = false,
                ProcessTerminationTimeout = TimeSpan.FromSeconds(2)
            });
        }
        catch (OperationCanceledException)
        {
            outcomeWriter.Write(
                new WorkflowResult<object?>(
                    "cancelled",
                    null,
                    new[] { "Operation cancelled." },
                    WorkflowErrorKind.Cancelled),
                json);
            return (int)CliExitCode.Cancelled;
        }
        catch (WorkflowException ex)
        {
            outcomeWriter.Write(ex.ToResult<object?>(verbose: verbose), json);
            return (int)ExitCodes.From(ex.ErrorKind);
        }
        catch (Exception ex)
        {
            var messages = verbose
                ? new[] { ex.Message, ex.ToString() }
                : new[] { ex.Message };

            outcomeWriter.Write(
                new WorkflowResult<object?>(
                    "failed",
                    null,
                    messages,
                    WorkflowErrorKind.Validation),
                json);

            return (int)CliExitCode.Validation;
        }
    }

    private static bool IsExactVersionRequest(string[] args) =>
        args.Length == 1 &&
        string.Equals(args[0], RootCommands.VersionOptionName, StringComparison.Ordinal);

    private static bool HasFlag(string[] args, string optionName) =>
        args.Any(arg => string.Equals(arg, optionName, StringComparison.Ordinal));

    private static bool GetBooleanOption(ParseResult parseResult, string optionName)
    {
        try
        {
            return parseResult.GetValue<bool>(optionName);
        }
        catch
        {
            return false;
        }
    }
}
