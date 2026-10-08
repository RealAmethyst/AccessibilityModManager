using System.Security.Cryptography;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.PortableTests;

public sealed class XivLauncherSpeechRepairTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-speech-repair-" + Guid.NewGuid().ToString("N"));
    private readonly XivLauncherConfig plugin = new()
    {
        PluginAssembly = "XivAccess.dll", InternalName = "XivAccess",
        WorkingPluginId = "507b48de-3362-4471-86f4-baa7e56d9387", BridgeDirectory = "wine"
    };

    public XivLauncherSpeechRepairTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepairsKnownCorePreservesBackupAndIsIdempotent(bool adapter)
    {
        var name = adapter ? "prism-core.dll" : "prism.dll";
        Stage(name);
        if (adapter)
        {
            using var source = typeof(XivLauncherSpeech).Assembly.GetManifestResourceStream(
                "AccessibilityModManager.Infrastructure.Assets.Prism0173Compat.prism_compat.dll")!;
            using var target = File.Create(Path.Combine(root, "prism.dll"));
            source.CopyTo(target);
        }
        Repair(name);
        Assert.Equal(XivLauncherSpeechRepair.Sha256, Hash(Path.Combine(root, name)));
        Assert.Equal(XivLauncherSpeech.Prism0183Sha256, Hash(Assert.Single(Directory.GetFiles(Path.Combine(root, "backups"), "*.bak"))));
        Repair(name);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "backups"), "*.bak"));
    }

    [Fact]
    public void DoesNotReplaceUnownedCore()
    {
        Stage("prism.dll");
        Assert.Throws<InvalidOperationException>(() => Repair("unrelated.dll"));
        Assert.Equal(XivLauncherSpeech.Prism0183Sha256, Hash(Path.Combine(root, "prism.dll")));
    }

    [Fact]
    public void RejectsUnknownBuildWithoutBackupOrWrite()
    {
        File.WriteAllText(Path.Combine(root, "prism.dll"), "unknown future Prism");
        Assert.Throws<InvalidDataException>(() => Repair("prism.dll"));
        Assert.Equal("unknown future Prism", File.ReadAllText(Path.Combine(root, "prism.dll")));
        Assert.False(Directory.Exists(Path.Combine(root, "backups")));
    }

    [Fact]
    public void RunningLauncherStopsRepair()
    {
        Stage("prism.dll");
        Assert.Throws<InvalidOperationException>(() => Repair("prism.dll", () => throw new InvalidOperationException("running")));
        Assert.Equal(XivLauncherSpeech.Prism0183Sha256, Hash(Path.Combine(root, "prism.dll")));
    }

    [Fact]
    public void RefusesLinkedCore()
    {
        if (!OperatingSystem.IsLinux()) return;
        Stage("outside.dll");
        File.CreateSymbolicLink(Path.Combine(root, "prism.dll"), Path.Combine(root, "outside.dll"));
        Assert.Throws<InvalidOperationException>(() => Repair("prism.dll"));
        Assert.Equal(XivLauncherSpeech.Prism0183Sha256, Hash(Path.Combine(root, "outside.dll")));
    }

    private void Stage(string name) => File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "prism-0.18.3.dll"), Path.Combine(root, name));
    private void Repair(string owned, Action? check = null) => XivLauncherSpeechRepair.Ensure(root, plugin,
        new InstallReceipt
        {
            GameId = "xiv", PluginId = "test", InstalledVersion = "1", InstalledAt = DateTime.UtcNow,
            Changes = [new FileChange { RelativePath = owned, Type = ChangeType.Added }],
            BackupFolder = "", ManifestHash = "test"
        }, Path.Combine(root, "backups"), check ?? (() => { }));
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    public void Dispose() => Directory.Delete(root, true);
}
