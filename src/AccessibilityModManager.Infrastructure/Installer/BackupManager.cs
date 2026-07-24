using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Installer;

/// <summary>
/// Creates and restores backups of game files before modification.
/// Backup folder structure: {gameInstallPath}/modmanager_backups/{pluginId}/{gameId}/{timestamp}/
/// </summary>
public sealed class BackupManager
{
    private readonly ILogger _logger;

    public BackupManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a new backup folder and returns its path.
    /// </summary>
    public string CreateBackupFolder(string gameInstallPath, string pluginId, string gameId)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        // pluginId/gameId are untrusted — keep the backup folder inside the game install.
        var backupFolder = PathSafety.CombineContained(
            gameInstallPath, "modmanager_backups", pluginId, gameId, timestamp);
        Directory.CreateDirectory(backupFolder);
        _logger.Information("Created backup folder: {BackupFolder}", backupFolder);
        return backupFolder;
    }

    /// <summary>
    /// Backs up a single file from the game directory into the backup folder.
    /// Returns the relative path within the backup folder.
    /// </summary>
    public string BackupFile(string gameInstallPath, string relativeFilePath, string backupFolder)
    {
        var sourcePath = Path.GetFullPath(Path.Combine(gameInstallPath, relativeFilePath));
        ValidatePathWithinDirectory(sourcePath, gameInstallPath, "source file");

        if (!File.Exists(sourcePath))
        {
            _logger.Debug("No existing file to back up: {Path}", relativeFilePath);
            return string.Empty;
        }

        var backupRelativePath = relativeFilePath;
        var backupFullPath = Path.Combine(backupFolder, backupRelativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(backupFullPath)!);
        File.Copy(sourcePath, backupFullPath, overwrite: true);

        _logger.Debug("Backed up: {Source} -> {Backup}", relativeFilePath, backupFullPath);
        return backupRelativePath;
    }

    /// <summary>
    /// Restores a single file from the backup folder back to the game directory.
    /// </summary>
    public void RestoreFile(string gameInstallPath, string relativeFilePath, string backupFolder, string backupRelativePath)
    {
        var backupFullPath = Path.Combine(backupFolder, backupRelativePath);
        var targetPath = Path.GetFullPath(Path.Combine(gameInstallPath, relativeFilePath));
        ValidatePathWithinDirectory(targetPath, gameInstallPath, "restore target");

        if (!File.Exists(backupFullPath))
        {
            _logger.Warning("Backup file not found during restore: {Path}", backupFullPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(backupFullPath, targetPath, overwrite: true);

        _logger.Debug("Restored: {Backup} -> {Target}", backupRelativePath, relativeFilePath);
    }

    /// <summary>
    /// Removes a file that was added (not replaced) during install.
    /// </summary>
    public void RemoveAddedFile(string gameInstallPath, string relativeFilePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(gameInstallPath, relativeFilePath));
        ValidatePathWithinDirectory(fullPath, gameInstallPath, "file to remove");

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.Debug("Removed added file: {Path}", relativeFilePath);
        }

        // Clean up empty directories up to the game install root
        CleanEmptyDirectories(Path.GetDirectoryName(fullPath)!, gameInstallPath);
    }

    private void CleanEmptyDirectories(string directory, string stopAt)
    {
        var stopAtFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        while (!string.Equals(current, stopAtFull, StringComparison.OrdinalIgnoreCase) &&
               PathSafety.IsContained(stopAtFull, current))
        {
            if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                break;

            Directory.Delete(current);
            _logger.Debug("Removed empty directory: {Dir}", current);

            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;
            current = parent;
        }
    }

    private static void ValidatePathWithinDirectory(string fullPath, string directory, string context)
    {
        PathSafety.EnsureContained(directory, fullPath, context);
    }
}
