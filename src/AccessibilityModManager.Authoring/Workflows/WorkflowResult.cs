namespace AccessibilityModManager.Authoring.Workflows;

public enum WorkflowErrorKind
{
    None,
    Usage,
    Validation,
    Authentication,
    Conflict,
    Cancelled
}

public sealed record WorkflowResult<T>
{
    public WorkflowResult(
        string status,
        T? value,
        IReadOnlyList<string> messages,
        WorkflowErrorKind errorKind = WorkflowErrorKind.None,
        IReadOnlyList<string>? completedPhases = null)
    {
        Status = string.IsNullOrWhiteSpace(status)
            ? throw new ArgumentException("Status is required.", nameof(status))
            : status;
        Value = value;
        Messages = messages ?? throw new ArgumentNullException(nameof(messages));
        ErrorKind = errorKind;
        CompletedPhases = completedPhases;
    }

    public string Status { get; }
    public T? Value { get; }
    public IReadOnlyList<string> Messages { get; }
    public WorkflowErrorKind ErrorKind { get; }
    public IReadOnlyList<string>? CompletedPhases { get; }
}

public sealed class WorkflowException : Exception
{
    public WorkflowException(
        WorkflowErrorKind errorKind,
        string status,
        IReadOnlyList<string> messages,
        IReadOnlyList<string>? completedPhases = null,
        Exception? innerException = null)
        : base(CreateMessage(status, messages), innerException)
    {
        ErrorKind = errorKind;
        Status = string.IsNullOrWhiteSpace(status)
            ? throw new ArgumentException("Status is required.", nameof(status))
            : status;
        Messages = messages ?? throw new ArgumentNullException(nameof(messages));
        CompletedPhases = completedPhases;
    }

    public WorkflowErrorKind ErrorKind { get; }
    public string Status { get; }
    public IReadOnlyList<string> Messages { get; }
    public IReadOnlyList<string>? CompletedPhases { get; }

    public WorkflowResult<T> ToResult<T>(T? value = default, bool verbose = false)
    {
        if (!verbose)
        {
            return new WorkflowResult<T>(Status, value, Messages, ErrorKind, CompletedPhases);
        }

        var detailedMessages = new List<string>(Messages.Count + 1);
        detailedMessages.AddRange(Messages);
        detailedMessages.Add(ToString());
        return new WorkflowResult<T>(Status, value, detailedMessages, ErrorKind, CompletedPhases);
    }

    private static string CreateMessage(string status, IReadOnlyList<string> messages)
    {
        if (messages is { Count: > 0 } && !string.IsNullOrWhiteSpace(messages[0]))
        {
            return messages[0];
        }

        return status;
    }
}
