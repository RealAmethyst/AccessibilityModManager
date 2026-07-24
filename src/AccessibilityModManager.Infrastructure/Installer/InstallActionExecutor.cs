using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Installer;

/// <summary>
/// Executes manifest install actions (copyFile, copyFolder, replaceFile).
/// All paths are validated to stay within the game directory.
/// Returns FileChange records for the receipt.
/// </summary>
public sealed class InstallActionExecutor
{
    private readonly BackupManager _backupManager;
    private readonly ILogger _logger;

    public InstallActionExecutor(BackupManager backupManager, ILogger logger)
    {
        _backupManager = backupManager;
        _logger = logger;
    }

    public List<FileChange> Execute(
        InstallAction action,
        string packageExtractDir,
        string gameInstallPath,
        string backupFolder)
    {
        return action switch
        {
            CopyFileAction copyFile => ExecuteCopyFile(copyFile, packageExtractDir, gameInstallPath, backupFolder),
            CopyFolderAction copyFolder => ExecuteCopyFolder(copyFolder, packageExtractDir, gameInstallPath, backupFolder),
            ReplaceFileAction replaceFile => ExecuteReplaceFile(replaceFile, packageExtractDir, gameInstallPath, backupFolder),
            _ => throw new InvalidOperationException($"Unknown install action type: {action.GetType().Name}")
        };
    }

    private List<FileChange> ExecuteCopyFile(CopyFileAction action, string packageDir, string gameDir, string backupFolder)
    {
        var sourcePath = ResolveSafe(packageDir, action.Source, "package source");
        var targetPath = ResolveSafe(gameDir, action.Target, "install target");
        var targetRelative = action.Target;

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Package source file not found: {action.Source}");

        var changes = new List<FileChange>();
        var existed = File.Exists(targetPath);

        if (existed)
        {
            var backupRel = _backupManager.BackupFile(gameDir, targetRelative, backupFolder);
            changes.Add(new FileChange
            {
                Type = ChangeType.Replaced,
                RelativePath = targetRelative,
                BackupRelativePath = backupRel
            });
        }
        else
        {
            changes.Add(new FileChange
            {
                Type = ChangeType.Added,
                RelativePath = targetRelative
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(sourcePath, targetPath, overwrite: true);
        _logger.Information("CopyFile: {Source} -> {Target}", action.Source, action.Target);

        return changes;
    }

    private List<FileChange> ExecuteCopyFolder(CopyFolderAction action, string packageDir, string gameDir, string backupFolder)
    {
        var sourceDir = ResolveSafe(packageDir, action.SourceDir, "package source directory");
        var targetDir = ResolveSafe(gameDir, action.TargetDir, "install target directory");

        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Package source directory not found: {action.SourceDir}");

        var changes = new List<FileChange>();

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativeToSource = Path.GetRelativePath(sourceDir, sourceFile);
            var combined = Path.Combine(action.TargetDir, relativeToSource);
            // ResolveSafe validates the combined relative path against gameDir consistently with CopyFile/ReplaceFile.
            var targetFile = ResolveSafe(gameDir, combined, $"copyFolder entry '{relativeToSource}'");
            var relativeToGame = Path.GetRelativePath(gameDir, targetFile);

            var existed = File.Exists(targetFile);

            if (existed)
            {
                var backupRel = _backupManager.BackupFile(gameDir, relativeToGame, backupFolder);
                changes.Add(new FileChange
                {
                    Type = ChangeType.Replaced,
                    RelativePath = relativeToGame,
                    BackupRelativePath = backupRel
                });
            }
            else
            {
                changes.Add(new FileChange
                {
                    Type = ChangeType.Added,
                    RelativePath = relativeToGame
                });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: true);
        }

        _logger.Information("CopyFolder: {Source} -> {Target} ({Count} files)",
            action.SourceDir, action.TargetDir, changes.Count);

        return changes;
    }

    private List<FileChange> ExecuteReplaceFile(ReplaceFileAction action, string packageDir, string gameDir, string backupFolder)
    {
        var sourcePath = ResolveSafe(packageDir, action.Source, "package source");
        var targetPath = ResolveSafe(gameDir, action.Target, "install target");
        var targetRelative = action.Target;

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Package source file not found: {action.Source}");

        var changes = new List<FileChange>();

        if (File.Exists(targetPath))
        {
            // Always back up an existing file, even if the manifest set backup=false. A replaced
            // file with no backup can't be restored on uninstall/rollback — it would silently
            // destroy the user's original, which is never an acceptable outcome.
            var backupRel = _backupManager.BackupFile(gameDir, targetRelative, backupFolder);
            changes.Add(new FileChange
            {
                Type = ChangeType.Replaced,
                RelativePath = targetRelative,
                BackupRelativePath = backupRel
            });
        }
        else
        {
            changes.Add(new FileChange
            {
                Type = ChangeType.Added,
                RelativePath = targetRelative
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(sourcePath, targetPath, overwrite: true);
        _logger.Information("ReplaceFile: {Source} -> {Target}", action.Source, action.Target);

        return changes;
    }

    /// <summary>
    /// Resolves a relative path within a base directory, and validates it doesn't escape.
    /// </summary>
    private static string ResolveSafe(string baseDir, string relativePath, string context)
    {
        var fullPath = Path.GetFullPath(Path.Combine(baseDir, relativePath));
        var fullBase = Path.GetFullPath(baseDir);

        if (!fullPath.StartsWith(fullBase + Path.DirectorySeparatorChar) && fullPath != fullBase)
        {
            throw new InvalidOperationException(
                $"Path escape detected in {context}: '{relativePath}' resolves outside '{baseDir}'");
        }

        return fullPath;
    }
}
