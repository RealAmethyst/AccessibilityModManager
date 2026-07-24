using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Tests.Helpers;
using Microsoft.Win32;

namespace AccessibilityModManager.Tests.Detection;

public class RegistryGameDetectorTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _subKeyPath;
    private readonly RegistryGameDetector _detector;

    public RegistryGameDetectorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_reg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        // Isolated, per-test HKCU subkey so we never collide with or clobber real values.
        _subKeyPath = @"Software\AmmTest_" + Guid.NewGuid().ToString("N");
        _detector = new RegistryGameDetector(new GameVerifier(TestLogger.Create()), TestLogger.Create());
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_subKeyPath, throwOnMissingSubKey: false); } catch { }
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }

    private void WriteValue(string value, string data)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKeyPath);
        key!.SetValue(value, data);
    }

    private GameDefinition Game(bool probeSubfolders = true) => new()
    {
        GameId = "ptcgl",
        DisplayName = "Pokémon TCG Live",
        ExeName = "Pokemon TCG Live.exe",
        RegistryProbe = new RegistryProbe
        {
            Hive = "HKCU",
            Key = _subKeyPath,
            Value = "Path",
            ProbeSubfolders = probeSubfolders
        }
    };

    [Fact]
    public void ResolveInstallPath_ValueIsGameFolder_ReturnsIt()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "Pokemon TCG Live.exe"), "exe");
        WriteValue("Path", _tempRoot);

        Assert.Equal(_tempRoot, _detector.ResolveInstallPath(Game()));
    }

    [Fact]
    public void ResolveInstallPath_ValueIsParentFolder_ProbesSubfolders()
    {
        // Registry value points at the publisher folder; the game is in a subfolder (PTCGL case).
        var child = Path.Combine(_tempRoot, "Pokémon Trading Card Game Live");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "Pokemon TCG Live.exe"), "exe");
        WriteValue("Path", _tempRoot);

        Assert.Equal(child, _detector.ResolveInstallPath(Game()));
    }

    [Fact]
    public void ResolveInstallPath_ParentFolder_ProbeDisabled_ReturnsNull()
    {
        var child = Path.Combine(_tempRoot, "Pokémon Trading Card Game Live");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "Pokemon TCG Live.exe"), "exe");
        WriteValue("Path", _tempRoot);

        Assert.Null(_detector.ResolveInstallPath(Game(probeSubfolders: false)));
    }

    [Fact]
    public void ResolveInstallPath_QuotedAndTrailingSlash_Normalized()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "Pokemon TCG Live.exe"), "exe");
        WriteValue("Path", "\"" + _tempRoot + "\\\"");   // surrounding quotes + trailing backslash

        Assert.Equal(_tempRoot, _detector.ResolveInstallPath(Game()));
    }

    [Fact]
    public void ResolveInstallPath_ValueMissing_ReturnsNull()
    {
        WriteValue("SomethingElse", _tempRoot);   // key exists, but not the "Path" value
        Assert.Null(_detector.ResolveInstallPath(Game()));
    }

    [Fact]
    public void ResolveInstallPath_NoRegistryProbe_ReturnsNull()
    {
        var game = new GameDefinition { GameId = "x", DisplayName = "X" };
        Assert.Null(_detector.ResolveInstallPath(game));
    }
}
