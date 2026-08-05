using System.CommandLine;
using System.CommandLine.Parsing;
using AccessibilityModManager.Authoring.Workflows;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class RootCommands
{
    public const string Version = "0.28.0";
    public const string VersionOptionName = "--version";
    public const string JsonOptionName = "--json";
    public const string QuietOptionName = "--quiet";
    public const string ProjectOptionName = "--project";
    public const string DryRunOptionName = "--dry-run";
    public const string YesOptionName = "--yes";
    public const string VerboseOptionName = "--verbose";

    public static RootCommand Create()
    {
        var root = new RootCommand("Accessibility Mod Manager authoring CLI.");

        root.Add(new Option<bool>(JsonOptionName)
        {
            Description = "Write machine-readable JSON."
        });

        root.Add(new Option<bool>(QuietOptionName)
        {
            Description = "Suppress human status lines."
        });

        root.Add(new Option<string?>(ProjectOptionName)
        {
            Description = "Path to the author project directory."
        });

        root.Add(new Option<bool>(DryRunOptionName)
        {
            Description = "Validate and preview without making durable changes."
        });

        root.Add(new Option<bool>(YesOptionName)
        {
            Description = "Confirm prompts without bypassing validation or trust checks."
        });

        root.Add(new Option<bool>(VerboseOptionName)
        {
            Description = "Include detailed exception information."
        });

        root.SetAction(new Func<ParseResult, int>(_ =>
            throw new WorkflowException(
                WorkflowErrorKind.Usage,
                "usage",
                new[] { "A command is required. Use --help for available commands." })
        ));

        return root;
    }
}
