using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Writes a prepared launch-option change only while Steam is closed. Each write replaces the
/// VDF atomically and preserves all unrelated text and the file's Unix permissions.
/// </summary>
public sealed class SteamLocalConfigStore(string configPath, Func<bool>? steamIsRunning = null)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Func<bool> _steamIsRunning = steamIsRunning ??
        (() => Process.GetProcessesByName("steam").Length != 0);

    public void EnsureSteamClosed()
    {
        if (_steamIsRunning())
            throw new InvalidOperationException(
                "Close Steam before the manager changes its saved launch options, then reopen Steam afterward.");
    }

    public async Task<string?> ReadLaunchOptionsAsync(string appId, CancellationToken ct = default)
    {
        var current = await ReadTextAsync(ct);
        return SteamLocalConfigEditor.Read(current.Text, appId);
    }

    public async Task<SteamLaunchOptionsPlan> PlanAsync(
        string appId, string wrapperPath, string gameExecutable,
        string launcherExecutable, string hostBridgeDirectory,
        CancellationToken ct = default)
    {
        var current = await ReadTextAsync(ct);
        var plan = SteamLaunchOptionsPlan.Create(
            SteamLocalConfigEditor.Read(current.Text, appId), wrapperPath,
            gameExecutable, launcherExecutable, hostBridgeDirectory);
        return plan with { AppWasPresent = SteamLocalConfigEditor.HasApp(current.Text, appId) };
    }

    public async Task<SteamLaunchOptionsPlan> PlanAsync(
        string appId, string wrapperPath, string gameExecutable,
        string gameDirectory, ProtonLaunchConfig launch, CancellationToken ct = default)
    {
        var current = await ReadTextAsync(ct);
        var plan = SteamLaunchOptionsPlan.Create(
            SteamLocalConfigEditor.Read(current.Text, appId), wrapperPath,
            gameExecutable, gameDirectory, launch);
        return plan with { AppWasPresent = SteamLocalConfigEditor.HasApp(current.Text, appId) };
    }

    public Task InstallAsync(string appId, SteamLaunchOptionsPlan plan, CancellationToken ct = default) =>
        ChangeAsync(text => SteamLocalConfigEditor.Install(text, appId, plan), ct);

    public Task RestoreAsync(string appId, SteamLaunchOptionsPlan plan, CancellationToken ct = default) =>
        ChangeAsync(text => SteamLocalConfigEditor.Restore(text, appId, plan), ct);

    private async Task ChangeAsync(Func<string, string> render, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Linux Steam configuration editing requires Linux.");
        EnsureSteamClosed();

        var original = await ReadTextAsync(ct);
        var changed = render(original.Text);
        if (changed == original.Text) return;

        var temp = configPath + ".amm-" + Guid.NewGuid().ToString("N");
        try
        {
            var content = StrictUtf8.GetBytes(changed);
            if (original.HasBom)
                content = [0xEF, 0xBB, 0xBF, .. content];
            await using (var output = new FileStream(temp, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(content, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            File.SetUnixFileMode(temp, File.GetUnixFileMode(configPath));

            if (_steamIsRunning())
                throw new InvalidOperationException("Steam started while launch options were being prepared. Try again after closing it.");
            var current = await File.ReadAllBytesAsync(configPath, ct);
            if (!SHA256.HashData(current).AsSpan().SequenceEqual(SHA256.HashData(original.Bytes)))
                throw new InvalidOperationException("Steam settings changed during setup. The manager left them untouched.");
            File.Move(temp, configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private async Task<(string Text, byte[] Bytes, bool HasBom)> ReadTextAsync(CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(configPath, ct);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = StrictUtf8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        return (text, bytes, hasBom);
    }
}
