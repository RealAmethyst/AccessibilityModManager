using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface ISteamDetector
{
    Task<List<GameInstall>> DetectInstalledGamesAsync(IEnumerable<GameDefinition> knownGames, string pluginId, CancellationToken ct = default);
}
