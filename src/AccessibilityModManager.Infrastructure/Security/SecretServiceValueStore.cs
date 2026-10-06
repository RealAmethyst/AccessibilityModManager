using System.Diagnostics;

namespace AccessibilityModManager.Infrastructure.Security;

/// <summary>Named values in the current Linux desktop's Secret Service collection.</summary>
public static class SecretServiceValueStore
{
    private const string Application = "accessibility-mod-manager";
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    public static string? Lookup(string account)
    {
        var result = Run("lookup", account);
        if (result.ExitCode == 1 && result.Output.Length == 0 && result.Error.Length == 0) return null;
        if (result.ExitCode != 0)
            throw new InvalidOperationException("The Linux password store could not be opened.");
        return result.Output;
    }

    public static void Store(string account, string label, string value, string? collection = null)
    {
        var result = Run("store", account, "--label=" + label, value, collection);
        if (result.ExitCode != 0 || result.Error.Length != 0)
            throw new InvalidOperationException("The Linux password store did not save a value.");
    }

    public static void Clear(string account)
    {
        var result = Run("clear", account);
        if (result.ExitCode != 0 && !(result.ExitCode == 1 && result.Error.Length == 0))
            throw new InvalidOperationException("The Linux password store did not remove a value.");
    }

    private static (int ExitCode, string Output, string Error) Run(
        string command, string account, string? option = null, string? value = null,
        string? collection = null)
    {
        using var process = Start(command, account, option, collection);
        using var timeout = new CancellationTokenSource(OperationTimeout);
        try
        {
            if (value is not null)
            {
                process.StandardInput.WriteAsync(value.AsMemory(), timeout.Token)
                    .GetAwaiter().GetResult();
                process.StandardInput.Close();
            }

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            return (process.ExitCode, output.GetAwaiter().GetResult(),
                error.GetAwaiter().GetResult());
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("The Linux password store did not respond within 10 seconds.");
        }
    }

    private static Process Start(string command, string account, string? option, string? collection)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Secret Service is only used on Linux.");
        if (string.IsNullOrWhiteSpace(account) || account.Any(ch =>
                !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-')))
            throw new ArgumentException("Invalid password store account name.", nameof(account));
        var start = new ProcessStartInfo("secret-tool")
        {
            UseShellExecute = false,
            RedirectStandardInput = command == "store",
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(command);
        if (collection is not null) start.ArgumentList.Add("--collection=" + collection);
        if (option is not null) start.ArgumentList.Add(option);
        start.ArgumentList.Add("application");
        start.ArgumentList.Add(Application);
        start.ArgumentList.Add("account");
        start.ArgumentList.Add(account);
        return Process.Start(start) ??
            throw new InvalidOperationException("The Linux password store did not start.");
    }
}
