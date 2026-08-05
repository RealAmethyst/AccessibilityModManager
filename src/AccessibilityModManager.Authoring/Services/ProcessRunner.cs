using System.Diagnostics;
using System.IO;
using System.Text;

namespace AccessibilityModManager.AuthorTool.Services;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(Stderr) ? Stdout : $"{Stdout}\n{Stderr}";
}

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) stdoutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuilder.AppendLine(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process: {fileName}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            stdoutBuilder.ToString().TrimEnd('\r', '\n'),
            stderrBuilder.ToString().TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// Runs a process and returns stdout as raw BYTES.
    ///
    /// <para><see cref="RunAsync"/> cannot be used where the exact bytes matter: it reads line by
    /// line, which rewrites every line ending to the platform's and drops the trailing one. That is
    /// fine for parsing git's own output and wrong for reading a blob out of git, where the whole
    /// question is whether the stored bytes equal the bytes intended — a check that would otherwise
    /// be defeated by the very line-ending rewriting it exists to catch.</para>
    /// </summary>
    /// <param name="maxBytes">
    /// Hard ceiling. An index is a small document; anything approaching this is a broken repository
    /// or the wrong object, and buffering it unbounded to find that out helps nobody.
    /// </param>
    public static async Task<(int ExitCode, byte[] Stdout, string Stderr)> RunBinaryAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDirectory = null,
        int maxBytes = 16 * 1024 * 1024,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process: {fileName}");

        // stderr is drained concurrently: a process that fills the error pipe while nobody reads it
        // blocks forever, and git is perfectly willing to be chatty on it.
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await process.StandardOutput.BaseStream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidOperationException($"{fileName} produced more than {maxBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }

        var stderr = await stderrTask;

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return (process.ExitCode, buffer.ToArray(), stderr.TrimEnd('\r', '\n'));
    }
}
