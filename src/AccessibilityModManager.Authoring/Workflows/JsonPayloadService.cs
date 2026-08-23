using System.Text;
using System.Text.Json;

namespace AccessibilityModManager.Authoring.Workflows;

public sealed class JsonPayloadService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<T> ReadAsync<T>(string source, TextReader stdin, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(stdin);

        ct.ThrowIfCancellationRequested();

        var json = source == "-"
            ? await stdin.ReadToEndAsync().WaitAsync(ct)
            : await ReadFileAsync(source, ct);

        var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
        if (value is null)
        {
            throw new InvalidOperationException(
                $"JSON payload from {DescribeSource(source)} deserialized to null.");
        }

        return value;
    }

    private static async Task<string> ReadFileAsync(string path, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);

        return await reader.ReadToEndAsync().WaitAsync(ct);
    }

    private static string DescribeSource(string source) =>
        source == "-" ? "standard input" : $"'{Path.GetFullPath(source)}'";
}
