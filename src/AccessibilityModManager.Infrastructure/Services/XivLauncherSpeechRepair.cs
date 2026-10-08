using System.Security.Cryptography;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Repairs known managed Prism cores without exposing Wine's version to FFXIV.</summary>
public static class XivLauncherSpeechRepair
{
    public const string Sha256 = "11cddc955741f4009580e30f4828ef17d17853e3874232d7249d495a01b49d1e";

    public static void Ensure(string payloadRoot, XivLauncherConfig plugin, InstallReceipt receipt,
        string backupRoot, Action? ensureClosed = null)
    {
        if (plugin.BridgeDirectory is null) return;
        var directory = Path.GetDirectoryName(PathSafety.CombineContained(payloadRoot, plugin.PluginAssembly))!;
        var prism = Path.Combine(directory, "prism.dll");
        PathSafety.EnsureNoReparseTraversal(Path.GetPathRoot(payloadRoot)!, prism, "Prism library");
        var hash = Hash(prism);
        var core = prism;
        if (hash == Prism0173Compatibility.Sha256)
        {
            core = Path.Combine(directory, "prism-core.dll");
            PathSafety.EnsureNoReparseTraversal(payloadRoot, core, "Prism core");
            hash = Hash(core);
        }
        if (hash == Sha256) return;
        if (hash != XivLauncherSpeech.Prism0183Sha256)
            throw new InvalidDataException("This Prism build has no verified XIVLauncher speech repair. Its files were left untouched.");
        var relative = Path.GetRelativePath(payloadRoot, core).Replace('\\', '/');
        if (!receipt.Changes.Any(change => change.RelativePath.Replace('\\', '/') == relative))
            throw new InvalidOperationException("The Prism core is not owned by this mod's installation receipt. It was left untouched.");
        var checkClosed = ensureClosed ?? XivLauncherFrontendInstaller.EnsureLauncherClosed;
        checkClosed();
        PathSafety.EnsureNoReparseTraversal(Path.GetPathRoot(backupRoot)!, backupRoot, "Speech repair backups");
        Directory.CreateDirectory(backupRoot);
        PathSafety.EnsureNoReparseTraversal(backupRoot, Path.Combine(backupRoot, "speech.lock"), "Speech repair lock");
        using var repairLock = new FileStream(Path.Combine(backupRoot, "speech.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var temp = core + ".amm-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteCore(temp);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, File.GetUnixFileMode(core));
            checkClosed();
            if (Hash(core) != hash)
                throw new InvalidOperationException("The Prism library changed during repair. Retry after closing XIVLauncher.");
            File.Copy(core, Path.Combine(backupRoot, Path.GetFileName(core) + "." + Guid.NewGuid().ToString("N") + ".bak"));
            File.Move(temp, core, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static void WriteCore(string path)
    {
        using var resource = typeof(XivLauncherSpeechRepair).Assembly.GetManifestResourceStream(
            "AccessibilityModManager.Infrastructure.Assets.PrismWine.prism.dll")
            ?? throw new InvalidDataException("The verified Linux Prism core is missing.");
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) resource.CopyTo(output);
        if (Hash(path) != Sha256) throw new InvalidDataException("The bundled Linux Prism core failed verification.");
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
