using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed record XivLauncherPaths(string SteamRoot, string DataRoot, string PayloadRoot,
    string ConfigPath, string GlobalSteamConfig)
{
    public static XivLauncherPaths Resolve(GameInstall game, string? homeOverride = null)
    {
        var steam = game.SteamRootPath ?? throw new InvalidOperationException("Install XIV through Steam so its installation can be detected.");
        if (Directory.Exists(steam))
            steam = new DirectoryInfo(steam).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(steam);
        if (steam.Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal))
            throw new PlatformNotSupportedException("XIVLauncher mod setup currently requires native Steam. Flatpak Steam's separate Wine paths and screen-reader access are not supported yet.");
        if (!Directory.Exists(steam)) throw new DirectoryNotFoundException("The detected Steam installation is missing.");
        var home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = Path.Combine(home, ".xlcore");
        PathSafety.EnsureSafeId(game.PluginId, "Plugin ID");
        PathSafety.EnsureSafeId(game.Game.GameId, "Game ID");
        var payload = PathSafety.CombineContained(data, "devPlugins", "amm", game.PluginId, game.Game.GameId);
        PathSafety.EnsureNoReparseTraversal(home, payload, "XIVLauncher plugin location");
        return new(steam, data, payload, Path.Combine(data, "dalamudConfig.json"), Path.Combine(steam, "config", "config.vdf"));
    }

    public string WinePath(string relative)
    {
        var prefix = Path.Combine(DataRoot, "wineprefix");
        if (Directory.Exists(prefix))
        {
            var drive = new DirectoryInfo(Path.Combine(prefix, "dosdevices", "z:"));
            if (drive.ResolveLinkTarget(true)?.FullName != "/")
                throw new InvalidOperationException("XIVLauncher's Wine Z drive does not map the Linux filesystem. Its plugin path could not be verified.");
        }
        // Wine creates Z: -> / for a new prefix. Existing prefixes must prove that mapping.
        return "Z:" + PathSafety.CombineContained(PayloadRoot, relative).Replace('/', '\\');
    }
}
