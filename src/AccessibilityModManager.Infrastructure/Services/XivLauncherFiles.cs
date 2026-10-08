using System.Diagnostics;
using System.Text;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Atomic compare-and-replace writes for the launcher's externally owned settings.</summary>
public static class XivLauncherFiles
{
    public static void EnsureClosed()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("XIVLauncher setup requires Linux.");
        if (SteamShutdownService.IsSteamRunning())
            throw new InvalidOperationException("Close Steam before changing the XIVLauncher setup.");
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var name = process.ProcessName;
                if (name.StartsWith("ffxiv", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("XIVLauncher", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Close FINAL FANTASY XIV and XIVLauncher before changing the mod setup.");
            }
        }
    }

    public static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;

    public static void Write(string path, string? expected, string? replacement, string backupDirectory,
        Action? ensureClosed = null)
    {
        (ensureClosed ?? EnsureClosed)();
        if (Read(path) != expected)
            throw new InvalidOperationException("Settings changed while setup was being prepared. Retry after closing Steam and XIVLauncher.");
        PathSafety.EnsureNoReparseTraversal(Path.GetDirectoryName(path)!, path, "launcher settings");
        if (expected == replacement) return;
        Directory.CreateDirectory(backupDirectory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(backupDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (File.Exists(path)) File.Copy(path, Path.Combine(backupDirectory,
            Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".bak"));
        if (replacement is null) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".amm-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, replacement, new UTF8Encoding(false));
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp,
                File.Exists(path) ? File.GetUnixFileMode(path) : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            (ensureClosed ?? EnsureClosed)();
            if (Read(path) != expected) throw new InvalidOperationException("Settings changed during setup; the new write was cancelled.");
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
