using System.IO.Compression;
using System.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Security;

/// <summary>
/// Extracts ZIP files with zip-slip protection.
/// Every extracted path is validated to stay within the target directory.
/// </summary>
public sealed class SafeZipExtractor
{
    private readonly ILogger _logger;

    public SafeZipExtractor(ILogger logger)
    {
        _logger = logger;
    }

    public async Task ExtractAsync(string zipPath, string targetDirectory, CancellationToken ct = default,
        bool preserveExecutablePermissions = false)
    {
        await using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await ExtractAsync(stream, targetDirectory, ct, sourceLabel: zipPath, preserveExecutablePermissions: preserveExecutablePermissions);
    }

    /// <summary>
    /// Stream overload for callers that verified the archive's bytes and must extract those
    /// EXACT bytes — re-opening by path after a hash check would allow a swap in between.
    /// </summary>
    public async Task ExtractAsync(Stream zipStream, string targetDirectory, CancellationToken ct = default,
        string sourceLabel = "(stream)", bool preserveExecutablePermissions = false)
    {
        var fullTargetPath = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(fullTargetPath);

        _logger.Information("Extracting {ZipPath} to {TargetDir}", sourceLabel, fullTargetPath);

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Contains(':') ||
                normalized.Split('/').Any(part => part == ".."))
            {
                throw new SecurityException($"ZIP entry '{entry.FullName}' has an unsafe path.");
            }

            // ZIP paths use forward slashes; treat a trailing backslash as a directory too.
            if (normalized.EndsWith('/'))
                continue;

            var destinationPath = PathSafety.CombineContained(fullTargetPath,
                normalized.Replace('/', Path.DirectorySeparatorChar));

            if (!seen.Add(destinationPath))
                throw new SecurityException($"ZIP entry '{entry.FullName}' is duplicated.");

            // Zip slip protection: ensure the resolved path stays inside the target. PathSafety
            // handles trailing-separator roots (extracting to "D:\" must not false-positive).
            if (!PathSafety.IsContained(fullTargetPath, destinationPath))
            {
                _logger.Error("Zip slip detected: entry {Entry} resolves to {Path} which is outside {Target}",
                    entry.FullName, destinationPath, fullTargetPath);
                throw new SecurityException(
                    $"Zip entry '{entry.FullName}' would extract outside target directory. Archive may be malicious.");
            }

            // Physical containment: a junction/symlink between the target root and this entry
            // would redirect the write outside the folder the text says it stays in. The root
            // itself may be a link (shimmed install roots are); nothing deeper may be.
            PathSafety.EnsureNoReparseTraversal(fullTargetPath, destinationPath, $"zip entry '{entry.FullName}'");

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            using var entryStream = entry.Open();
            using var fileStream = File.Create(destinationPath);
            await entryStream.CopyToAsync(fileStream, ct);

            // Native portable apps can contain launchers and helper binaries. Retain only
            // their owner's execute bit; never restore setuid/setgid or archive write modes.
            if (preserveExecutablePermissions && OperatingSystem.IsLinux() &&
                ((entry.ExternalAttributes >> 16) & 0x40) != 0)
                File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(destinationPath) | UnixFileMode.UserExecute);

            _logger.Debug("Extracted: {Entry}", entry.FullName);
        }

        _logger.Information("Extraction complete: {Count} entries", archive.Entries.Count);
    }
}
