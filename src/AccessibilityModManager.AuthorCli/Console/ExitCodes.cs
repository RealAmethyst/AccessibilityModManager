using AccessibilityModManager.Authoring.Workflows;

namespace AccessibilityModManager.AuthorCli.Console;

public enum CliExitCode
{
    Success = 0,
    Usage = 2,
    Validation = 3,
    Authentication = 4,
    Conflict = 5,
    Cancelled = 130
}

public static class ExitCodes
{
    public static CliExitCode From(WorkflowErrorKind errorKind) =>
        errorKind switch
        {
            WorkflowErrorKind.None => CliExitCode.Success,
            WorkflowErrorKind.Usage => CliExitCode.Usage,
            WorkflowErrorKind.Validation => CliExitCode.Validation,
            WorkflowErrorKind.Authentication => CliExitCode.Authentication,
            WorkflowErrorKind.Conflict => CliExitCode.Conflict,
            WorkflowErrorKind.Cancelled => CliExitCode.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(errorKind), errorKind, null)
        };
}
