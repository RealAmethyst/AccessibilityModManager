using System.Security.Cryptography;
using System.Text.Json;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.PortableTests;

public sealed class XivLauncherFrontendTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-xl-frontend-test-" + Guid.NewGuid().ToString("N"));
    private string Steam => Path.Combine(root, "Steam");
    private string Source => Path.Combine(root, "bundle");
    private string Tool => XlmInstaller.ToolDirectory(Steam);
    private string Target => Path.Combine(Tool, "xlcore");
    private string Hook => Path.Combine(Tool, "prelaunch.d", "amm-accessible-launcher.sh");

    public XivLauncherFrontendTests()
    {
        Directory.CreateDirectory(Tool);
        foreach (var name in new[] { "xlm", "xlm.sh", "toolmanifest.vdf" }) File.WriteAllText(Path.Combine(Tool, name), "original");
        File.WriteAllText(Path.Combine(Tool, "compatibilitytool.vdf"), "\"compatibilitytools\" { \"compat_tools\" { \"xlm\" { } } }");
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "XIVLauncher.Core"), "original launcher");
        Directory.CreateDirectory(Source);
        var hashes = new Dictionary<string, string>();
        foreach (var name in new[] { "XIVLauncher.Core", "XIVLauncher.Core.dll", "Avalonia.FreeDesktop.AtSpi.dll", "versiondata" })
        {
            var path = Path.Combine(Source, name);
            File.WriteAllText(path, "accessible " + name);
            hashes[name] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }
        File.WriteAllText(Path.Combine(Source, "amm-frontend.json"), JsonSerializer.Serialize(
            new XivLauncherFrontendInstaller.Bundle("1.4.0-amm1", hashes)));
    }

    [Fact]
    public void InstallBacksUpOriginalAndReusesVerifiedFrontend()
    {
        XivLauncherFrontendInstaller.EnsureInstalled(Steam, Source, () => { });
        Assert.Equal("accessible XIVLauncher.Core", File.ReadAllText(Path.Combine(Target, "XIVLauncher.Core")));
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(Tool, "amm-backups")));
        Assert.Equal("original launcher", File.ReadAllText(Path.Combine(backup, "XIVLauncher.Core")));
        Assert.Contains("export XLM_SKIP_UPDATE=true", File.ReadAllText(Hook));
        Assert.DoesNotContain("SDL_VIDEO_DRIVER", File.ReadAllText(Hook));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Tool, "xlm.sh")));
        XivLauncherFrontendInstaller.EnsureInstalled(Steam, Source, () => { });
        Assert.Single(Directory.GetDirectories(Path.Combine(Tool, "amm-backups")));
    }

    [Fact]
    public void CorruptBundleDoesNotChangeInstalledLauncherOrHooks()
    {
        File.AppendAllText(Path.Combine(Source, "XIVLauncher.Core"), "tampered");
        Assert.Throws<InvalidDataException>(() => XivLauncherFrontendInstaller.EnsureInstalled(Steam, Source, () => { }));
        Assert.Equal("original launcher", File.ReadAllText(Path.Combine(Target, "XIVLauncher.Core")));
        Assert.False(File.Exists(Hook));
    }

    [Fact]
    public void HookConflictLeavesOriginalLauncherAndOtherHooksAlone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Hook)!);
        File.WriteAllText(Hook, "user-owned hook");
        Assert.Throws<InvalidOperationException>(() => XivLauncherFrontendInstaller.EnsureInstalled(Steam, Source, () => { }));
        Assert.Equal("user-owned hook", File.ReadAllText(Hook));
        Assert.Equal("original launcher", File.ReadAllText(Path.Combine(Target, "XIVLauncher.Core")));
    }

    [Fact]
    public void ConcurrentLauncherStartupRollsBackActivation()
    {
        var checks = 0;
        Assert.Throws<InvalidOperationException>(() => XivLauncherFrontendInstaller.EnsureInstalled(Steam, Source, () =>
        {
            if (++checks == 3) throw new InvalidOperationException("Launcher started.");
        }));
        Assert.Equal("original launcher", File.ReadAllText(Path.Combine(Target, "XIVLauncher.Core")));
        Assert.False(File.Exists(Hook));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
