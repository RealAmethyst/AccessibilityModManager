using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// A reversible change to one Steam game's launch options. The caller must persist this plan
/// before applying it and only write through Steam's settings integration when Steam is closed.
/// </summary>
public sealed record SteamLaunchOptionsPlan(string Previous, string Installed)
{
    /// <summary>False when Steam had no LaunchOptions value before installation.</summary>
    public bool WasPresent { get; init; }
    /// <summary>False when the game had no app block in this Steam account yet.</summary>
    public bool AppWasPresent { get; init; } = true;

    public string Restore(string current)
    {
        if (!string.Equals(current, Installed, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Steam launch options changed after the mod was installed. Review them before restoring.");
        return Previous;
    }

    public static SteamLaunchOptionsPlan Create(
        string? current, string wrapperPath, string gameExecutable,
        string launcherExecutable, string hostBridgeDirectory)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Steam Proton launch plans require Linux.");
        if (!Path.IsPathFullyQualified(wrapperPath) || !File.Exists(wrapperPath) ||
            !Path.IsPathFullyQualified(gameExecutable) || !File.Exists(gameExecutable) ||
            !Path.IsPathFullyQualified(launcherExecutable) || !File.Exists(launcherExecutable) ||
            !Path.IsPathFullyQualified(hostBridgeDirectory) || !Directory.Exists(hostBridgeDirectory))
            throw new InvalidOperationException("Steam launch setup has a missing wrapper, game, launcher, or bridge directory.");

        var prefix = string.Join(' ',
            Quote(wrapperPath), Quote(gameExecutable), Quote(launcherExecutable),
            Quote(hostBridgeDirectory), "--");
        return CreateWithPrefix(current, prefix);
    }

    public static SteamLaunchOptionsPlan Create(
        string? current, string wrapperPath, string gameExecutable,
        string gameDirectory, ProtonLaunchConfig launch)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Steam Proton launch plans require Linux.");
        if (!Path.IsPathFullyQualified(wrapperPath) || !File.Exists(wrapperPath) ||
            !Path.IsPathFullyQualified(gameExecutable) || !File.Exists(gameExecutable))
            throw new InvalidOperationException("Steam launch setup has a missing wrapper or game executable.");
        var launcher = launch.LauncherPath is null ? null :
            PathSafety.CombineContained(gameDirectory, launch.LauncherPath);
        var bridge = launch.BridgeDirectory is null ? null :
            PathSafety.CombineContained(gameDirectory, launch.BridgeDirectory);
        if (launch.LaunchMode == "replaceExecutable" && (launcher is null || !File.Exists(launcher)) ||
            launch.LaunchMode == "direct" && launcher is not null ||
            launch.LaunchMode is not ("replaceExecutable" or "direct") ||
            bridge is not null && !Directory.Exists(bridge))
            throw new InvalidOperationException("Steam launch setup has a missing or invalid loader asset.");
        var prefix = string.Join(' ', Quote(wrapperPath), "--amm-v1", launch.LaunchMode,
            Quote(gameExecutable), Quote(launcher ?? "-"), Quote(bridge ?? "-"),
            Quote(launch.WineDllOverrides.Count == 0 ? "-" : string.Join(';', launch.WineDllOverrides)),
            launch.ReloadedRoot is null ? "0" : "1", "--");
        return CreateWithPrefix(current, prefix);
    }

    private static SteamLaunchOptionsPlan CreateWithPrefix(string? current, string prefix)
    {
        var previous = current ?? string.Empty;
        if (string.IsNullOrWhiteSpace(previous))
            return new SteamLaunchOptionsPlan(previous, prefix + " %command%")
            {
                WasPresent = current is not null
            };

        const string marker = "%command%";
        var first = previous.IndexOf(marker, StringComparison.Ordinal);
        if (first < 0 || previous.IndexOf(marker, first + marker.Length, StringComparison.Ordinal) >= 0 ||
            first > 0 && !char.IsWhiteSpace(previous[first - 1]) ||
            first + marker.Length < previous.Length && !char.IsWhiteSpace(previous[first + marker.Length]))
            throw new InvalidOperationException(
                "Existing Steam launch options do not contain one standalone %command% token. " +
                "Review them before adding the mod launcher.");

        var installed = previous[..first] + prefix + " " + marker + previous[(first + marker.Length)..];
        return new SteamLaunchOptionsPlan(previous, installed) { WasPresent = true };
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
