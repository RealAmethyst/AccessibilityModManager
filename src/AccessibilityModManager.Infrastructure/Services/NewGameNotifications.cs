using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed record ObservedPluginGames(string SourceKey, string Author, IReadOnlyList<GameDefinition> Games);

public sealed class NewGameNotifications(IConfigService config)
{
    public async Task<IReadOnlyList<string>> ObserveAsync(IReadOnlyList<ObservedPluginGames> observations)
    {
        var messages = new List<string>();
        await config.UpdateAsync(state =>
        {
            foreach (var item in observations)
            {
                var current = item.Games.Select(game => game.GameId).ToHashSet(StringComparer.Ordinal);
                if (!state.KnownPluginGameIds.TryGetValue(item.SourceKey, out var known))
                {
                    state.KnownPluginGameIds[item.SourceKey] = current.ToList();
                    continue; // First successful observation establishes the baseline without a flood of popups.
                }
                var seen = known.ToHashSet(StringComparer.Ordinal);
                foreach (var game in item.Games)
                    if (seen.Add(game.GameId)) messages.Add($"{item.Author} just added {game.DisplayName}.");
                state.KnownPluginGameIds[item.SourceKey] = seen.ToList();
            }
        });
        return messages;
    }
}
