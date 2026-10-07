using System.Formats.Tar;
using System.IO.Compression;
using System.Security;

namespace AccessibilityModManager.Infrastructure.Security;

/// <summary>Extract regular files from a gzip-compressed tar without trusting archive paths, links or ownership.</summary>
public sealed class SafeTarGZipExtractor
{
    public async Task ExtractAsync(Stream archive, string targetDirectory, CancellationToken ct = default,
        long maximumBytes = 8L * 1024 * 1024 * 1024, int maximumEntries = 100000)
    {
        var root = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(root);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        await using var reader = new TarReader(gzip, leaveOpen: true);
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        long bytes = 0;
        int count = 0;
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken: ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (++count > maximumEntries || entry.Length > maximumBytes - bytes)
                throw new InvalidDataException("The portable-app archive exceeds its extraction limits.");
            bytes += entry.Length;
            if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new SecurityException("Portable-app tar archives cannot contain links or special files.");
            var name = entry.Name;
            if (name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Split('/').Contains(".."))
                throw new SecurityException($"Tar entry '{name}' has an unsafe path.");
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToArray();
            if (parts.Length == 0)
            {
                if (entry.EntryType == TarEntryType.Directory) continue;
                throw new SecurityException("A tar file entry cannot name the extraction root.");
            }
            var destination = PathSafety.CombineContained(root, Path.Combine(parts));
            if (!seen.Add(destination)) throw new SecurityException($"Tar entry '{name}' is duplicated.");
            PathSafety.EnsureNoReparseTraversal(root, destination, "portable-app tar entry");
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, ct);
                if (output.Length != entry.Length) throw new InvalidDataException("A tar entry is incomplete.");
            }
            // Preserve the executable bit needed by launchers/helpers, never setuid/setgid or ownership.
            if (OperatingSystem.IsLinux() && (entry.Mode & UnixFileMode.UserExecute) != 0)
                File.SetUnixFileMode(destination, File.GetUnixFileMode(destination) | UnixFileMode.UserExecute);
        }
    }
}
