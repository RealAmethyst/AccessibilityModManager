using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Microsoft.Win32;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Detection;

public sealed class SteamDetector : ISteamDetector
{
    private readonly IGameVerifier _gameVerifier;
    private readonly ILogger _logger;
    private readonly string? _steamRootOverride;

    public SteamDetector(IGameVerifier gameVerifier, ILogger logger, string? steamRootOverride = null)
    {
        _gameVerifier = gameVerifier;
        _logger = logger;
        _steamRootOverride = steamRootOverride;
    }

    public Task<List<GameInstall>> DetectInstalledGamesAsync(
        IEnumerable<GameDefinition> knownGames, string pluginId, CancellationToken ct = default)
    {
        var results = new List<GameInstall>();

        var steamRoots = _steamRootOverride is not null
            ? new List<string> { _steamRootOverride }
            : FindSteamPaths();
        if (steamRoots.Count == 0)
        {
            _logger.Warning("Steam installation not found");
            return Task.FromResult(results);
        }

        _logger.Information("Found Steam roots: {SteamRoots}", steamRoots);

        var libraryPaths = steamRoots.SelectMany(root =>
                GetLibraryPaths(root).Select(library => (Library: library, SteamRoot: root)))
            .GroupBy(pair => pair.Library,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        _logger.Information("Found {Count} Steam library folders", libraryPaths.Count);

        var gamesList = knownGames.ToList();
        var gamesWithSteamId = gamesList.Where(g => !string.IsNullOrEmpty(g.SteamAppId)).ToList();

        foreach (var (libraryPath, steamRoot) in libraryPaths)
        {
            ct.ThrowIfCancellationRequested();

            var commonPath = Path.Combine(libraryPath, "steamapps", "common");
            if (!Directory.Exists(commonPath))
                continue;

            // Check appmanifest files to match Steam App IDs to folder names
            var manifestsPath = Path.Combine(libraryPath, "steamapps");
            var appManifests = ParseAppManifests(manifestsPath);

            foreach (var game in gamesWithSteamId)
            {
                if (appManifests.TryGetValue(game.SteamAppId!, out var installDir))
                {
                    var gamePath = Path.Combine(commonPath, installDir);
                    if (Directory.Exists(gamePath) && _gameVerifier.VerifyInstallPath(game, gamePath) &&
                        !results.Any(result => result.Game.GameId == game.GameId))
                    {
                        var prefixPath = Path.Combine(libraryPath, "steamapps", "compatdata",
                            game.SteamAppId!, "pfx");
                        results.Add(new GameInstall
                        {
                            Game = game,
                            PluginId = pluginId,
                            InstallPath = gamePath,
                            SteamLibraryPath = libraryPath,
                            SteamRootPath = steamRoot,
                            ProtonPrefixPath = Directory.Exists(prefixPath) ? prefixPath : null,
                            IsValid = true
                        });
                        _logger.Information("Detected {Game} at {Path}", game.DisplayName, gamePath);
                    }
                }
            }
        }

        return Task.FromResult(results);
    }

    private List<string> FindSteamPaths()
    {
        if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) return [];

            string[] linuxPaths =
            [
                Path.Combine(home, ".local", "share", "Steam"),
                Path.Combine(home, ".steam", "root"),
                Path.Combine(home, ".steam", "steam"),
                Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")
            ];
            return linuxPaths.Where(Directory.Exists).ToList();
        }

        if (!OperatingSystem.IsWindows()) return [];

        // Try registry first (most reliable on Windows)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return [path];
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to read Steam path from HKCU registry");
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            var path = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return [path];
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to read Steam path from HKLM registry");
        }

        // Fallback: common install locations
        string[] commonPaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            @"C:\Steam",
            @"D:\Steam"
        ];

        foreach (var path in commonPaths)
        {
            if (Directory.Exists(path))
                return [path];
        }

        return [];
    }

    private List<string> GetLibraryPaths(string steamPath)
    {
        // Dedupe on a normalized form: the registry SteamPath uses forward slashes/lowercase
        // ("c:/program files (x86)/steam") while libraryfolders.vdf lists the same root with
        // backslashes ("C:\Program Files (x86)\Steam"). Without normalization the main library is
        // added twice and every game in it is detected twice.
        var paths = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        void AddIfNew(string p)
        {
            var norm = Normalize(p);
            if (seen.Add(norm)) paths.Add(norm);
        }

        AddIfNew(steamPath);

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
        {
            _logger.Warning("libraryfolders.vdf not found at {Path}", vdfPath);
            return paths;
        }

        try
        {
            var content = File.ReadAllText(vdfPath);
            var parsed = VdfParser.ParseLibraryFolders(content);
            foreach (var p in parsed)
            {
                if (Directory.Exists(p))
                    AddIfNew(p);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to parse libraryfolders.vdf");
        }

        return paths;
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    /// <summary>
    /// Parses appmanifest_*.acf files to map Steam App IDs to install directory names.
    /// </summary>
    private Dictionary<string, string> ParseAppManifests(string steamappsPath)
    {
        var result = new Dictionary<string, string>();

        if (!Directory.Exists(steamappsPath))
            return result;

        foreach (var acfFile in Directory.EnumerateFiles(steamappsPath, "appmanifest_*.acf"))
        {
            try
            {
                var content = File.ReadAllText(acfFile);
                var appId = ExtractAcfValue(content, "appid");
                var installDir = ExtractAcfValue(content, "installdir");

                // StateFlags bit 4 = fully installed. A manifest mid-download or mid-update is
                // not a usable install even when enough files exist to fool the probe rules.
                // A missing/unparseable StateFlags keeps the manifest (the verifier still gates).
                var stateFlags = ExtractAcfValue(content, "StateFlags");
                if (ulong.TryParse(stateFlags, out var flags) && (flags & 4) == 0)
                {
                    _logger.Debug("Skipping {AcfFile}: StateFlags {Flags} says not fully installed",
                        acfFile, flags);
                    continue;
                }

                if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(installDir))
                {
                    try
                    {
                        result[appId] = PathSafety.EnsureLeafFileName(installDir, "Steam install directory");
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger.Warning(ex, "Skipping unsafe Steam manifest {AcfFile}", acfFile);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to parse {AcfFile}", acfFile);
            }
        }

        return result;
    }

    private static string? ExtractAcfValue(string content, string key)
    {
        // ACF format: "key"    "value"
        var searchKey = $"\"{key}\"";
        var idx = content.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        idx += searchKey.Length;
        // Find the next quoted string
        var openQuote = content.IndexOf('"', idx);
        if (openQuote < 0) return null;
        var closeQuote = content.IndexOf('"', openQuote + 1);
        if (closeQuote < 0) return null;

        return content.Substring(openQuote + 1, closeQuote - openQuote - 1);
    }
}
