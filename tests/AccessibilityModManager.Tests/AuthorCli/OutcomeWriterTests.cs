using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using Xunit;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class OutcomeWriterTests
{
    [Fact]
    public void Human_output_is_emitted_as_complete_lines()
    {
        var console = new RecordingConsole();
        var writer = new OutcomeWriter(console);
        var result = new WorkflowResult<string>(
            "validation-failed",
            null,
            new[] { "First problem.", "Second problem." },
            WorkflowErrorKind.Validation);

        writer.Write(result, json: false);

        Assert.NotEmpty(console.AllChunks);
        Assert.All(console.AllChunks, chunk => Assert.EndsWith(Environment.NewLine, chunk));
    }

    [Fact]
    public void Json_output_is_a_single_valid_object()
    {
        var console = new RecordingConsole();
        var writer = new OutcomeWriter(console);
        var result = new WorkflowResult<string>("ok", "value", Array.Empty<string>());

        writer.Write(result, json: true);

        Assert.Equal(
            "{\"status\":\"ok\",\"value\":\"value\",\"messages\":[]}" + Environment.NewLine,
            console.Stdout);
        Assert.Equal(string.Empty, console.Stderr);

        using var json = JsonDocument.Parse(console.Stdout);
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("value", json.RootElement.GetProperty("value").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public void Json_mode_keeps_errors_off_standard_output()
    {
        var console = new RecordingConsole();
        var writer = new OutcomeWriter(console);
        var result = new WorkflowResult<string>(
            "validation-failed",
            null,
            new[] { "A project path is required." },
            WorkflowErrorKind.Validation);

        writer.Write(result, json: true);

        Assert.Equal(string.Empty, console.Stdout);
        Assert.DoesNotContain("A project path is required.", console.Stdout);

        using var json = JsonDocument.Parse(console.Stderr);
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        Assert.Equal("validation-failed", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "A project path is required.",
            json.RootElement.GetProperty("messages")[0].GetString());
    }

    private sealed class RecordingConsole : ICliConsole
    {
        private readonly StringReader _input = new(string.Empty);
        private readonly RecordingTextWriter _stdout = new();
        private readonly RecordingTextWriter _stderr = new();
        private readonly List<string> _statusChunks = [];

        public TextReader In => _input;
        public TextWriter Out => _stdout;
        public TextWriter Error => _stderr;
        public bool IsInputRedirected => true;

        public string Stdout => string.Concat(_statusChunks) + _stdout.Text;
        public string Stderr => _stderr.Text;
        public string AllWrittenText => Stdout + Stderr;

        public IReadOnlyList<string> AllChunks =>
            _statusChunks
                .Concat(_stdout.Chunks)
                .Concat(_stderr.Chunks)
                .ToArray();

        public void WriteStatus(string message)
        {
            _statusChunks.Add(message + Environment.NewLine);
        }
    }

    private sealed class RecordingTextWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;
        public List<string> Chunks { get; } = [];
        public string Text => _buffer.ToString();

        public override void Write(char value)
        {
            var text = value.ToString();
            Chunks.Add(text);
            _buffer.Append(text);
        }

        public override void Write(string? value)
        {
            if (value is null)
                return;

            Chunks.Add(value);
            _buffer.Append(value);
        }

        public override void WriteLine()
        {
            Chunks.Add(NewLine);
            _buffer.Append(NewLine);
        }

        public override void WriteLine(string? value)
        {
            var text = (value ?? string.Empty) + NewLine;
            Chunks.Add(text);
            _buffer.Append(text);
        }
    }
}
