using AccessibilityModManager.Infrastructure.Installer;

namespace AccessibilityModManager.Tests.Installer;

public class PortableAppLayoutTests : IDisposable
{
    private readonly string _root;

    public PortableAppLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ammtest_applayout_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ResolveInstallRoot_ExeAtTopLevel_ReturnsFolder()
    {
        // The expected layout (F4): exe sits directly in the picked folder.
        File.WriteAllText(Path.Combine(_root, "emu.exe"), "x");

        Assert.Equal(_root, PortableAppLayout.ResolveInstallRoot(_root, "emu.exe"));
    }

    [Fact]
    public void ResolveInstallRoot_ExeInSingleSubfolder_ReturnsSubfolder()
    {
        // Safety net: a ZIP that wraps everything in one top-level directory.
        var sub = Path.Combine(_root, "MyEmu");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "emu.exe"), "x");

        Assert.Equal(sub, PortableAppLayout.ResolveInstallRoot(_root, "emu.exe"));
    }

    [Fact]
    public void ResolveInstallRoot_ExeMissing_ReturnsNull()
    {
        // Two sub-folders and no exe anywhere → can't confidently pick a root.
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        Directory.CreateDirectory(Path.Combine(_root, "b"));

        Assert.Null(PortableAppLayout.ResolveInstallRoot(_root, "emu.exe"));
    }

    [Fact]
    public void ResolveInstallRoot_FolderMissing_ReturnsNull()
    {
        Assert.Null(PortableAppLayout.ResolveInstallRoot(Path.Combine(_root, "does-not-exist"), "emu.exe"));
    }

    [Fact]
    public void ResolveInstallRoot_NoExeName_ReturnsFolderWhenPresent()
    {
        // A game with no ExeName set: fall back to the folder itself.
        Assert.Equal(_root, PortableAppLayout.ResolveInstallRoot(_root, null));
    }
}
