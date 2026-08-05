namespace AccessibilityModManager.AuthorCli.Console;

public interface ICliConsole
{
    TextReader In { get; }
    TextWriter Out { get; }
    TextWriter Error { get; }
    bool IsInputRedirected { get; }
    void WriteStatus(string message);
}

public sealed class CliConsole : ICliConsole
{
    public CliConsole(TextReader input, TextWriter output, TextWriter error, bool isInputRedirected)
    {
        In = input ?? throw new ArgumentNullException(nameof(input));
        Out = output ?? throw new ArgumentNullException(nameof(output));
        Error = error ?? throw new ArgumentNullException(nameof(error));
        IsInputRedirected = isInputRedirected;
    }

    public TextReader In { get; }
    public TextWriter Out { get; }
    public TextWriter Error { get; }
    public bool IsInputRedirected { get; }
    public bool Quiet { get; set; }

    public static CliConsole CreateSystem() =>
        new(
            TextReader.Synchronized(System.Console.In),
            TextWriter.Synchronized(System.Console.Out),
            TextWriter.Synchronized(System.Console.Error),
            System.Console.IsInputRedirected);

    public void WriteStatus(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (Quiet)
        {
            return;
        }

        Error.WriteLine(message);
        Error.Flush();
    }
}
