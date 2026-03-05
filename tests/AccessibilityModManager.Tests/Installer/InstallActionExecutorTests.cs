using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Installer;

public class InstallActionExecutorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _gameDir;
    private readonly string _packageDir;
    private readonly string _backupDir;
    private readonly InstallActionExecutor _executor;

    public InstallActionExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ammtest_" + Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_tempDir, "game");
        _packageDir = Path.Combine(_tempDir, "package");
        _backupDir = Path.Combine(_tempDir, "backup");

        Directory.CreateDirectory(_gameDir);
        Directory.CreateDirectory(_packageDir);
        Directory.CreateDirectory(_backupDir);

        var logger = TestLogger.Create();
        var backupManager = new BackupManager(logger);
        _executor = new InstallActionExecutor(backupManager, logger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void CopyFile_NewFile_RecordsAsAdded()
    {
        File.WriteAllText(Path.Combine(_packageDir, "mod.dll"), "mod content");

        var action = new CopyFileAction { Source = "mod.dll", Target = "mods/mod.dll" };
        var changes = _executor.Execute(action, _packageDir, _gameDir, _backupDir);

        Assert.Single(changes);
        Assert.Equal(ChangeType.Added, changes[0].Type);
        Assert.True(File.Exists(Path.Combine(_gameDir, "mods", "mod.dll")));
    }

    [Fact]
    public void CopyFile_ExistingFile_BacksUpAndRecordsAsReplaced()
    {
        // Existing file in game dir
        Directory.CreateDirectory(Path.Combine(_gameDir, "mods"));
        File.WriteAllText(Path.Combine(_gameDir, "mods", "mod.dll"), "original");

        // New file in package
        File.WriteAllText(Path.Combine(_packageDir, "mod.dll"), "updated");

        var action = new CopyFileAction { Source = "mod.dll", Target = "mods/mod.dll" };
        var changes = _executor.Execute(action, _packageDir, _gameDir, _backupDir);

        Assert.Single(changes);
        Assert.Equal(ChangeType.Replaced, changes[0].Type);
        Assert.NotNull(changes[0].BackupRelativePath);
        Assert.Equal("updated", File.ReadAllText(Path.Combine(_gameDir, "mods", "mod.dll")));
        Assert.True(File.Exists(Path.Combine(_backupDir, changes[0].BackupRelativePath!)));
    }

    [Fact]
    public void ReplaceFile_WithBackup_BacksUpOriginal()
    {
        Directory.CreateDirectory(Path.Combine(_gameDir, "config"));
        File.WriteAllText(Path.Combine(_gameDir, "config", "settings.cfg"), "old");
        File.WriteAllText(Path.Combine(_packageDir, "settings.cfg"), "new");

        var action = new ReplaceFileAction { Source = "settings.cfg", Target = "config/settings.cfg", Backup = true };
        var changes = _executor.Execute(action, _packageDir, _gameDir, _backupDir);

        Assert.Single(changes);
        Assert.Equal(ChangeType.Replaced, changes[0].Type);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_gameDir, "config", "settings.cfg")));
    }

    [Fact]
    public void CopyFolder_CopiesAllFiles()
    {
        Directory.CreateDirectory(Path.Combine(_packageDir, "data", "sub"));
        File.WriteAllText(Path.Combine(_packageDir, "data", "a.txt"), "aaa");
        File.WriteAllText(Path.Combine(_packageDir, "data", "sub", "b.txt"), "bbb");

        var action = new CopyFolderAction { SourceDir = "data", TargetDir = "gamedata" };
        var changes = _executor.Execute(action, _packageDir, _gameDir, _backupDir);

        Assert.Equal(2, changes.Count);
        Assert.True(File.Exists(Path.Combine(_gameDir, "gamedata", "a.txt")));
        Assert.True(File.Exists(Path.Combine(_gameDir, "gamedata", "sub", "b.txt")));
    }

    [Fact]
    public void Execute_PathTraversal_Throws()
    {
        File.WriteAllText(Path.Combine(_packageDir, "mod.dll"), "content");

        var action = new CopyFileAction { Source = "mod.dll", Target = "../../escape.dll" };
        Assert.Throws<InvalidOperationException>(() =>
            _executor.Execute(action, _packageDir, _gameDir, _backupDir));
    }

    [Fact]
    public void Execute_MissingSourceFile_Throws()
    {
        var action = new CopyFileAction { Source = "nonexistent.dll", Target = "mod.dll" };
        Assert.Throws<FileNotFoundException>(() =>
            _executor.Execute(action, _packageDir, _gameDir, _backupDir));
    }
}
