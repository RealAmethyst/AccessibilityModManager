using System.Reflection.PortableExecutable;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Detection;

/// <summary>
/// Fills the executable omitted by older Steam catalog entries. Steam's own launch configuration
/// is used when it names one Windows executable; otherwise the game install must contain one
/// unambiguous Windows executable in the root or one directory below it.
/// No game-specific names are kept here.
/// </summary>
public static class SteamGameExecutableResolver
{
    public static GameInstall Resolve(GameInstall install)
    {
        if (!OperatingSystem.IsLinux() || !string.IsNullOrWhiteSpace(install.Game.ExeName))
            return install;
        if (string.IsNullOrWhiteSpace(install.Game.EffectiveSteamAppId))
            throw new InvalidOperationException("This catalog game has no Steam App ID.");

        // Steam's own launch configuration names the executable it starts for this game.
        var launched = install.SteamRootPath is null ? null
            : SteamAppInfo.LaunchExecutable(install.SteamRootPath, install.Game.EffectiveSteamAppId);
        if (launched is not null && !launched.Split('/').Any(part => part is "" or "." or ".."))
        {
            var launchedPath = Path.Combine(install.InstallPath, launched);
            if (File.Exists(launchedPath) && IsWindowsExecutable(install.InstallPath, launchedPath))
                return WithExecutable(install, launchedPath);
        }

        var directories = new[] { install.InstallPath }
            .Concat(Directory.EnumerateDirectories(install.InstallPath)
                .Where(directory => !new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)));
        var candidates = directories.SelectMany(directory =>
                Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            .Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsUnityCrashHandler(Path.GetFileName(path)))
            .Where(path => IsWindowsExecutable(install.InstallPath, path))
            .ToList();
        var title = NormalizeName(install.Game.DisplayName);
        var matching = candidates.Where(path =>
            NormalizeName(Path.GetFileNameWithoutExtension(path)) == title).ToList();
        var chosen = matching.Count == 1 ? matching[0] :
            matching.Count == 0 && candidates.Count == 1 ? candidates[0] : null;
        if (chosen is null)
            throw new InvalidOperationException(
                $"Steam found {install.Game.DisplayName}, but the catalog does not name its executable " +
                "and the game folder has no unique title-matching executable in its root or direct subfolders. " +
                "The author needs to name the executable or publish a Proton package.");

        return WithExecutable(install, chosen);
    }

    private static GameInstall WithExecutable(GameInstall install, string chosen)
    {
        var original = install.Game;
        var game = new GameDefinition
        {
            GameId = original.GameId, DisplayName = original.DisplayName,
            ModName = original.ModName, Description = original.Description,
            SteamAppId = original.SteamAppId, LinuxSteamAppId = original.LinuxSteamAppId,
            LinuxExeName = original.LinuxExeName, LinuxProbeRules = original.LinuxProbeRules,
            ExeName = Path.GetRelativePath(install.InstallPath, chosen).Replace('\\', '/'),
            ProbeRules = original.ProbeRules, RegistryProbe = original.RegistryProbe,
            AsciiPathShim = original.AsciiPathShim, Dependencies = original.Dependencies,
            Tags = original.Tags, Languages = original.Languages,
            DefaultPreInstall = original.DefaultPreInstall,
            DefaultPostInstall = original.DefaultPostInstall,
            DefaultPostUninstall = original.DefaultPostUninstall
        };
        return new GameInstall
        {
            Game = game, PluginId = install.PluginId, InstallPath = install.InstallPath,
            SteamLibraryPath = install.SteamLibraryPath, SteamRootPath = install.SteamRootPath,
            ProtonPrefixPath = install.ProtonPrefixPath, IsValid = install.IsValid,
            DetectedVersion = install.DetectedVersion, ModState = install.ModState
        };
    }

    private static bool IsWindowsExecutable(string gameDirectory, string path)
    {
        try
        {
            PathSafety.EnsureNoReparseTraversal(gameDirectory, path, "game executable");
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.PEHeaders.CoffHeader.Machine is Machine.Amd64 or Machine.I386;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string NormalizeName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool IsUnityCrashHandler(string fileName) =>
        fileName.Equals("UnityCrashHandler.exe", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("UnityCrashHandler32.exe", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("UnityCrashHandler64.exe", StringComparison.OrdinalIgnoreCase);
}
