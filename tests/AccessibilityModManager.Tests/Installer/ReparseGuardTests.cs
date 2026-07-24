using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Installer;

/// <summary>
/// Audit finding 15: file-safety checks compare path TEXT, so a junction sitting INSIDE the game
/// folder could redirect writes/restores/deletes outside it. Policy: the install root itself may
/// be a junction (the ASCII path shim is exactly that); anything deeper may not.
/// </summary>
public class ReparseGuardTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AsciiPathShimService _shim = new(TestLogger.Create());

    public ReparseGuardTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_reparse_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        // Junctions are removed link-first so their targets' contents are never touched. A path
        // may already be gone by the time it's reached (it was seen through a link removed
        // earlier in the loop) — skip those.
        foreach (var dir in Directory.EnumerateDirectories(_tempRoot, "*", SearchOption.AllDirectories).ToList())
        {
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    _shim.RemoveJunctionLink(dir);
            }
            catch (IOException) { }
        }
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private string MakeDir(params string[] parts)
    {
        var p = Path.Combine([_tempRoot, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    [Fact]
    public void PlainNestedPath_Passes()
    {
        var root = MakeDir("game");
        MakeDir("game", "mods", "sub");
        PathSafety.EnsureNoReparseTraversal(root, Path.Combine(root, "mods", "sub", "x.dll"), "target");
    }

    [Fact]
    public void NotYetExistingTail_Passes()
    {
        var root = MakeDir("game");
        PathSafety.EnsureNoReparseTraversal(root, Path.Combine(root, "new", "deeper", "x.dll"), "target");
    }

    [Fact]
    public async Task JunctionInsideRoot_Refused()
    {
        var root = MakeDir("game");
        var elsewhere = MakeDir("elsewhere");
        var link = Path.Combine(root, "mods");
        await _shim.CreateJunctionAsync(link, elsewhere);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PathSafety.EnsureNoReparseTraversal(root, Path.Combine(link, "x.dll"), "target"));
        Assert.Contains("link", ex.Message);
    }

    [Fact]
    public async Task RootItselfAJunction_Passes()
    {
        // The supported PTCG setup: C:\PokemonTCGLive -> the real (non-ASCII) install folder.
        var real = MakeDir("real-game");
        Directory.CreateDirectory(Path.Combine(real, "mods"));
        var junctionRoot = Path.Combine(_tempRoot, "ascii-alias");
        await _shim.CreateJunctionAsync(junctionRoot, real);

        PathSafety.EnsureNoReparseTraversal(junctionRoot, Path.Combine(junctionRoot, "mods", "x.dll"), "target");
    }

    [Fact]
    public async Task InstallAction_ThroughNestedJunction_FailsClosed()
    {
        var gameDir = MakeDir("game");
        var outside = MakeDir("outside");
        var link = Path.Combine(gameDir, "Mods");
        await _shim.CreateJunctionAsync(link, outside);

        var packageDir = MakeDir("package");
        File.WriteAllText(Path.Combine(packageDir, "payload.dll"), "x");

        var executor = new InstallActionExecutor(new BackupManager(TestLogger.Create()), TestLogger.Create());
        var action = new CopyFileAction { Source = "payload.dll", Target = "Mods\\payload.dll" };

        Assert.Throws<InvalidOperationException>(() =>
            executor.Execute(action, packageDir, gameDir, MakeDir("backup"), new List<FileChange>()));

        // The write really was stopped: nothing landed on the other side of the link.
        Assert.False(File.Exists(Path.Combine(outside, "payload.dll")));
    }

    [Fact]
    public async Task RestoreTarget_ThroughNestedJunction_FailsClosed()
    {
        var gameDir = MakeDir("game");
        var outside = MakeDir("outside");
        var link = Path.Combine(gameDir, "data");
        await _shim.CreateJunctionAsync(link, outside);

        var backupFolder = MakeDir("game", "modmanager_backups", "p", "g", "t");
        Directory.CreateDirectory(Path.Combine(backupFolder, "data"));
        File.WriteAllText(Path.Combine(backupFolder, "data", "orig.bin"), "original");

        var backup = new BackupManager(TestLogger.Create());
        Assert.Throws<InvalidOperationException>(() =>
            backup.RestoreFile(gameDir, "data\\orig.bin", backupFolder, "data\\orig.bin"));
        Assert.False(File.Exists(Path.Combine(outside, "orig.bin")));
    }
}
