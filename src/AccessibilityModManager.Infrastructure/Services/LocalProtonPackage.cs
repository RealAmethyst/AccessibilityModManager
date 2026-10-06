using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>A locally selected, unpublished Proton package for testing before catalog release.</summary>
public sealed record LocalProtonPackage(
    string Path, Manifest Manifest, GameDefinition Game, ModRelease Release)
{
    public static async Task<LocalProtonPackage> OpenAsync(
        string path, ILogger logger, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        stream.Position = 0;
        Manifest manifest;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            var entry = zip.GetEntry(PluginPackageValidation.ManifestEntryName)
                ?? throw new InvalidDataException("The package has no top-level manifest.json.");
            if (entry.Length > 1024 * 1024)
                throw new InvalidDataException("The package manifest is too large.");
            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, true));
            manifest = new ManifestParser(logger).Parse(await reader.ReadToEndAsync(ct));
        }
        if (ReleaseTarget.Normalize(manifest.TargetPlatform) != ReleaseTarget.Proton ||
            manifest.ProtonLaunch is null)
            throw new InvalidDataException("Choose a Proton package with Steam launch metadata.");
        stream.Position = 0;
        var report = PluginPackageValidation.Validate(stream, manifest.PluginId,
            manifest.GameId, manifest.ModVersion, logger);
        if (!report.IsValid)
            throw new InvalidDataException("Package validation failed: " + string.Join(" ", report.Errors));

        var launch = manifest.ProtonLaunch;
        var game = new GameDefinition
        {
            GameId = manifest.GameId,
            DisplayName = launch.GameDisplayName,
            SteamAppId = launch.SteamAppId,
            ExeName = launch.GameExecutable,
            Dependencies = manifest.Dependencies
        };
        var release = new ModRelease
        {
            GameId = manifest.GameId,
            PluginId = manifest.PluginId,
            Version = manifest.ModVersion,
            Channel = "local-preview",
            TargetPlatform = ReleaseTarget.Proton,
            Sha256 = hash
        };
        return new LocalProtonPackage(path, manifest, game, release);
    }
}
