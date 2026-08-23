using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AccessibilityModManager.AuthorCli.Console;
using Xunit;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class SecretReaderTests
{
    [Fact]
    public async Task Concealed_input_honours_backspace_without_echoing_characters()
    {
        var console = new RecordingConsole("secret\b\b42\r\n");

        var secret = await SecretReader.ReadAsync(console, CancellationToken.None);

        Assert.Equal("secr42", secret);
        Assert.Equal(string.Empty, console.AllWrittenText);
        Assert.DoesNotContain("secret", console.AllWrittenText);
    }

    private sealed class RecordingConsole : ICliConsole
    {
        private readonly StringReader _input;
        private readonly RecordingTextWriter _stdout = new();
        private readonly RecordingTextWriter _stderr = new();

        public RecordingConsole(string input)
        {
            _input = new StringReader(input);
        }

        public TextReader In => _input;
        public TextWriter Out => _stdout;
        public TextWriter Error => _stderr;
        public bool IsInputRedirected => true;

        public string AllWrittenText => _stdout.Text + _stderr.Text;

        public void WriteStatus(string message)
        {
            _stderr.WriteLine(message);
        }
    }

    private sealed class RecordingTextWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;
        public string Text => _buffer.ToString();

        public override void Write(char value) => _buffer.Append(value);

        public override void Write(string? value)
        {
            if (value is not null)
                _buffer.Append(value);
        }

        public override void WriteLine()
        {
            _buffer.Append(NewLine);
        }

        public override void WriteLine(string? value)
        {
            _buffer.Append(value ?? string.Empty);
            _buffer.Append(NewLine);
        }
    }
}
