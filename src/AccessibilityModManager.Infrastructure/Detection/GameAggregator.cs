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
    private readonly IRegistryGameDetector _registryDetector;
    private readonly IGameVerifier _verifier;
    private readonly ILogger _logger;

    public GameAggregator(
        ISteamDetector steamDetector,
        IRegistryGameDetector registryDetector,
        IGameVerifier verifier,
        ILogger logger)
    {
        _steamDetector = steamDetector;
        _registryDetector = registryDetector;
        _verifier = verifier;
        _logger = logger;
    }

    /// <summary>
    /// What a detection pass found, plus any manual overrides it silently healed (re-pointed to a
    /// location that actually verifies). The caller persists healed entries to config — the
    /// aggregator itself never writes config.
    /// </summary>
    public sealed class DetectionResult
    {
        public required List<GameInstall> Installs { get; init; }
        public Dictionary<string, string> HealedOverrides { get; } = [];
    }

    /// <summary>
    /// Detects all installed games across all active plugins.
    ///
    /// A manual override is re-verified on every pass (audit finding 32): a folder that merely
    /// EXISTS is not a detected game — a stale or gutted override otherwise stays "detected"
    /// forever. When an override is missing or fails verification: for an emulator game the
    /// remembered emulator install (<paramref name="installedEmulators"/>, keyed by lowercased exe
    /// name) is tried next and, if it verifies, silently adopted as the healed override; then the
    /// registry probe gets its chance (a bad override must not block it). A failing override is
    /// never deleted — the entry stays in config so it can recover (e.g. an unplugged drive).
    /// </summary>
    public async Task<DetectionResult> DetectAllGamesAsync(
        Dictionary<string, PluginRepoIndex> activePluginIndexes,
        Dictionary<string, string> manualOverrides,
        Dictionary<string, string> installedEmulators,
        CancellationToken ct = default)
    {
        var result = new DetectionResult { Installs = [] };
        var allInstalls = result.Installs;

        foreach (var (pluginId, index) in activePluginIndexes)
        {
            ct.ThrowIfCancellationRequested();

            _logger.Information("Detecting games for plugin {PluginId} ({GameCount} games defined)",
                pluginId, index.Games.Count);

            // Steam auto-detection
            var detected = await _steamDetector.DetectInstalledGamesAsync(index.Games, pluginId, ct);
            allInstalls.AddRange(detected);

            // For games Steam didn't find: manual override first, then registry probe.
            foreach (var game in index.Games)
            {
                // Skip if already detected via Steam
                if (detected.Any(d => d.Game.GameId == game.GameId))
                    continue;

                // Manual override. This is also where an adopted ASCII junction lives after a
                // shimmed game's first install (the install flow writes the junction path here),
                // so the override deliberately takes precedence over the registry real-path.
                var hasOverride = manualOverrides.TryGetValue(game.GameId, out var overridePath);
                if (hasOverride && _verifier.VerifyInstallPath(game, overridePath!))
                {
                    allInstalls.Add(new GameInstall
                    {
                        Game = game,
                        PluginId = pluginId,
                        InstallPath = overridePath!,
                        IsValid = true
                    });
                    _logger.Information("Using manual override for {Game}: {Path}", game.DisplayName, overridePath);
                    continue;
                }

                if (hasOverride)
                {
                    _logger.Warning("Manual override for {Game} at {Path} no longer verifies; trying to heal",
                        game.DisplayName, overridePath);
                }

                // Emulator fallback: the same emulator may already be installed for another game
                // (or the override entry never got written). Gated to games that actually declare
                // an emulator-style installer (extractApp game-installer) — the same gate the
                // install flow's reuse path uses — so a non-emulator game whose ExeName happens to
                // match a recorded emulator can never adopt the wrong folder. The recorded value
                // is re-resolved through the wrapped-ZIP layout logic before verifying, mirroring
                // the reuse path; only a location that VERIFIES is adopted, and the healed pair is
                // reported so the caller persists it — the user never has to touch config by hand.
                var isEmulatorGame = game.Dependencies.Any(d =>
                    d.IsGameInstaller && d.Fix?.AutoInstall is ExtractAppAutoInstall);
                if (isEmulatorGame &&
                    !string.IsNullOrWhiteSpace(game.ExeName) &&
                    installedEmulators.TryGetValue(game.ExeName.ToLowerInvariant(), out var recordedEmulator))
                {
                    var emulatorRoot = Installer.PortableAppLayout.ResolveInstallRoot(recordedEmulator, game.ExeName)
                                       ?? recordedEmulator;
                    if (_verifier.VerifyInstallPath(game, emulatorRoot))
                    {
                        allInstalls.Add(new GameInstall
                        {
                            Game = game,
                            PluginId = pluginId,
                            InstallPath = emulatorRoot,
                            IsValid = true
                        });
                        result.HealedOverrides[game.GameId] = emulatorRoot;
                        _logger.Information("Healed {Game} from the remembered {Exe} install at {Path}",
                            game.DisplayName, game.ExeName, emulatorRoot);
                        continue;
                    }
                }

                // Registry-based detection for non-Steam games (e.g. Pokémon TCG Live). Returns
                // the REAL install path; for a game with an AsciiPathShim the junction isn't
                // created until the first install, so before setup the game detects at its real
                // (possibly non-ASCII) path. That's fine — detection only reads. The probe
                // verifies the path itself, so a game rescued here is a real install even when
                // the override above went stale (the override is kept; the next install re-adopts
                // the junction path over it).
                if (game.RegistryProbe is not null)
                {
                    var regPath = _registryDetector.ResolveInstallPath(game);
                    if (regPath is not null)
                    {
                        allInstalls.Add(new GameInstall
                        {
                            Game = game,
                            PluginId = pluginId,
                            InstallPath = regPath,
                            IsValid = true
                        });
                    }
                }
            }
        }

        _logger.Information("Total detected game installs: {Count}", allInstalls.Count);
        return result;
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
