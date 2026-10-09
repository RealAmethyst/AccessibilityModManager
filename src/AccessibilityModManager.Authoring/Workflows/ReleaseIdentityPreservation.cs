using System.Text.Json;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Authoring.Workflows;

/// <summary>
/// Prevents a release publication from silently deleting catalog rows that were already live.
/// Details of an existing row may be replaced, and new identities may be added; the stable
/// identity made from its bucket game id, version and channel must survive.
/// </summary>
public static class ReleaseIdentityPreservation
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly record struct Identity(string GameId, string Version, string Channel)
    {
        public override string ToString() => $"{GameId} {Version} ({Channel})";
    }

    /// <summary>
    /// Returns a refusal reason when <paramref name="candidateBytes"/> omits any release identity
    /// from <paramref name="liveBytes"/>; otherwise returns <see langword="null"/>.
    /// </summary>
    public static string? ValidateTransition(byte[]? liveBytes, byte[] candidateBytes)
    {
        ArgumentNullException.ThrowIfNull(candidateBytes);
        if (liveBytes is null)
            return null;

        try
        {
            var live = Deserialize(liveBytes, "live");
            var candidate = Deserialize(candidateBytes, "candidate");
            var candidateIdentities = Identities(candidate).ToHashSet();
            var missing = Identities(live)
                .Where(identity => !candidateIdentities.Contains(identity))
                .Distinct()
                .OrderBy(identity => identity.GameId, StringComparer.Ordinal)
                .ThenBy(identity => identity.Version, StringComparer.Ordinal)
                .ThenBy(identity => identity.Channel, StringComparer.Ordinal)
                .ToArray();

            return missing.Length == 0
                ? null
                : "Publishing this release would remove existing catalog release(s): " +
                  string.Join(", ", missing.Select(identity => identity.ToString())) + ".";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return $"Release preservation could not be verified: {ex.Message}";
        }
    }

    private static PluginRepoIndex Deserialize(byte[] bytes, string description) =>
        JsonSerializer.Deserialize<PluginRepoIndex>(bytes, JsonOptions) ??
        throw new InvalidOperationException($"The {description} catalog was empty.");

    private static IEnumerable<Identity> Identities(PluginRepoIndex index)
    {
        foreach (var (gameId, releases) in index.ReleasesByGameId)
        foreach (var release in releases)
            yield return new Identity(gameId, release.Version, release.Channel);
    }
}
