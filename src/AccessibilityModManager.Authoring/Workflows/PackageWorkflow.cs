using System.IO.Compression;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.Authoring.Workflows;

public sealed record PackageBuildRequest(
    string SourceFolder,
    string OutputZipPath,
    string PluginId,
    string GameId,
    string Version,
    IReadOnlyList<Dependency> Dependencies,
    LifecycleScriptInputs Scripts);

public sealed record PackageBuildPreview(
    string SourceFolder,
    string OutputZipPath,
    string PluginId,
    string GameId,
    string Version,
    int TopLevelEntryCount,
    bool HasLifecycleScripts);

public sealed record PackageInspection(
    string ZipPath,
    string Sha256,
    int FileCount,
    long TotalBytes,
    PackageValidationReport Validation);

public sealed class PackageWorkflow
{
    private readonly ManifestBuilderService _builder;
    private readonly Sha256HashService _hashes;
    private readonly ILogger _logger;

    public PackageWorkflow(
        ManifestBuilderService builder,
        Sha256HashService hashes,
        ILogger logger)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _hashes = hashes ?? throw new ArgumentNullException(nameof(hashes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public PackageBuildPreview PreviewBuild(PackageBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.PluginId, request.GameId, request.Version);
        ArgumentNullException.ThrowIfNull(request.Dependencies);
        ArgumentNullException.ThrowIfNull(request.Scripts);

        var source = ManifestBuilderService.ValidateBuildInputs(request.SourceFolder, request.Scripts);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputZipPath);
        var output = Path.GetFullPath(request.OutputZipPath);

        if (Directory.Exists(output))
            throw new InvalidOperationException($"Package output path is a directory: '{output}'.");
        if (!string.Equals(Path.GetExtension(output), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Package output must use the .zip extension.");
        if (PathSafety.IsContained(source, output))
        {
            throw new InvalidOperationException(
                "Package output cannot be inside the source folder because the ZIP would include or lock itself while being built.");
        }

        return new PackageBuildPreview(
            source,
            output,
            request.PluginId.Trim(),
            request.GameId.Trim(),
            request.Version.Trim(),
            Directory.EnumerateFileSystemEntries(source, "*", SearchOption.TopDirectoryOnly).Count(),
            request.Scripts.PreInstall is not null ||
            request.Scripts.PostInstall is not null ||
            request.Scripts.PostUninstall is not null);
    }

    public async Task<PackageInspection> BuildAsync(
        PackageBuildRequest request,
        CancellationToken ct)
    {
        var preview = PreviewBuild(request);
        ct.ThrowIfCancellationRequested();

        try
        {
            await _builder.BuildPackageAsync(
                preview.SourceFolder,
                preview.GameId,
                preview.PluginId,
                preview.Version,
                request.Dependencies.ToList(),
                preview.OutputZipPath,
                request.Scripts,
                ct);

            var inspection = await ValidateAsync(
                preview.OutputZipPath,
                preview.PluginId,
                preview.GameId,
                preview.Version,
                ct);

            if (!inspection.Validation.IsValid)
            {
                throw new InvalidOperationException(
                    "The finished package failed the manager's pre-publish validation:" +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, inspection.Validation.Errors));
            }

            return inspection;
        }
        catch
        {
            TryDelete(preview.OutputZipPath);
            throw;
        }
    }

    public async Task<PackageInspection> ValidateAsync(
        string zipPath,
        string expectedPluginId,
        string expectedGameId,
        string expectedVersion,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ValidateIdentity(expectedPluginId, expectedGameId, expectedVersion);

        var fullPath = Path.GetFullPath(zipPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Package ZIP not found: {fullPath}", fullPath);

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        ct.ThrowIfCancellationRequested();
        var report = PluginPackageValidation.Validate(
            stream,
            expectedPluginId.Trim(),
            expectedGameId.Trim(),
            expectedVersion.Trim(),
            _logger);

        var (fileCount, totalBytes) = InspectEntries(stream);
        var sha256 = await _hashes.ComputeAsync(stream, ct);

        return new PackageInspection(fullPath, sha256, fileCount, totalBytes, report);
    }

    private static (int FileCount, long TotalBytes) InspectEntries(Stream stream)
    {
        if (stream.CanSeek)
            stream.Position = 0;

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var files = archive.Entries
                .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal))
                .ToArray();
            return (files.Length, files.Sum(entry => entry.Length));
        }
        catch (InvalidDataException)
        {
            return (0, 0);
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = 0;
        }
    }

    private static void ValidateIdentity(string pluginId, string gameId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the primary build or validation exception. A leftover failed package is
            // still logged by the caller and will never be returned as publishable output.
        }
    }
}
