namespace AccessibilityModManager.Core.Models;

/// <summary>
/// A fetch result that knows whether it came from the network or from the local last-good cache.
/// Offline (or when the catalog host is down), the manager serves the cached registry/indexes so
/// installed mods stay reachable — but the UI must SAY the data is cached, so
/// <see cref="FromCache"/> and <see cref="CachedAtUtc"/> travel with the value.
/// </summary>
public sealed class Fetched<T>
{
    public required T Value { get; init; }

    /// <summary>True when the network fetch failed and this value came from the local cache.</summary>
    public bool FromCache { get; init; }

    /// <summary>When the cached copy was originally fetched from the network. Null for live fetches.</summary>
    public DateTimeOffset? CachedAtUtc { get; init; }
}
