namespace AccessibilityModManager.Core.Models;

/// <summary>
/// Declared package compatibility. Shared Windows/Linux packages resolve to the native runtime.
/// An omitted target on an older release means Windows, so the Linux manager
/// never silently installs a package that was only tested on Windows.
/// </summary>
public static class ReleaseTarget
{
    public const string Windows = "windows";
    public const string Proton = "proton";
    public const string Linux = "linux";
    public const string WindowsLinux = "windows-linux";

    public static string Normalize(string? target)
    {
        if (target is null) return Windows;
        return target switch
        {
            Windows or Proton or Linux or WindowsLinux => target,
            _ => throw new InvalidOperationException($"Unknown release target '{target}'.")
        };
    }

    /// <summary>Resolve a shared package to the native runtime of the installing manager.</summary>
    public static string ForRuntime(string? target)
    {
        var normalized = Normalize(target);
        if (normalized != WindowsLinux) return normalized;
        if (OperatingSystem.IsWindows()) return Windows;
        if (OperatingSystem.IsLinux()) return Linux;
        throw new PlatformNotSupportedException("Shared Windows/Linux packages need Windows or Linux.");
    }

    public static string DisplayName(string? target) => Normalize(target) switch
    {
        WindowsLinux => "Windows and native Linux",
        Windows => "Windows",
        Linux => "Native Linux",
        _ => "Proton"
    };

    public static void EnsureSupportedHere(string? target)
    {
        if (IsSupportedHere(target)) return;

        throw new PlatformNotSupportedException(
            $"This package targets {Normalize(target)}; it cannot be installed on this operating system.");
    }

    public static bool IsSupportedHere(string? target)
    {
        var normalized = Normalize(target);
        return OperatingSystem.IsWindows() && normalized is Windows or WindowsLinux ||
               OperatingSystem.IsLinux() && normalized is Proton or Linux or WindowsLinux;
    }
}
