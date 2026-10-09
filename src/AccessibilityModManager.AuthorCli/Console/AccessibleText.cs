using System.Text.RegularExpressions;

namespace AccessibilityModManager.AuthorCli.Console;

internal static partial class AccessibleText
{
    public static IReadOnlyList<string> MeaningfulLines(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var plain = AnsiSequence().Replace(value, string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        return plain
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Any(char.IsLetterOrDigit))
            .ToArray();
    }

    public static string StatusOrFallback(string? value, bool failed)
    {
        var lines = MeaningfulLines(value);
        return lines.Count > 0
            ? string.Join(' ', lines)
            : failed ? "Operation failed." : "Operation completed.";
    }

    [GeneratedRegex("\\u001B\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiSequence();
}
