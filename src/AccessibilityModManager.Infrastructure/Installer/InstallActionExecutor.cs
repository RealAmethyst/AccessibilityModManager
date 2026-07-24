using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Installer;

/// <summary>
/// Executes manifest install actions (copyFile, copyFolder, replaceFile).
/// All paths are validated to stay within the game directory.
/// Every file change is appended to the caller's <c>journal</c> BEFORE the write happens, so a
/// failure mid-action (a locked file halfway through a copyFolder) still leaves the already-written
/// files journaled and the engine's rollback can undo them. Returning a list only at the end lost
/// that journal whenever an action threw partway through.
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

    public void Execute(
        InstallAction action,
        string packageExtractDir,
        string gameInstallPath,
        string backupFolder,
        List<FileChange> journal)
    {
        switch (action)
        {
            case CopyFileAction copyFile:
                ExecuteCopyFile(copyFile, packageExtractDir, gameInstallPath, backupFolder, journal);
                break;
            case CopyFolderAction copyFolder:
                ExecuteCopyFolder(copyFolder, packageExtractDir, gameInstallPath, backupFolder, journal);
                break;
            case ReplaceFileAction replaceFile:
                ExecuteReplaceFile(replaceFile, packageExtractDir, gameInstallPath, backupFolder, journal);
                break;
            default:
                throw new InvalidOperationException($"Unknown install action type: {action.GetType().Name}");
        }
    }

    private void ExecuteCopyFile(
        CopyFileAction action, string packageDir, string gameDir, string backupFolder, List<FileChange> journal)
    {
        var sourcePath = ResolveSafe(packageDir, action.Source, "package source");
        var targetPath = ResolveSafe(gameDir, action.Target, "install target");

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Package source file not found: {action.Source}");

        CopyOneFile(sourcePath, targetPath, action.Target, gameDir, backupFolder, journal);
        _logger.Information("CopyFile: {Source} -> {Target}", action.Source, action.Target);
    }

    private void ExecuteCopyFolder(
        CopyFolderAction action, string packageDir, string gameDir, string backupFolder, List<FileChange> journal)
    {
        var sourceDir = ResolveSafe(packageDir, action.SourceDir, "package source directory");
        ResolveSafe(gameDir, action.TargetDir, "install target directory");

        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Package source directory not found: {action.SourceDir}");

        var count = 0;
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativeToSource = Path.GetRelativePath(sourceDir, sourceFile);
            var combined = Path.Combine(action.TargetDir, relativeToSource);
            // ResolveSafe validates the combined relative path against gameDir consistently with CopyFile/ReplaceFile.
            var targetFile = ResolveSafe(gameDir, combined, $"copyFolder entry '{relativeToSource}'");
            var relativeToGame = Path.GetRelativePath(gameDir, targetFile);

            CopyOneFile(sourceFile, targetFile, relativeToGame, gameDir, backupFolder, journal);
            count++;
        }

        _logger.Information("CopyFolder: {Source} -> {Target} ({Count} files)",
            action.SourceDir, action.TargetDir, count);
    }

    private void ExecuteReplaceFile(
        ReplaceFileAction action, string packageDir, string gameDir, string backupFolder, List<FileChange> journal)
    {
        var sourcePath = ResolveSafe(packageDir, action.Source, "package source");
        var targetPath = ResolveSafe(gameDir, action.Target, "install target");

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Package source file not found: {action.Source}");

        // Always back up an existing file, even if the manifest set backup=false. A replaced
        // file with no backup can't be restored on uninstall/rollback — it would silently
        // destroy the user's original, which is never an acceptable outcome.
        CopyOneFile(sourcePath, targetPath, action.Target, gameDir, backupFolder, journal);
        _logger.Information("ReplaceFile: {Source} -> {Target}", action.Source, action.Target);
    }

    /// <summary>
    /// Backs up the target if it already exists, journals the change, then writes. The journal
    /// entry is appended BEFORE the copy so a failed write is still rolled back.
    /// </summary>
    private void CopyOneFile(
        string sourcePath, string targetPath, string targetRelative,
        string gameDir, string backupFolder, List<FileChange> journal)
    {
        if (File.Exists(targetPath))
        {
            var backupRel = _backupManager.BackupFile(gameDir, targetRelative, backupFolder);
            journal.Add(new FileChange
            {
                Type = ChangeType.Replaced,
                RelativePath = targetRelative,
                BackupRelativePath = backupRel
            });
        }
        else
        {
            journal.Add(new FileChange
            {
                Type = ChangeType.Added,
                RelativePath = targetRelative
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(sourcePath, targetPath, overwrite: true);
    }

    /// <summary>
    /// Resolves a relative path within a base directory, and validates it doesn't escape.
    /// </summary>
    private static string ResolveSafe(string baseDir, string relativePath, string context)
    {
        var fullPath = Path.GetFullPath(Path.Combine(baseDir, relativePath));
        return PathSafety.EnsureContained(baseDir, fullPath, context);
    }
}
