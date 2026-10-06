namespace AccessibilityModManager.Core.Models;

/// <summary>
/// Package runtime. An omitted target on an older release means Windows, so the Linux manager
/// never silently installs a package that was only tested on Windows.
/// </summary>
public static class ReleaseTarget
{
    public const string Windows = "windows";
    public const string Proton = "proton";
    public const string Linux = "linux";

    public static string Normalize(string? target)
    {
        if (target is null) return Windows;
        return target switch
        {
            Windows or Proton or Linux => target,
            _ => throw new InvalidOperationException($"Unknown release target '{target}'.")
        };
    }

    public static void EnsureSupportedHere(string? target)
    {
        if (IsSupportedHere(target)) return;

        throw new PlatformNotSupportedException(
            $"This package targets {Normalize(target)}; it cannot be installed on this operating system.");
    }

    public static bool IsSupportedHere(string? target)
    {
        var normalized = Normalize(target);
        return OperatingSystem.IsWindows() && normalized == Windows ||
               OperatingSystem.IsLinux() && normalized is Proton or Linux;
    }
}
