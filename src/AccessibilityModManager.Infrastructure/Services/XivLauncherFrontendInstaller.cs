using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Installs the packaged accessible frontend while retaining XLM and native launcher data.</summary>
public static class XivLauncherFrontendInstaller
{
    private const string HookName = "amm-accessible-launcher.sh";
    private const string HookMarker = "# Accessibility Mod Manager: accessible XIVLauncher";
    private const string Hook = HookMarker + "\n" +
        "# The manager supplies this frontend; Dalamud, Wine and game updates remain native.\n" +
        "export XLM_SKIP_UPDATE=true\n" +
        "export XLM_UPDATER_DISABLE=true\n";

    public sealed record Bundle(string Version, Dictionary<string, string> Files);

    public static string EnsureInstalled(string steamRoot, string? bundleDirectory = null, Action? ensureClosed = null)
    {
        var checkClosed = ensureClosed ?? EnsureLauncherClosed;
        checkClosed();
        XlmInstaller.Verify(steamRoot);
        var source = bundleDirectory ?? Path.Combine(AppContext.BaseDirectory, "xivlauncher");
        var manifestPath = Path.Combine(source, "amm-frontend.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("This manager build is missing its accessible XIVLauncher. Install the complete Linux manager package.");
        var manifestText = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<Bundle>(manifestText)
            ?? throw new InvalidDataException("The accessible launcher bundle metadata is missing.");
        PathSafety.EnsureSafeId(manifest.Version, "Accessible launcher version");
        if (!manifest.Files.ContainsKey("XIVLauncher.Core") || !manifest.Files.ContainsKey("XIVLauncher.Core.dll") ||
            !manifest.Files.ContainsKey("Avalonia.FreeDesktop.AtSpi.dll") || !manifest.Files.ContainsKey("versiondata"))
            throw new InvalidDataException("The accessible launcher bundle is incomplete.");
        VerifyFiles(source, manifest);
        var tool = XlmInstaller.ToolDirectory(steamRoot);
        var lockPath = Path.Combine(tool, ".amm-frontend-install.lock");
        PathSafety.EnsureNoReparseTraversal(tool, lockPath, "Accessible launcher installation lock");
        using var installLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var target = Path.Combine(tool, "xlcore");
        var hookPath = Path.Combine(tool, "prelaunch.d", HookName);
        PathSafety.EnsureNoReparseTraversal(tool, target, "Accessible launcher directory");
        PathSafety.EnsureNoReparseTraversal(tool, hookPath, "Accessible launcher hook");
        var oldHook = XivLauncherFiles.Read(hookPath);
        if (oldHook is not null && !oldHook.StartsWith(HookMarker + "\n", StringComparison.Ordinal))
            throw new InvalidOperationException("Another launcher hook uses the manager's filename. It was left untouched.");
        var backupRoot = Path.Combine(tool, "amm-backups");
        PathSafety.EnsureNoReparseTraversal(tool, backupRoot, "Accessible launcher backups");
        Directory.CreateDirectory(backupRoot);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(backupRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var installedManifest = Path.Combine(target, "amm-frontend.json");
        if (File.Exists(installedManifest) && File.ReadAllText(installedManifest) == manifestText)
        {
            VerifyFiles(target, manifest);
            XivLauncherFiles.Write(hookPath, oldHook, Hook, backupRoot, checkClosed);
            return Path.Combine(target, "XIVLauncher.Core");
        }
        var stage = Path.Combine(tool, ".amm-frontend-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(backupRoot, "xlcore-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var movedPrevious = false;
        var activated = false;
        try
        {
            foreach (var name in manifest.Files.Keys.Append("amm-frontend.json"))
            {
                var from = PathSafety.CombineContained(source, name);
                var to = PathSafety.CombineContained(stage, name);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(to, File.GetUnixFileMode(from));
            }
            VerifyFiles(stage, manifest);
            checkClosed();
            if (Directory.Exists(target)) { Directory.Move(target, backup); movedPrevious = true; }
            Directory.Move(stage, target);
            activated = true;
            XivLauncherFiles.Write(hookPath, oldHook, Hook, backupRoot, checkClosed);
        }
        catch
        {
            if (activated) Directory.Delete(target, true);
            if (movedPrevious) Directory.Move(backup, target);
            throw;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        return Path.Combine(target, "XIVLauncher.Core");
    }

    private static void VerifyFiles(string root, Bundle manifest)
    {
        foreach (var (relative, hash) in manifest.Files)
        {
            var path = PathSafety.CombineContained(root, relative);
            PathSafety.EnsureNoReparseTraversal(root, path, "Accessible launcher bundle file");
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !File.Exists(path))
                throw new InvalidDataException("The accessible launcher bundle is incomplete: " + relative);
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The accessible launcher bundle failed verification: " + relative);
        }
    }

    public static void EnsureLauncherClosed()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                if (process.ProcessName.StartsWith("ffxiv", StringComparison.OrdinalIgnoreCase) ||
                    process.ProcessName.StartsWith("XIVLauncher", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Close FINAL FANTASY XIV and XIVLauncher before preparing the accessible launcher.");
        }
    }
}
