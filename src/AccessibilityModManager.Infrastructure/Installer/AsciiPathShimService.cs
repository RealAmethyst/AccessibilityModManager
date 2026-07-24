using System.Diagnostics;
using System.Text;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Installer;

/// <summary>
/// Junction mechanics for <see cref="AsciiPathShim"/>. Junctions are created with
/// <c>cmd /c mklink /J</c> because .NET has no junction API (only symbolic links, which need
/// admin and are treated differently by the OAuth callback handling — see end-user-setup.md §4).
/// This service never deletes anything: removing a junction with a recursive delete would walk
/// into and destroy the real install, so cleanup (if ever wanted) is a separate, explicit
/// <c>rmdir</c>-the-link operation, not this service's job.
/// </summary>
public sealed class AsciiPathShimService : IAsciiPathShimService
{
    private readonly ILogger _logger;

    public AsciiPathShimService(ILogger logger)
    {
        _logger = logger;
    }

    public string GetJunctionPath(AsciiPathShim shim, string realInstallPath)
    {
        // The junction name comes from the unsigned plugin index. A rooted or
        // separator-containing value would make Path.Combine discard the drive root and aim the
        // junction anywhere — require a single ASCII folder name (ASCII being the shim's entire
        // reason to exist).
        var name = Security.PathSafety.EnsureLeafFileName(shim.JunctionName, "AsciiPathShim junction name");
        if (name.Any(c => c > 127))
            throw new InvalidOperationException(
                $"AsciiPathShim junction name '{name}' must contain only ASCII characters.");

        var root = Path.GetPathRoot(Path.GetFullPath(realInstallPath));
        if (string.IsNullOrEmpty(root))
            throw new InvalidOperationException(
                $"Can't determine the drive of '{realInstallPath}' to place the ASCII junction.");
        return Path.Combine(root, name);
    }

    public bool JunctionPathExists(string junctionPath) => Directory.Exists(junctionPath);

    public string? GetJunctionTarget(string junctionPath)
    {
        try
        {
            // returnFinalTarget:false → the junction's immediate reparse target (reads the link's
            // own metadata, no traversal into the target). Non-links return null; missing paths throw.
            var target = Directory.ResolveLinkTarget(junctionPath, returnFinalTarget: false);
            var full = target?.FullName;
            if (string.IsNullOrEmpty(full)) return null;

            // Junction targets are normally returned as a clean drive path, but some reparse points
            // carry the NT-namespace prefix (\??\ or \\?\); strip it so callers can compare paths.
            if (full.StartsWith(@"\??\", StringComparison.Ordinal) ||
                full.StartsWith(@"\\?\", StringComparison.Ordinal))
                full = full[4..];

            return full;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Couldn't resolve junction target for {Junction}", junctionPath);
            return null;
        }
    }

    public void RemoveJunctionLink(string junctionPath)
    {
        // Non-recursive delete removes ONLY the reparse point, never the real files it targets.
        // Directory.Delete(path, recursive: true) on a junction would walk into and destroy the
        // target — that's the invariant this whole service exists to protect.
        if (Directory.Exists(junctionPath))
        {
            Directory.Delete(junctionPath, recursive: false);
            _logger.Information("Removed junction link {Junction} (target untouched)", junctionPath);
        }
    }

    public async Task CreateJunctionAsync(string junctionPath, string realTargetPath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(junctionPath);
        psi.ArgumentList.Add(realTargetPath);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start cmd.exe to create the junction.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"mklink /J failed (exit {process.ExitCode}) creating '{junctionPath}' -> '{realTargetPath}'. " +
                $"Output: {output.ToString().Trim()}");

        _logger.Information("Created ASCII junction {Junction} -> {Target}", junctionPath, realTargetPath);
    }
}
