using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using Microsoft.Win32;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Detection;

public sealed class SteamDetector : ISteamDetector
{
    private readonly IGameVerifier _gameVerifier;
    private readonly ILogger _logger;

    public SteamDetector(IGameVerifier gameVerifier, ILogger logger)
    {
        _gameVerifier = gameVerifier;
        _logger = logger;
    }

    public Task<List<GameInstall>> DetectInstalledGamesAsync(
        IEnumerable<GameDefinition> knownGames, string pluginId, CancellationToken ct = default)
    {
        var results = new List<GameInstall>();

        var steamPath = FindSteamPath();
        if (steamPath == null)
        {
            _logger.Warning("Steam installation not found");
            return Task.FromResult(results);
        }

        _logger.Information("Found Steam at: {SteamPath}", steamPath);

        var libraryPaths = GetLibraryPaths(steamPath);
        _logger.Information("Found {Count} Steam library folders", libraryPaths.Count);

        var gamesList = knownGames.ToList();
        var gamesWithSteamId = gamesList.Where(g => !string.IsNullOrEmpty(g.SteamAppId)).ToList();

        foreach (var libraryPath in libraryPaths)
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
                    if (Directory.Exists(gamePath) && _gameVerifier.VerifyInstallPath(game, gamePath))
                    {
                        results.Add(new GameInstall
                        {
                            Game = game,
                            PluginId = pluginId,
                            InstallPath = gamePath,
                            IsValid = true
                        });
                        _logger.Information("Detected {Game} at {Path}", game.DisplayName, gamePath);
                    }
                }
            }
        }

        return Task.FromResult(results);
    }

    private string? FindSteamPath()
    {
        // Try registry first (most reliable on Windows)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;
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
                return path;
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
                return path;
        }

        return null;
    }

    private List<string> GetLibraryPaths(string steamPath)
    {
        var paths = new List<string> { steamPath };

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
                if (Directory.Exists(p) && !paths.Contains(p, StringComparer.OrdinalIgnoreCase))
                    paths.Add(p);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to parse libraryfolders.vdf");
        }

        return paths;
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

                if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(installDir))
                    result[appId] = installDir;
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
