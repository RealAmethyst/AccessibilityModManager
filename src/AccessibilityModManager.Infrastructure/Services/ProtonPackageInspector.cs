using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Holds the exact verified package bytes used for both preflight and installation.</summary>
public sealed class PreparedProtonPackage(string path, Manifest manifest,
    VisualCppArchitecture requiredVisualCppRuntimes) : IDisposable
{
    public string Path { get; } = path;
    public Manifest Manifest { get; } = manifest;
    public VisualCppArchitecture RequiredVisualCppRuntimes { get; } = requiredVisualCppRuntimes;

    public void Dispose()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}

public sealed class ProtonPackageInspector(ILogger logger)
{
    private const long MaxManifestBytes = 1024 * 1024;

    public async Task<PreparedProtonPackage> PrepareAsync(
        string packagePath, ModRelease release, CancellationToken ct = default)
    {
        if (ReleaseTarget.Normalize(release.TargetPlatform) != ReleaseTarget.Proton)
            throw new InvalidOperationException("A Proton setup requires a Proton release.");

        var staged = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "amm-proton-package-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var source = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var destination = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(destination, ct);

            await using var stream = File.OpenRead(staged);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Proton package SHA-256 does not match its selected release.");

            stream.Position = 0;
            var report = PluginPackageValidation.Validate(stream, release.PluginId,
                release.GameId, release.Version, logger);
            if (!report.IsValid)
                throw new InvalidDataException("The Proton package is invalid: " + string.Join(" ", report.Errors));

            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = archive.GetEntry(PluginPackageValidation.ManifestEntryName)
                ?? throw new InvalidDataException("The Proton package has no manifest.json.");
            if (entry.Length > MaxManifestBytes)
                throw new InvalidDataException("The Proton package manifest is too large.");
            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, true));
            var manifest = new ManifestParser(logger).Parse(await reader.ReadToEndAsync(ct));
            if (ReleaseTarget.Normalize(manifest.TargetPlatform) != ReleaseTarget.Proton ||
                manifest.ProtonLaunch is null)
                throw new InvalidDataException("This Proton package has no supported Steam launch setup.");
            return new PreparedProtonPackage(staged, manifest,
                ProtonVisualCppRuntimeDetector.Detect(archive));
        }
        catch
        {
            if (File.Exists(staged)) File.Delete(staged);
            throw;
        }
    }
}
