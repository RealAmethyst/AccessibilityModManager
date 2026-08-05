using System.Text;

namespace AccessibilityModManager.AuthorCli.Console;

public static class SecretReader
{
    public static async Task<string> ReadAsync(ICliConsole console, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(console);

        if (!console.IsInputRedirected && console is CliConsole)
        {
            return await ReadInteractiveAsync(ct);
        }

        return await ReadRedirectedAsync(console.In, ct);
    }

    private static async Task<string> ReadInteractiveAsync(CancellationToken ct)
    {
        var builder = new StringBuilder();
        var originalTreatControlCAsInput = System.Console.TreatControlCAsInput;

        System.Console.TreatControlCAsInput = true;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                while (!System.Console.KeyAvailable)
                {
                    await Task.Delay(25, ct);
                }

                var key = System.Console.ReadKey(intercept: true);

                if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.C)
                {
                    throw new OperationCanceledException(ct);
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    return builder.ToString();
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (builder.Length > 0)
                    {
                        builder.Length--;
                    }

                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    builder.Append(key.KeyChar);
                }
            }
        }
        finally
        {
            System.Console.TreatControlCAsInput = originalTreatControlCAsInput;
        }
    }

    private static async Task<string> ReadRedirectedAsync(TextReader input, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var buffer = new char[1];

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var read = await input.ReadAsync(buffer, 0, 1).WaitAsync(ct);
            if (read == 0)
            {
                return builder.ToString();
            }

            var value = buffer[0];
            switch (value)
            {
                case '\u0003':
                    throw new OperationCanceledException(ct);

                case '\b':
                case '\u007F':
                    if (builder.Length > 0)
                    {
                        builder.Length--;
                    }

                    break;

                case '\r':
                case '\n':
                    return builder.ToString();

                default:
                    if (!char.IsControl(value))
                    {
                        builder.Append(value);
                    }

                    break;
            }
        }
    }
}
