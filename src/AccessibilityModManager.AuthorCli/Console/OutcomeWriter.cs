using System.Text.Json;
using System.Text.Json.Serialization;
using AccessibilityModManager.Authoring.Workflows;

namespace AccessibilityModManager.AuthorCli.Console;

public sealed class OutcomeWriter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly ICliConsole _console;

    public OutcomeWriter(ICliConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public void Write<T>(WorkflowResult<T> result, bool json)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (json)
        {
            WriteJson(result);
            return;
        }

        WriteHuman(result);
    }

    private void WriteHuman<T>(WorkflowResult<T> result)
    {
        var writer = result.ErrorKind == WorkflowErrorKind.None
            ? _console.Out
            : _console.Error;

        var messages = result.Messages
            .SelectMany(AccessibleText.MeaningfulLines)
            .ToArray();
        if (messages.Length > 0)
        {
            foreach (var message in messages)
            {
                writer.WriteLine(message);
            }

            writer.Flush();
            return;
        }

        if (result.Value is string text)
        {
            var lines = AccessibleText.MeaningfulLines(text);
            if (lines.Count > 0)
            {
                foreach (var line in lines)
                    writer.WriteLine(line);
                writer.Flush();
                return;
            }
        }

        writer.WriteLine(AccessibleText.StatusOrFallback(
            result.Status,
            result.ErrorKind != WorkflowErrorKind.None));
        writer.Flush();
    }

    private void WriteJson<T>(WorkflowResult<T> result)
    {
        var writer = result.ErrorKind == WorkflowErrorKind.None
            ? _console.Out
            : _console.Error;

        var payload = new JsonOutcome
        {
            Status = AccessibleText.StatusOrFallback(
                result.Status,
                result.ErrorKind != WorkflowErrorKind.None),
            Value = result.Value,
            Messages = result.Messages.SelectMany(AccessibleText.MeaningfulLines).ToArray(),
            ErrorKind = result.ErrorKind,
            CompletedPhases = result.CompletedPhases is { Count: > 0 }
                ? result.CompletedPhases
                : null
        };

        writer.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        writer.Flush();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class JsonOutcome
    {
        public required string Status { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Value { get; init; }

        public required IReadOnlyList<string> Messages { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public WorkflowErrorKind ErrorKind { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? CompletedPhases { get; init; }
    }
}
