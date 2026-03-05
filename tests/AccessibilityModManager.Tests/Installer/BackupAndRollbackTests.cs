using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Installer;

public class BackupAndRollbackTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _gameDir;
    private readonly BackupManager _backupManager;

    public BackupAndRollbackTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ammtest_" + Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_tempDir, "game");
        Directory.CreateDirectory(_gameDir);
        _backupManager = new BackupManager(TestLogger.Create());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void CreateBackupFolder_CreatesNamespacedFolder()
    {
        var folder = _backupManager.CreateBackupFolder(_gameDir, "my-plugin", "my-game");

        Assert.True(Directory.Exists(folder));
        Assert.Contains("modmanager_backups", folder);
        Assert.Contains("my-plugin", folder);
        Assert.Contains("my-game", folder);
    }

    [Fact]
    public void BackupFile_CopiesFileToBackupFolder()
    {
        File.WriteAllText(Path.Combine(_gameDir, "config.ini"), "original");
        var backupFolder = _backupManager.CreateBackupFolder(_gameDir, "p", "g");

        var backupRel = _backupManager.BackupFile(_gameDir, "config.ini", backupFolder);

        Assert.NotEmpty(backupRel);
        Assert.True(File.Exists(Path.Combine(backupFolder, backupRel)));
        Assert.Equal("original", File.ReadAllText(Path.Combine(backupFolder, backupRel)));
    }

    [Fact]
    public void RestoreFile_RestoresFromBackup()
    {
        // Setup: create original, back it up, then overwrite
        File.WriteAllText(Path.Combine(_gameDir, "data.txt"), "original");
        var backupFolder = _backupManager.CreateBackupFolder(_gameDir, "p", "g");
        var backupRel = _backupManager.BackupFile(_gameDir, "data.txt", backupFolder);
        File.WriteAllText(Path.Combine(_gameDir, "data.txt"), "modified");

        _backupManager.RestoreFile(_gameDir, "data.txt", backupFolder, backupRel);

        Assert.Equal("original", File.ReadAllText(Path.Combine(_gameDir, "data.txt")));
    }

    [Fact]
    public void RemoveAddedFile_DeletesFileAndCleansEmptyDirs()
    {
        var subDir = Path.Combine(_gameDir, "mods", "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "new.dll"), "data");

        _backupManager.RemoveAddedFile(_gameDir, Path.Combine("mods", "sub", "new.dll"));

        Assert.False(File.Exists(Path.Combine(subDir, "new.dll")));
        // Empty dirs should be cleaned up
        Assert.False(Directory.Exists(subDir));
    }

    [Fact]
    public void BackupFile_PathTraversal_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _backupManager.BackupFile(_gameDir, "../../evil.txt", _tempDir));
    }
}
