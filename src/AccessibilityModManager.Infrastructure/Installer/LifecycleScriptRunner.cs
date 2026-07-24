using System.Diagnostics;
using System.Text;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Installer;

public sealed record LifecycleScriptResult(int ExitCode, bool Succeeded, string CombinedOutput);

/// <summary>
/// Runs a single <see cref="LifecycleScript"/>. Picks the runner by file extension
/// (<c>.exe</c> direct, <c>.ps1</c> via PowerShell, <c>.cmd</c>/<c>.bat</c> via cmd.exe).
/// Streams stdout/stderr line-by-line via the supplied callback so the manager's progress
/// dialog can update live. Honors cancellation by killing the process tree.
/// </summary>
/// <remarks>
/// When <see cref="LifecycleScript.NeedsAdmin"/> is true we have to use ShellExecute so
/// Windows can show a UAC prompt. ShellExecute can't redirect stdout/stderr, so for elevated
/// scripts we don't capture output — the user sees a UAC prompt and the process runs in its
/// own console (or hidden, depending on what the script does internally). We accept this
/// limitation in v1; authors can write a non-elevated wrapper script that elevates only the
/// specific operation it needs if they want progress streaming back.
/// </remarks>
public sealed class LifecycleScriptRunner
{
    private readonly ILogger _logger;

    public LifecycleScriptRunner(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<LifecycleScriptResult> RunAsync(
        LifecycleScript script,
        string scriptAbsolutePath,
        string gameFolder,
        string modFolder,
        Action<string>? onOutputLine,
        CancellationToken ct = default)
    {
        if (!File.Exists(scriptAbsolutePath))
            throw new FileNotFoundException(
                $"Lifecycle script not found: {scriptAbsolutePath}", scriptAbsolutePath);

        var ext = Path.GetExtension(scriptAbsolutePath).ToLowerInvariant();
        var (filename, args) = BuildCommand(ext, scriptAbsolutePath, gameFolder, modFolder);

        var psi = new ProcessStartInfo
        {
            FileName = filename,
            WorkingDirectory = gameFolder,
            CreateNoWindow = !script.NeedsAdmin,
            UseShellExecute = script.NeedsAdmin,
            RedirectStandardOutput = !script.NeedsAdmin,
            RedirectStandardError = !script.NeedsAdmin,
        };
        if (!script.NeedsAdmin)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        if (script.NeedsAdmin)
        {
            // UAC elevation. Windows will prompt the user.
            psi.Verb = "runas";
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        _logger.Information("Running lifecycle script: {File} {Args} (admin={Admin}, wd={Wd})",
            filename, string.Join(" ", args), script.NeedsAdmin, gameFolder);

        using var process = new Process { StartInfo = psi };

        var combinedBuffer = new StringBuilder();

        if (!script.NeedsAdmin)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    combinedBuffer.AppendLine(e.Data);
                    onOutputLine?.Invoke(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    combinedBuffer.AppendLine(e.Data);
                    onOutputLine?.Invoke(e.Data);
                }
            };
        }

        try
        {
            if (!process.Start())
                throw new InvalidOperationException(
                    $"Failed to start lifecycle script process ({filename}).");
        }
        catch (System.ComponentModel.Win32Exception ex) when (script.NeedsAdmin && ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED: user denied UAC. Surface as a clean cancellation
            // rather than a raw Win32Exception.
            throw new OperationCanceledException(
                "User declined the elevation prompt for the lifecycle script.", ex);
        }

        if (!script.NeedsAdmin)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var succeeded = process.ExitCode == 0;
        _logger.Information("Lifecycle script exit code: {Code} ({Result})",
            process.ExitCode, succeeded ? "ok" : "failed");

        return new LifecycleScriptResult(process.ExitCode, succeeded, combinedBuffer.ToString());
    }

    /// <summary>
    /// Picks the runner + builds the argument list for a given file extension. Q5=C: working
    /// dir is the game folder; <c>--gameFolder</c> and <c>--modFolder</c> are passed as named
    /// args to .exe/.ps1, positional to .cmd/.bat.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Args) BuildCommand(
        string ext, string scriptAbsolutePath, string gameFolder, string modFolder)
    {
        return ext switch
        {
            ".exe" => (
                scriptAbsolutePath,
                new[] { "--gameFolder", gameFolder, "--modFolder", modFolder }),

            ".ps1" => (
                "powershell.exe",
                new[]
                {
                    "-NoProfile",
                    "-ExecutionPolicy", "Bypass",
                    "-File", scriptAbsolutePath,
                    "-gameFolder", gameFolder,
                    "-modFolder", modFolder
                }),

            ".cmd" or ".bat" => (
                "cmd.exe",
                new[] { "/c", scriptAbsolutePath, gameFolder, modFolder }),

            _ => throw new InvalidOperationException(
                $"Unsupported lifecycle script extension '{ext}'. Allowed: .exe, .ps1, .cmd, .bat")
        };
    }

    /// <summary>
    /// Validates that a manifest's lifecycle script has an allowed extension and that the
    /// referenced executable exists inside the extracted ZIP staging dir. Throws when not.
    /// </summary>
    public static void ValidateScriptInStaging(LifecycleScript script, string stagingDir)
    {
        if (string.IsNullOrWhiteSpace(script.Executable))
            throw new InvalidOperationException("Lifecycle script has no executable path.");

        var ext = Path.GetExtension(script.Executable).ToLowerInvariant();
        if (ext is not ".exe" and not ".ps1" and not ".cmd" and not ".bat")
            throw new InvalidOperationException(
                $"Lifecycle script extension '{ext}' is not allowed. Use .exe, .ps1, .cmd, or .bat.");

        // Reject path traversal — script must live inside the staging dir. PathSafety handles
        // trailing-separator and case aliases so a legitimate staging path can't false-fail.
        var scriptFull = Path.GetFullPath(Path.Combine(stagingDir, script.Executable));
        if (!PathSafety.IsContained(stagingDir, scriptFull))
            throw new InvalidOperationException(
                $"Lifecycle script path '{script.Executable}' escapes the package staging dir.");

        if (!File.Exists(scriptFull))
            throw new FileNotFoundException(
                $"Lifecycle script declared at '{script.Executable}' wasn't found inside the package.",
                scriptFull);

        if (string.IsNullOrWhiteSpace(script.What) ||
            string.IsNullOrWhiteSpace(script.Why) ||
            string.IsNullOrWhiteSpace(script.Modifies))
            throw new InvalidOperationException(
                "Lifecycle script must include all three description fields (what, why, modifies).");
    }
}
