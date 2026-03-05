using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Detection;

/// <summary>
/// Aggregates game definitions from all active plugins and detects installed games.
/// Handles the case where multiple plugins define the same game.
/// </summary>
public sealed class GameAggregator
{
    private readonly ISteamDetector _steamDetector;
    private readonly ILogger _logger;

    public GameAggregator(ISteamDetector steamDetector, ILogger logger)
    {
        _steamDetector = steamDetector;
        _logger = logger;
    }

    /// <summary>
    /// Detects all installed games across all active plugins.
    /// </summary>
    public async Task<List<GameInstall>> DetectAllGamesAsync(
        Dictionary<string, PluginRepoIndex> activePluginIndexes,
        Dictionary<string, string> manualOverrides,
        CancellationToken ct = default)
    {
        var allInstalls = new List<GameInstall>();

        foreach (var (pluginId, index) in activePluginIndexes)
        {
            ct.ThrowIfCancellationRequested();

            _logger.Information("Detecting games for plugin {PluginId} ({GameCount} games defined)",
                pluginId, index.Games.Count);

            // Steam auto-detection
            var detected = await _steamDetector.DetectInstalledGamesAsync(index.Games, pluginId, ct);
            allInstalls.AddRange(detected);

            // Apply manual overrides
            foreach (var game in index.Games)
            {
                // Skip if already detected via Steam
                if (detected.Any(d => d.Game.GameId == game.GameId))
                    continue;

                // Check manual overrides
                if (manualOverrides.TryGetValue(game.GameId, out var overridePath) &&
                    Directory.Exists(overridePath))
                {
                    allInstalls.Add(new GameInstall
                    {
                        Game = game,
                        PluginId = pluginId,
                        InstallPath = overridePath,
                        IsValid = true
                    });
                    _logger.Information("Using manual override for {Game}: {Path}", game.DisplayName, overridePath);
                }
            }
        }

        _logger.Information("Total detected game installs: {Count}", allInstalls.Count);
        return allInstalls;
    }

    /// <summary>
    /// Gets a deduplicated list of all games across active plugins (for UI display).
    /// Groups by gameId, showing which plugins support each game.
    /// </summary>
    public static Dictionary<string, List<(string PluginId, GameDefinition Game)>> GetGamesByGameId(
        Dictionary<string, PluginRepoIndex> activePluginIndexes)
    {
        var result = new Dictionary<string, List<(string, GameDefinition)>>();

        foreach (var (pluginId, index) in activePluginIndexes)
        {
            foreach (var game in index.Games)
            {
                if (!result.ContainsKey(game.GameId))
                    result[game.GameId] = [];

                result[game.GameId].Add((pluginId, game));
            }
        }

        return result;
    }
}
