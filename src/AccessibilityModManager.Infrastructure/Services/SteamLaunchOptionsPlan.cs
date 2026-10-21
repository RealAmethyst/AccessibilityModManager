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

    /// <summary>
    /// Plans removal of only the manager's launch text from a value edited after install.
    /// Returns null when the value no longer contains the manager's wrapper.
    /// </summary>
    public SteamLaunchOptionsPlan? WithoutManagerText(string current)
    {
        if (RemoveManagerText(current) is not { } remaining) return null;
        var removeOption = !WasPresent && remaining.Trim() == CommandMarker;
        return new SteamLaunchOptionsPlan(removeOption ? string.Empty : remaining, current)
        {
            WasPresent = !removeOption,
            // Text the user added keeps the app block even when the manager created it.
            AppWasPresent = AppWasPresent || !removeOption
        };
    }

    /// <summary>
    /// The user's own launch options: <paramref name="current"/> without the manager's text,
    /// or null when nothing of the user's remains and none existed before install.
    /// </summary>
    public string? UserOptions(string? current)
    {
        if (current == Installed) return WasPresent ? Previous : null;
        if (current is null) return null;
        if (RemoveManagerText(current) is not { } remaining)
            return ContainsManagerText(current)
                ? throw new InvalidOperationException(
                    "Steam's launch option names the mod launcher in an unexpected form. " +
                    "Remove the launcher text from the game's Steam launch options, then try again.")
                : current;
        return !WasPresent && remaining.Trim() == CommandMarker ? null : remaining;
    }

    private const string CommandMarker = "%command%";
    private const string WrapperFileName = "steam-proton-launch";
    // Arguments after the wrapper, up to and including "--", in the two formats the manager writes.
    private const int ModernArguments = 8;
    private const int LegacyArguments = 4;

    /// <summary>True when the value still mentions the manager's launch wrapper.</summary>
    public static bool ContainsManagerText(string current) =>
        current.Contains("/" + WrapperFileName, StringComparison.Ordinal);

    /// <summary>
    /// Removes the manager's wrapper and its fixed arguments, in either format the manager has
    /// written, and keeps everything the user placed before or after it. Returns null when there
    /// is not exactly one complete wrapper text.
    /// </summary>
    public static string? RemoveManagerText(string current)
    {
        string? result = null;
        for (var start = 0; start < current.Length; start++)
        {
            if (current[start] != '\'' || start > 0 && !char.IsWhiteSpace(current[start - 1])) continue;
            var index = start;
            if (ReadToken(current, ref index) is not { } wrapper ||
                !wrapper.EndsWith("/" + WrapperFileName, StringComparison.Ordinal))
                continue;
            var afterWrapper = index;
            var first = ReadToken(current, ref index);
            var count = first == "--amm-v1" ? ModernArguments : LegacyArguments;
            index = afterWrapper;
            string? last = null;
            for (var argument = 0; argument < count; argument++)
                last = ReadToken(current, ref index);
            if (last != "--") continue;
            if (index < current.Length && current[index] == ' ') index++;
            if (result is not null) return null;
            result = current[..start] + current[index..];
        }
        return result;
    }

    /// <summary>Reads one shell word written by the manager: single-quoted parts and plain text.</summary>
    private static string? ReadToken(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        if (index >= text.Length) return null;
        var value = new System.Text.StringBuilder();
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            var c = text[index++];
            if (c == '\'')
            {
                var close = text.IndexOf('\'', index);
                if (close < 0) return null;
                value.Append(text, index, close - index);
                index = close + 1;
            }
            else if (c == '\\' && index < text.Length)
            {
                value.Append(text[index++]);
            }
            else
            {
                value.Append(c);
            }
        }
        return value.ToString();
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

    /// <summary>Checks that the manager's text can be placed before the user's %command%.</summary>
    public static void RequireCommandMarker(string? current)
    {
        if (string.IsNullOrWhiteSpace(current)) return;
        const string marker = CommandMarker;
        var first = current.IndexOf(marker, StringComparison.Ordinal);
        if (first < 0 || current.IndexOf(marker, first + marker.Length, StringComparison.Ordinal) >= 0 ||
            first > 0 && !char.IsWhiteSpace(current[first - 1]) ||
            first + marker.Length < current.Length && !char.IsWhiteSpace(current[first + marker.Length]))
            throw new InvalidOperationException(
                "Existing Steam launch options do not contain one standalone %command% token. " +
                "Review them before adding the mod launcher.");
    }

    private static SteamLaunchOptionsPlan CreateWithPrefix(string? current, string prefix)
    {
        var previous = current ?? string.Empty;
        if (string.IsNullOrWhiteSpace(previous))
            return new SteamLaunchOptionsPlan(previous, prefix + " %command%")
            {
                WasPresent = current is not null
            };

        RequireCommandMarker(previous);
        const string marker = CommandMarker;
        var first = previous.IndexOf(marker, StringComparison.Ordinal);
        var installed = previous[..first] + prefix + " " + marker + previous[(first + marker.Length)..];
        return new SteamLaunchOptionsPlan(previous, installed) { WasPresent = true };
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
