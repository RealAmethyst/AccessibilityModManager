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

    public async Task ExtractAsync(string zipPath, string targetDirectory, CancellationToken ct = default)
    {
        var fullTargetPath = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(fullTargetPath);

        _logger.Information("Extracting {ZipPath} to {TargetDir}", zipPath, fullTargetPath);

        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // Skip directory entries
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var destinationPath = Path.GetFullPath(Path.Combine(fullTargetPath, entry.FullName));

            // Zip slip protection: ensure the resolved path stays inside the target
            if (!destinationPath.StartsWith(fullTargetPath + Path.DirectorySeparatorChar) &&
                destinationPath != fullTargetPath)
            {
                _logger.Error("Zip slip detected: entry {Entry} resolves to {Path} which is outside {Target}",
                    entry.FullName, destinationPath, fullTargetPath);
                throw new SecurityException(
                    $"Zip entry '{entry.FullName}' would extract outside target directory. Archive may be malicious.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            using var entryStream = entry.Open();
            using var fileStream = File.Create(destinationPath);
            await entryStream.CopyToAsync(fileStream, ct);

            _logger.Debug("Extracted: {Entry}", entry.FullName);
        }

        _logger.Information("Extraction complete: {Count} entries", archive.Entries.Count);
    }
}
