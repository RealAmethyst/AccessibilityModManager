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
        // Timestamp for human readability + a GUID chunk for uniqueness: two backup folders in
        // the same second must never collide and overwrite each other's originals.
        var folderName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..24];
        // pluginId/gameId are untrusted — keep the backup folder inside the game install.
        var backupFolder = PathSafety.CombineContained(
            gameInstallPath, "modmanager_backups", pluginId, gameId, folderName);
        // Guard BEFORE creating: the chain above the fresh leaf could pre-exist, and a junction
        // planted at, say, modmanager_backups would otherwise get directories created through it
        // (and would redirect every backup — the only copy of the user's originals).
        PathSafety.EnsureNoReparseTraversal(gameInstallPath, backupFolder, "backup folder");
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
        PathSafety.EnsureNoReparseTraversal(gameInstallPath, sourcePath, "source file");

        if (!File.Exists(sourcePath))
        {
            _logger.Debug("No existing file to back up: {Path}", relativeFilePath);
            return string.Empty;
        }

        var backupRelativePath = relativeFilePath;
        var backupFullPath = Path.GetFullPath(Path.Combine(backupFolder, backupRelativePath));
        ValidatePathWithinDirectory(backupFullPath, backupFolder, "backup destination");
        PathSafety.EnsureNoReparseTraversal(backupFolder, backupFullPath, "backup destination");
        // Anchor the walk at the GAME root too when the backup folder lives inside it (the mod
        // flow always does this): the segment between the game root and the backup folder must
        // also be link-free, or the folder-level exemption above would trust a planted junction.
        if (PathSafety.IsContained(gameInstallPath, backupFolder))
            PathSafety.EnsureNoReparseTraversal(gameInstallPath, backupFullPath, "backup destination");

        // First backup wins: if two actions in one install touch the same file, the second's
        // "current content" is the first action's output, not the user's original. Overwriting
        // here would make uninstall restore a half-modded file instead of the true original.
        if (File.Exists(backupFullPath))
        {
            _logger.Debug("Backup already captured for {Path}; keeping the original", relativeFilePath);
            return backupRelativePath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(backupFullPath)!);
        File.Copy(sourcePath, backupFullPath, overwrite: false);

        _logger.Debug("Backed up: {Source} -> {Backup}", relativeFilePath, backupFullPath);
        return backupRelativePath;
    }

    /// <summary>
    /// Restores a single file from the backup folder back to the game directory. Returns false
    /// when the backup file is missing — the caller must treat that as a failed restore, not
    /// silently carry on (a receipt that then gets deleted would erase the only evidence).
    /// </summary>
    public bool RestoreFile(string gameInstallPath, string relativeFilePath, string backupFolder, string backupRelativePath)
    {
        // The backup-relative path comes from the receipt. Receipts are tamper-checked, but the
        // read stays confined to the backup folder anyway — defense in depth, same as the target.
        var backupFullPath = Path.GetFullPath(Path.Combine(backupFolder, backupRelativePath));
        ValidatePathWithinDirectory(backupFullPath, backupFolder, "restore source");
        PathSafety.EnsureNoReparseTraversal(backupFolder, backupFullPath, "restore source");
        if (PathSafety.IsContained(gameInstallPath, backupFolder))
            PathSafety.EnsureNoReparseTraversal(gameInstallPath, backupFullPath, "restore source");
        var targetPath = Path.GetFullPath(Path.Combine(gameInstallPath, relativeFilePath));
        ValidatePathWithinDirectory(targetPath, gameInstallPath, "restore target");
        PathSafety.EnsureNoReparseTraversal(gameInstallPath, targetPath, "restore target");

        if (!File.Exists(backupFullPath))
        {
            _logger.Warning("Backup file not found during restore: {Path}", backupFullPath);
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(backupFullPath, targetPath, overwrite: true);

        _logger.Debug("Restored: {Backup} -> {Target}", backupRelativePath, relativeFilePath);
        return true;
    }

    /// <summary>
    /// Removes a file that was added (not replaced) during install.
    /// </summary>
    public void RemoveAddedFile(string gameInstallPath, string relativeFilePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(gameInstallPath, relativeFilePath));
        ValidatePathWithinDirectory(fullPath, gameInstallPath, "file to remove");
        PathSafety.EnsureNoReparseTraversal(gameInstallPath, fullPath, "file to remove");

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
