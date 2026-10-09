using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AccessibilityModManager.AuthorCli;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class AccessibilityOutputTests
{
    private static readonly Regex Ansi = new("\\u001b\\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex BareCarriageReturn = new("\\r(?!\\n)", RegexOptions.Compiled);

    [Fact]
    public async Task Default_help_has_no_ansi_or_cursor_rewriting()
    {
        var console = new RecordingConsole();
        using var services = CliServices.Create(new CliServiceOverrides(Console: console));

        var exitCode = await Program.RunAsync(["--help"], services);

        Assert.Equal(0, exitCode);
        Assert.DoesNotMatch(Ansi, console.AllText);
        Assert.DoesNotMatch(BareCarriageReturn, console.AllText);
    }

    [Fact]
    public void Human_output_never_emits_a_punctuation_only_message()
    {
        var console = new RecordingConsole();
        var writer = new OutcomeWriter(console);

        writer.Write(
            new WorkflowResult<object?>("validationFailed", null, ["...", "---"], WorkflowErrorKind.Validation),
            json: false);

        Assert.Equal("validationFailed" + Environment.NewLine, console.Stderr);
    }

    [Fact]
    public void Human_output_strips_ansi_and_turns_cursor_rewrites_into_lines()
    {
        var console = new RecordingConsole();
        var writer = new OutcomeWriter(console);

        writer.Write(
            new WorkflowResult<object?>("ok", null, ["\u001b[31mFirst\u001b[0m\rSecond"]),
            json: false);

        Assert.Equal("First" + Environment.NewLine + "Second" + Environment.NewLine, console.Stdout);
        Assert.DoesNotMatch(Ansi, console.AllText);
        Assert.DoesNotMatch(BareCarriageReturn, console.AllText);
    }

    [Fact]
    public async Task Json_mode_writes_exactly_one_standard_output_document()
    {
        var console = new RecordingConsole();
        using var services = CliServices.Create(new CliServiceOverrides(Console: console));
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "stable bytes");

            var exitCode = await Program.RunAsync(["package", "hash", "--file", file, "--json"], services);

            Assert.Equal(0, exitCode);
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(console.Stdout), isFinalBlock: true, state: default);
            Assert.True(JsonDocument.TryParseValue(ref reader, out var document));
            using (document)
                Assert.Equal(JsonValueKind.Object, document!.RootElement.ValueKind);
            while (reader.Read())
                Assert.Equal(JsonTokenType.None, reader.TokenType);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Quiet_suppresses_status_but_not_warnings_or_failures()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var console = new CliConsole(new StringReader(string.Empty), output, error, isInputRedirected: true)
        {
            Quiet = true
        };

        console.WriteStatus("routine status");
        console.WriteWarning("configuration warning");

        using var services = CliServices.Create(new CliServiceOverrides(Console: console));
        var exitCode = await Program.RunAsync(["not-a-command", "--quiet"], services);

        Assert.Equal((int)CliExitCode.Usage, exitCode);
        Assert.DoesNotContain("routine status", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("configuration warning", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("not-a-command", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingConsole : ICliConsole
    {
        private readonly StringWriter _stdout = new();
        private readonly StringWriter _stderr = new();

        public TextReader In { get; } = new StringReader(string.Empty);
        public TextWriter Out => _stdout;
        public TextWriter Error => _stderr;
        public bool IsInputRedirected => true;
        public string Stdout => _stdout.ToString();
        public string Stderr => _stderr.ToString();
        public string AllText => Stdout + Stderr;

        public void WriteStatus(string message) => _stderr.WriteLine(message);
        public void WriteWarning(string message) => _stderr.WriteLine(message);
    }
}
