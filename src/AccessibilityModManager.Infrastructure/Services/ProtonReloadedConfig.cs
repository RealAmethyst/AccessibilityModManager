using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed record OwnedProtonConfigFile(
    string Path, string InstalledBase64, string? PreviousBase64, int? PreviousMode);

/// <summary>
/// Keeps Reloaded II's prefix-local pointer and per-game AppConfig stable across game launches.
/// Existing, different loader configurations are refused because replacing them would disable
/// another mod in the same prefix.
/// </summary>
public static class ProtonReloadedConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<OwnedProtonConfigFile> Plan(GameInstall game, ProtonLaunchConfig launch)
    {
        if (!OperatingSystem.IsLinux() || launch.ReloadedRoot is null || launch.ReloadedModId is null ||
            game.ProtonPrefixPath is null || game.Game.ExeName is null)
            throw new InvalidOperationException("Reloaded II Proton setup needs a game prefix and loader metadata.");

        var prefix = game.ProtonPrefixPath;
        var zDrive = Path.Combine(prefix, "dosdevices", "z:");
        if (Directory.ResolveLinkTarget(zDrive, returnFinalTarget: true)?.FullName != "/")
            throw new InvalidOperationException("The Proton prefix has no Z: mapping to the host filesystem.");
        var users = Path.Combine(prefix, "drive_c", "users");
        var roaming = Directory.EnumerateDirectories(users)
            .Select(user => Path.Combine(user, "AppData", "Roaming"))
            .Where(Directory.Exists)
            .ToArray();
        if (roaming.Length != 1)
            throw new InvalidOperationException("Could not identify one Proton user's Roaming AppData directory.");

        var root = PathSafety.CombineContained(game.InstallPath, launch.ReloadedRoot);
        if (!Directory.Exists(root))
            throw new InvalidOperationException("The package did not install its Reloaded II root.");
        var exeName = Path.GetFileName(game.Game.ExeName);
        var gameExe = PathSafety.CombineContained(game.InstallPath, game.Game.ExeName);
        var appConfigPath = PathSafety.CombineContained(root, "Apps", exeName.ToLowerInvariant(), "AppConfig.json");
        var pointerPath = Path.Combine(roaming[0], "Reloaded-Mod-Loader-II", "ReloadedII.json");
        EnsureOwnedPath(game, pointerPath);
        EnsureOwnedPath(game, appConfigPath);
        if (File.Exists(pointerPath + ".dsts_backup"))
            throw new InvalidOperationException(
                "A previous Time Stranger launcher left a Reloaded II pointer backup in this prefix. " +
                "Recover that previous setup before installing this managed one.");

        var pointerContent = JsonSerializer.Serialize(new
        {
            LoaderPath32 = WinePath(Path.Combine(root, "Loader", "X86", "Reloaded.Mod.Loader.dll")),
            LoaderPath64 = WinePath(Path.Combine(root, "Loader", "X64", "Reloaded.Mod.Loader.dll")),
            LauncherPath = "",
            ApplicationConfigDirectory = WinePath(Path.Combine(root, "Apps")),
            ModConfigDirectory = WinePath(Path.Combine(root, "Mods")),
            PluginConfigDirectory = WinePath(Path.Combine(root, "Plugins")),
            ShowConsole = false
        }, JsonOptions) + "\n";
        var appContent = JsonSerializer.Serialize(new
        {
            AppId = exeName.ToLowerInvariant(),
            AppName = exeName,
            AppLocation = WinePath(gameExe),
            AppArguments = "",
            AppIcon = "",
            AutoInject = false,
            EnabledMods = new[] { launch.ReloadedModId },
            WorkingDirectory = WinePath(Path.GetDirectoryName(gameExe)!),
            PluginData = new Dictionary<string, object>(),
            SortedMods = Array.Empty<string>(),
            PreserveDisabledModOrder = true,
            DontInject = false,
            IsMsStore = false
        }, JsonOptions) + "\n";

        return [Capture(pointerPath, pointerContent), Capture(appConfigPath, appContent)];
    }

    public static void Apply(GameInstall game, IReadOnlyList<OwnedProtonConfigFile> files)
    {
        foreach (var file in files)
        {
            EnsureOwnedPath(game, file.Path);
            var installed = Convert.FromBase64String(file.InstalledBase64);
            var previous = file.PreviousBase64 is null ? null : Convert.FromBase64String(file.PreviousBase64);
            var current = File.Exists(file.Path) ? File.ReadAllBytes(file.Path) : null;
            if (Equal(current, installed)) continue;
            if (!Equal(current, previous))
                throw new InvalidOperationException($"Reloaded II config changed during setup: {file.Path}");
            Write(file.Path, installed, file.PreviousMode);
        }
    }

    /// <summary>
    /// Restores each owned file to its state before install. A mod launcher may rewrite these
    /// files on every launch, so a changed file is first copied to <paramref name="recoveryDirectory"/>.
    /// Returns the recovery copies that were written.
    /// </summary>
    public static IReadOnlyList<string> Restore(GameInstall game, IReadOnlyList<OwnedProtonConfigFile> files,
        string recoveryDirectory)
    {
        foreach (var file in files) EnsureOwnedPath(game, file.Path);
        var recovered = new List<string>();
        for (var index = files.Count - 1; index >= 0; index--)
        {
            var file = files[index];
            var installed = Convert.FromBase64String(file.InstalledBase64);
            var previous = file.PreviousBase64 is null ? null : Convert.FromBase64String(file.PreviousBase64);
            var current = File.Exists(file.Path) ? File.ReadAllBytes(file.Path) : null;
            if (Equal(current, previous)) continue;
            if (current is not null && !Equal(current, installed))
            {
                var copy = Path.Combine(recoveryDirectory, index + "-" + Path.GetFileName(file.Path));
                Write(copy, current, null);
                recovered.Add(copy);
            }
            if (previous is null) File.Delete(file.Path);
            else Write(file.Path, previous, file.PreviousMode);
        }
        return recovered;
    }

    private static OwnedProtonConfigFile Capture(string path, string content)
    {
        var installed = Encoding.UTF8.GetBytes(content);
        var previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (previous is not null && !Equal(previous, installed))
            throw new InvalidOperationException(
                $"An existing Reloaded II config differs from this mod's setup: {path}. Review it before installing.");
        return new OwnedProtonConfigFile(path, Convert.ToBase64String(installed),
            previous is null ? null : Convert.ToBase64String(previous),
            previous is null || !OperatingSystem.IsLinux() ? null : (int)File.GetUnixFileMode(path));
    }

    private static void EnsureOwnedPath(GameInstall game, string path)
    {
        var root = PathSafety.IsContained(game.InstallPath, path)
            ? game.InstallPath
            : game.ProtonPrefixPath is { } prefix && PathSafety.IsContained(prefix, path)
                ? prefix
                : throw new InvalidOperationException("Reloaded II config is outside the game and its Proton prefix.");
        PathSafety.EnsureNoReparseTraversal(root, path, "Reloaded II config");
    }

    private static string WinePath(string unixPath)
    {
        var full = Path.GetFullPath(unixPath);
        if (!full.StartsWith('/') || full.Contains('\\') || full.Contains(':'))
            throw new InvalidOperationException("The game path cannot be represented through Proton's Z: drive.");
        return "Z:" + full.Replace('/', '\\');
    }

    private static bool Equal(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static void Write(string path, byte[] content, int? previousMode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".amm-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temp, previousMode is null
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                    : (UnixFileMode)previousMode.Value);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
