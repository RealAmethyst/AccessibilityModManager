using System.Diagnostics;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Requests Steam's normal shutdown and waits before launch options are edited.</summary>
public sealed class SteamShutdownService(
    Func<bool>? steamIsRunning = null, Action<string>? requestShutdown = null)
{
    private readonly Func<bool> isRunning = steamIsRunning ?? IsSteamRunning;
    private readonly Action<string> shutdown = requestShutdown ?? RequestShutdown;

    public static bool IsSteamRunning() => Process.GetProcessesByName("steam").Length != 0;

    public async Task StopAsync(string steamRootPath, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (!isRunning()) return;
        shutdown(steamRootPath);
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        while (isRunning())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Steam is still running. Close it and any Steam games, then try again.");
            await Task.Delay(250, ct);
        }
    }

    private static void RequestShutdown(string steamRootPath)
    {
        var flatpak = steamRootPath.Contains(
            "/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal);
        var command = flatpak ? "flatpak" : "steam";
        var start = new ProcessStartInfo(command) { UseShellExecute = false };
        if (flatpak)
        {
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("com.valvesoftware.Steam");
        }
        start.ArgumentList.Add("-shutdown");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not ask Steam to close.");
    }
}
