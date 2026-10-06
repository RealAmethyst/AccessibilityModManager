using System.IO.Compression;
using System.Security.Cryptography;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// A verified pair of Windows Prism and Linux Wine bridge archives. Each release and architecture
/// has its own cache entry because the bridge ABI is not interchangeable between releases.
/// </summary>
public sealed record PrismBridgeRelease(
    string Version,
    string Architecture,
    Uri LinuxArchiveUrl,
    string LinuxArchiveSha256,
    Uri WindowsArchiveUrl,
    string WindowsArchiveSha256)
{
    public IReadOnlyList<string> BridgeNames { get; init; } =
        ["prism_orca_bridge", "prism_speech_dispatcher_bridge", "prism_spiel_bridge"];
    public Uri? PlaceholderArchiveUrl { get; init; }
    public string? PlaceholderArchiveSha256 { get; init; }

    public static PrismBridgeRelease V0183X64 { get; } = new(
        "0.18.3",
        "x64",
        new Uri("https://github.com/ethindp/prism/releases/download/v0.18.3/prism-linux-x64.zip"),
        "2b3050c89eaa4fb6c452377ec155ec7b71f444b424b547e04da238575d98a31c",
        new Uri("https://github.com/ethindp/prism/releases/download/v0.18.3/prism-windows-x64.zip"),
        "13d3c9d1d524b0737cde261c6468845753e36b6c5c2637e7d26bfb8bf4d8783f");
}

public sealed record PrismBridgeBundle(
    string Root,
    string WindowsPrismDll,
    string WindowsTolkDll,
    string WindowsPlaceholdersDirectory,
    string HostModulesDirectory,
    string NoticesDirectory);

/// <summary>
/// Downloads and stages the official matching Prism release artifacts once. This only prepares
/// shared files; installing a mod's Windows DLL, placing its placeholders, and configuring its
/// Steam launch environment are separate, per-game operations.
/// </summary>
public sealed class PrismBridgeProvisioner(HttpClient httpClient, string cacheRoot)
{
    private const long MaxArchiveBytes = 128L * 1024 * 1024;
    private static readonly string[] AllowedBridgeNames =
    [
        "prism_orca_bridge",
        "prism_speech_dispatcher_bridge",
        "prism_spiel_bridge"
    ];

    public async Task<PrismBridgeBundle> ProvisionAsync(
        PrismBridgeRelease release, CancellationToken ct = default)
    {
        EnsureLinuxRelease(release);
        var root = BundleRoot(release);
        if (Directory.Exists(root)) return ValidateExisting(root, release);

        Directory.CreateDirectory(cacheRoot);
        var downloadDir = Path.Combine(cacheRoot, ".downloads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadDir);
        try
        {
            var linuxArchive = Path.Combine(downloadDir, "linux.zip");
            var windowsArchive = Path.Combine(downloadDir, "windows.zip");
            var placeholderArchive = release.PlaceholderArchiveUrl is null ? null :
                Path.Combine(downloadDir, "placeholders.zip");
            await DownloadAsync(release.LinuxArchiveUrl, linuxArchive, ct);
            await DownloadAsync(release.WindowsArchiveUrl, windowsArchive, ct);
            if (placeholderArchive is not null)
                await DownloadAsync(release.PlaceholderArchiveUrl!, placeholderArchive, ct);
            return await ProvisionFromArchivesAsync(release, linuxArchive, windowsArchive, ct,
                placeholderArchive);
        }
        finally
        {
            Directory.Delete(downloadDir, recursive: true);
        }
    }

    /// <summary>Stages already downloaded official archives. Useful for offline packaging.</summary>
    public async Task<PrismBridgeBundle> ProvisionFromArchivesAsync(
        PrismBridgeRelease release, string linuxArchivePath, string windowsArchivePath,
        CancellationToken ct = default, string? placeholderArchivePath = null)
    {
        EnsureLinuxRelease(release);
        var root = BundleRoot(release);
        if (Directory.Exists(root)) return ValidateExisting(root, release);

        await VerifyArchiveAsync(linuxArchivePath, release.LinuxArchiveSha256, ct);
        await VerifyArchiveAsync(windowsArchivePath, release.WindowsArchiveSha256, ct);
        if (release.PlaceholderArchiveUrl is not null)
        {
            if (placeholderArchivePath is null)
                throw new InvalidDataException("This Prism release needs its pinned placeholder archive.");
            await VerifyArchiveAsync(placeholderArchivePath, release.PlaceholderArchiveSha256!, ct);
        }

        Directory.CreateDirectory(cacheRoot);
        var staging = Path.Combine(cacheRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var linux = ZipFile.OpenRead(linuxArchivePath);
            using var windows = ZipFile.OpenRead(windowsArchivePath);
            using var placeholders = ZipFile.OpenRead(placeholderArchivePath ?? linuxArchivePath);

            ExtractExactly(windows, "dynamic/release/bin/prism.dll",
                Path.Combine(staging, "windows", "prism.dll"));
            ExtractExactly(windows, "dynamic/release/bin/tolk.dll",
                Path.Combine(staging, "windows", "tolk.dll"));
            foreach (var name in release.BridgeNames)
            {
                ct.ThrowIfCancellationRequested();
                ExtractExactly(placeholders, $"dynamic/release/wine/placeholders/{name}.dll",
                    Path.Combine(staging, "windows", "placeholders", name + ".dll"));
                ExtractExactly(linux, $"dynamic/release/wine/{name}.dll.so",
                    Path.Combine(staging, "wine", name + ".dll.so"));
            }

            ExtractExactly(windows, "NOTICE", Path.Combine(staging, "notices", "NOTICE"));
            foreach (var entry in windows.Entries.Where(e =>
                         e.FullName.StartsWith("LICENSES/", StringComparison.Ordinal) &&
                         !e.FullName.EndsWith('/')))
            {
                ct.ThrowIfCancellationRequested();
                var target = PathSafety.CombineContained(Path.Combine(staging, "notices"), entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
            }

            var hashes = ExpectedFiles(staging, release).ToDictionary(
                path => Path.GetRelativePath(staging, path).Replace('\\', '/'),
                path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
            var marker = Path.Combine(staging, "bundle.sha256");
            File.WriteAllLines(marker, hashes.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Value}  {kv.Key}"));

            ct.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(staging, root);
            }
            catch (IOException) when (Directory.Exists(root))
            {
                // A second manager finished provisioning the same release first.
                return ValidateExisting(root, release);
            }
            return ValidateExisting(root, release);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void EnsureLinuxRelease(PrismBridgeRelease release)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Prism Wine bridges require Linux.");
        if (string.IsNullOrWhiteSpace(release.Version) ||
            release.Version.IndexOfAny(['/', '\\', ':']) >= 0 ||
            release.Architecture is not ("x64" or "x86"))
            throw new InvalidOperationException("Invalid Prism release identity.");
        if (release.LinuxArchiveUrl.Scheme != Uri.UriSchemeHttps ||
            release.WindowsArchiveUrl.Scheme != Uri.UriSchemeHttps ||
            release.PlaceholderArchiveUrl is not null &&
            release.PlaceholderArchiveUrl.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Prism archives must use HTTPS.");
        if (!IsSha256(release.LinuxArchiveSha256) || !IsSha256(release.WindowsArchiveSha256) ||
            release.PlaceholderArchiveUrl is not null &&
            (release.PlaceholderArchiveSha256 is null ||
             !IsSha256(release.PlaceholderArchiveSha256)))
            throw new InvalidOperationException("Prism archives need pinned SHA-256 digests.");
        if (release.BridgeNames.Count == 0 ||
            release.BridgeNames.Distinct(StringComparer.Ordinal).Count() != release.BridgeNames.Count ||
            release.BridgeNames.Any(name => !AllowedBridgeNames.Contains(name, StringComparer.Ordinal)))
            throw new InvalidOperationException("Prism bridge names are invalid.");
    }

    private string BundleRoot(PrismBridgeRelease release) =>
        Path.Combine(cacheRoot, $"prism-{release.Version}-{release.Architecture}-shim1");

    private async Task DownloadAsync(Uri url, string path, CancellationToken ct)
    {
        // Downloading is handled by the caller's HttpClient so it can apply the manager's
        // normal network policy and diagnostics. Verify the final URL after redirects too.
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Prism archive download redirected away from HTTPS.");
        if (response.Content.Headers.ContentLength > MaxArchiveBytes)
            throw new InvalidDataException("Prism archive exceeds the download size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            total += read;
            if (total > MaxArchiveBytes)
                throw new InvalidDataException("Prism archive exceeds the download size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static async Task VerifyArchiveAsync(string path, string expectedHash, CancellationToken ct)
    {
        if (new FileInfo(path).Length > MaxArchiveBytes)
            throw new InvalidDataException("Prism archive exceeds the size limit.");
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Prism archive SHA-256 mismatch: {Path.GetFileName(path)}");
    }

    private static void ExtractExactly(ZipArchive archive, string entryName, string target)
    {
        var matches = archive.Entries.Where(e => e.FullName == entryName).ToArray();
        if (matches.Length != 1 || matches[0].Length == 0)
            throw new InvalidDataException($"Prism archive is missing one unique {entryName} file.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        matches[0].ExtractToFile(target);
    }

    private static PrismBridgeBundle ValidateExisting(string root, PrismBridgeRelease release)
    {
        var marker = Path.Combine(root, "bundle.sha256");
        if (!File.Exists(marker))
            throw new InvalidDataException("Prism bridge cache is incomplete. Close games using it before repair.");
        var actualFiles = ExpectedFiles(root, release).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        var lines = File.ReadAllLines(marker);
        if (lines.Length != actualFiles.Count)
            throw new InvalidDataException("Prism bridge cache file list is incomplete.");
        foreach (var line in lines)
        {
            if (line.Length < 67 || line[64..66] != "  ")
                throw new InvalidDataException("Prism bridge cache marker is malformed.");
            var relative = line[66..];
            if (!actualFiles.Remove(relative))
                throw new InvalidDataException("Prism bridge cache marker has an unexpected file.");
            var path = PathSafety.CombineContained(root, relative);
            if (!File.Exists(path))
                throw new InvalidDataException($"Prism bridge cache file is missing: {relative}");
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            if (!actual.Equals(line[..64], StringComparison.Ordinal))
                throw new InvalidDataException($"Prism bridge cache file changed: {relative}");
        }
        return new PrismBridgeBundle(root,
            Path.Combine(root, "windows", "prism.dll"),
            Path.Combine(root, "windows", "tolk.dll"),
            Path.Combine(root, "windows", "placeholders"),
            Path.Combine(root, "wine"),
            Path.Combine(root, "notices"));
    }

    private static IEnumerable<string> ExpectedFiles(string root, PrismBridgeRelease release)
    {
        yield return Path.Combine(root, "windows", "prism.dll");
        yield return Path.Combine(root, "windows", "tolk.dll");
        foreach (var name in release.BridgeNames)
        {
            yield return Path.Combine(root, "windows", "placeholders", name + ".dll");
            yield return Path.Combine(root, "wine", name + ".dll.so");
        }
        yield return Path.Combine(root, "notices", "NOTICE");
        var licensesDir = Path.Combine(root, "notices", "LICENSES");
        if (!Directory.Exists(licensesDir))
            throw new InvalidDataException("Prism bridge cache is missing its licenses.");
        foreach (var path in Directory.EnumerateFiles(licensesDir, "*", SearchOption.AllDirectories))
            yield return path;
    }

    private static bool IsSha256(string value) => value.Length == 64 &&
        value.All(c => char.IsAsciiHexDigit(c));
}
