using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

public sealed record BuiltPackage(string ZipPath, int FileCount, long TotalBytes);

/// <summary>
/// Inputs for the three lifecycle script slots a release can declare. Each pair carries the
/// public <see cref="LifecycleScript"/> that ends up in <c>manifest.json</c> (only when the
/// slot is enabled) plus the optional absolute path to the script file on the author's
/// machine. When the absolute path is set, the builder copies that exact file into the
/// wrapped ZIP at <c>script.Executable</c>, regardless of whether it lives under the source
/// folder. When the absolute path is null, the builder falls back to expecting the file at
/// the corresponding location inside the source folder (legacy in-folder flow).
/// </summary>
public sealed record LifecycleScriptInputs(
    LifecycleScript? PreInstall = null,
    string? PreInstallSourcePath = null,
    LifecycleScript? PostInstall = null,
    string? PostInstallSourcePath = null,
    LifecycleScript? PostUninstall = null,
    string? PostUninstallSourcePath = null);

/// <summary>
/// Takes a source folder of mod files, generates a manager-format manifest.json describing
/// where each top-level entry installs to, and writes a wrapped ZIP containing
/// <c>manifest.json</c> at the root and the source contents under <c>files/</c>. The manager's
/// installer extracts the ZIP, reads the manifest, and runs each install action against the
/// game folder.
/// </summary>
public sealed class ManifestBuilderService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger _logger;

    public ManifestBuilderService(ILogger logger)
    {
        _logger = logger;
    }

    public static string GetBuildsDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager-Author",
        "builds");

    public async Task<BuiltPackage> BuildPackageAsync(
        string sourceFolder,
        string gameId,
        string pluginId,
        string version,
        IList<Dependency> dependencies,
        string outputZipPath,
        LifecycleScriptInputs? scripts = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"Source folder not found: {sourceFolder}");

        sourceFolder = Path.GetFullPath(sourceFolder);

        var topLevelEntries = Directory
            .EnumerateFileSystemEntries(sourceFolder, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A pure script-only mod is a legitimate shape (e.g. a release that just toggles a
        // registry key). Require non-empty content only when there are no scripts to run.
        var hasAnyScript =
            scripts?.PreInstall is not null ||
            scripts?.PostInstall is not null ||
            scripts?.PostUninstall is not null;
        if (topLevelEntries.Count == 0 && !hasAnyScript)
            throw new InvalidOperationException(
                "Source folder is empty and no lifecycle script is enabled. Put your mod files in there first (e.g. version.dll, MelonLoader/, Mods/), or enable a script on the Scripts tab.");

        // Verify each declared script can be located: either via the absolute path the author
        // picked with Browse, or — failing that — under the source folder. Catches typos and
        // missing files before the user uploads, since the manager's installer rejects
        // manifests whose scripts aren't in the wrapped ZIP.
        var preInstall = scripts?.PreInstall;
        var postInstall = scripts?.PostInstall;
        var postUninstall = scripts?.PostUninstall;
        ValidateScriptIsBundled(preInstall, scripts?.PreInstallSourcePath, sourceFolder, "Pre-install");
        ValidateScriptIsBundled(postInstall, scripts?.PostInstallSourcePath, sourceFolder, "Post-install");
        ValidateScriptIsBundled(postUninstall, scripts?.PostUninstallSourcePath, sourceFolder, "Post-uninstall");

        // Action source paths are relative to the staging dir's files/ subfolder — the
        // InstallerEngine passes <tempDir>/files as the executor's base path. Prepending
        // 'files/' here would resolve to <tempDir>/files/files/... and fail at install time.
        // (Lifecycle scripts use a different base — see ValidateScriptIsBundled.)
        var actions = new List<InstallAction>();
        foreach (var entry in topLevelEntries)
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                actions.Add(new CopyFolderAction
                {
                    SourceDir = name,
                    TargetDir = name
                });
            }
            else
            {
                actions.Add(new CopyFileAction
                {
                    Source = name,
                    Target = name
                });
            }
        }

        // Lifecycle scripts marked InstallToGameFolder need a copyFile action so the file
        // also lives in the game folder permanently. The script still bundles at its
        // declared in-package path (files/scripts/<basename>) so the runner can find it
        // from the temp staging dir; the install action just copies it out alongside the
        // rest of the mod's content.
        AppendInstallToGameFolderAction(actions, preInstall);
        AppendInstallToGameFolderAction(actions, postInstall);
        AppendInstallToGameFolderAction(actions, postUninstall);

        var manifest = new Manifest
        {
            GameId = gameId,
            PluginId = pluginId,
            ModVersion = version,
            InstallActions = actions,
            Dependencies = dependencies.ToList(),
            Verify = [],
            PreInstall = preInstall,
            PostInstall = postInstall,
            PostUninstall = postUninstall
        };

        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);

        var outputDir = Path.GetDirectoryName(outputZipPath) ?? ".";
        Directory.CreateDirectory(outputDir);
        if (File.Exists(outputZipPath)) File.Delete(outputZipPath);

        long totalBytes = 0;
        int fileCount = 0;
        var addedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var zipStream = new FileStream(outputZipPath, FileMode.CreateNew))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            // manifest.json at the ZIP root.
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using (var entryStream = manifestEntry.Open())
            await using (var writer = new StreamWriter(entryStream))
            {
                await writer.WriteAsync(manifestJson);
            }
            addedEntryNames.Add("manifest.json");
            fileCount++;
            totalBytes += manifestJson.Length;

            // Lifecycle scripts come first when the author picked them via Browse — those
            // files may live anywhere on disk, including outside the source folder, so we
            // bundle them explicitly. The source-folder enumeration below dedupes via
            // addedEntryNames, so a Browse-picked script that *also* happens to live inside
            // the source folder doesn't get written twice.
            await TryAddScriptFromAbsoluteAsync(zip, preInstall, scripts?.PreInstallSourcePath, addedEntryNames, ct,
                onAdded: bytes => { fileCount++; totalBytes += bytes; });
            await TryAddScriptFromAbsoluteAsync(zip, postInstall, scripts?.PostInstallSourcePath, addedEntryNames, ct,
                onAdded: bytes => { fileCount++; totalBytes += bytes; });
            await TryAddScriptFromAbsoluteAsync(zip, postUninstall, scripts?.PostUninstallSourcePath, addedEntryNames, ct,
                onAdded: bytes => { fileCount++; totalBytes += bytes; });

            // Mod content under files/, preserving the source folder's structure. Skip any
            // entry name already added by the lifecycle-script step so we don't bundle a
            // file twice when a Browse-picked script also lives inside the source folder.
            foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(sourceFolder, file).Replace('\\', '/');
                var entryName = $"files/{rel}";
                if (!addedEntryNames.Add(entryName)) continue;

                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);

                var fileInfo = new FileInfo(file);
                totalBytes += fileInfo.Length;
                fileCount++;

                await using var entryStream = entry.Open();
                await using var fileStream = File.OpenRead(file);
                await fileStream.CopyToAsync(entryStream, ct);
            }
        }

        _logger.Information("Built package {Path} with {Count} files, {Bytes} bytes",
            outputZipPath, fileCount, totalBytes);

        return new BuiltPackage(outputZipPath, fileCount, totalBytes);
    }

    /// <summary>
    /// Adds a <see cref="CopyFileAction"/> mapping the script's in-package path to the game
    /// folder root when the author opted into <see cref="LifecycleScript.InstallToGameFolder"/>.
    /// No-op otherwise. Source is the path inside <c>files/</c> (so the executor's base of
    /// <c>tempDir/files</c> resolves correctly); target is the basename so the file lands at
    /// the game folder root.
    /// </summary>
    private static void AppendInstallToGameFolderAction(List<InstallAction> actions, LifecycleScript? script)
    {
        if (script is null || !script.InstallToGameFolder) return;

        const string filesPrefix = "files/";
        var rel = script.Executable.Replace('\\', '/').TrimStart('/');
        if (!rel.StartsWith(filesPrefix, StringComparison.OrdinalIgnoreCase))
            return; // ValidateScriptIsBundled would have already thrown if this happened.

        var sourceInsideFiles = rel[filesPrefix.Length..];
        var basename = Path.GetFileName(sourceInsideFiles);
        actions.Add(new CopyFileAction
        {
            Source = sourceInsideFiles,
            Target = basename
        });
    }

    /// <summary>
    /// Copies the script file at <paramref name="absoluteSourcePath"/> into the ZIP at
    /// <c>script.Executable</c>. No-ops when the script isn't declared, the absolute path
    /// is empty, or another step already wrote that entry name. The author's source folder
    /// is the fallback when no absolute path is set, so we don't need to do anything here
    /// in that case — the source-folder enumeration step picks the file up.
    /// </summary>
    private static async Task TryAddScriptFromAbsoluteAsync(
        ZipArchive zip,
        LifecycleScript? script,
        string? absoluteSourcePath,
        HashSet<string> addedEntryNames,
        CancellationToken ct,
        Action<long> onAdded)
    {
        if (script is null || string.IsNullOrEmpty(absoluteSourcePath)) return;

        var entryName = script.Executable.Replace('\\', '/').TrimStart('/');
        if (!addedEntryNames.Add(entryName)) return;

        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        var fileInfo = new FileInfo(absoluteSourcePath);
        await using var entryStream = entry.Open();
        await using var fileStream = File.OpenRead(absoluteSourcePath);
        await fileStream.CopyToAsync(entryStream, ct);
        onAdded(fileInfo.Length);
    }

    /// <summary>
    /// Author declares scripts by their in-package path (<c>files/scripts/foo.ps1</c>). If
    /// they picked an absolute path with Browse, that file is the source of truth — verify
    /// it exists and matches the declared extension. Otherwise fall back to the legacy
    /// flow where the script must live inside the source folder at the matching path.
    /// </summary>
    private static void ValidateScriptIsBundled(
        LifecycleScript? script,
        string? absoluteSourcePath,
        string sourceFolder,
        string label)
    {
        if (script is null) return;

        if (string.IsNullOrWhiteSpace(script.Executable))
            throw new InvalidOperationException(
                $"{label}: executable path is empty.");

        var ext = Path.GetExtension(script.Executable).ToLowerInvariant();
        if (ext is not ".exe" and not ".ps1" and not ".cmd" and not ".bat")
            throw new InvalidOperationException(
                $"{label}: executable '{script.Executable}' has extension '{ext}'. " +
                "Allowed: .exe, .ps1, .cmd, or .bat.");

        const string filesPrefix = "files/";
        var rel = script.Executable.Replace('\\', '/').TrimStart('/');
        if (!rel.StartsWith(filesPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{label}: executable must be referenced as 'files/...' so it lives inside the package. " +
                $"Got '{script.Executable}'.");

        if (!string.IsNullOrEmpty(absoluteSourcePath))
        {
            if (!File.Exists(absoluteSourcePath))
                throw new FileNotFoundException(
                    $"{label} script source file is missing: '{absoluteSourcePath}'. " +
                    "Re-pick it on the Scripts tab.",
                    absoluteSourcePath);

            var pickedExt = Path.GetExtension(absoluteSourcePath).ToLowerInvariant();
            if (!string.Equals(pickedExt, ext, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{label}: picked file extension '{pickedExt}' doesn't match the script entry's '{ext}'. " +
                    "Re-pick the file on the Scripts tab so the in-package name matches.");
            return;
        }

        var inSource = rel[filesPrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(sourceFolder, inSource));
        if (!PathSafety.IsContained(sourceFolder, fullPath))
            throw new InvalidOperationException(
                $"{label}: '{script.Executable}' resolves outside the source folder.");

        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                $"{label} script '{script.Executable}' not found at '{fullPath}'. " +
                "Either place the file inside your source folder, or click Browse on the Scripts tab to pick it from anywhere.",
                fullPath);
    }
}
