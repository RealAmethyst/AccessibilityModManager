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

    /// <summary>True when this value came from the local cache rather than the network.</summary>
    public bool FromCache { get; init; }

    /// <summary>When the cached copy was originally fetched from the network. Null for live fetches.</summary>
    public DateTimeOffset? CachedAtUtc { get; init; }

    /// <summary>
    /// Non-null when the LIVE document was reached and REFUSED, and this is a previously accepted
    /// copy that independently passed the whole gate again.
    ///
    /// <para>Distinct from <see cref="FromCache"/> alone, which until now could only mean "the
    /// network was unreachable". The two need saying differently: being offline is ordinary, and a
    /// served catalog failing verification is not. Refusing the live document is not the same as
    /// erasing the last good one — a hostile server can withhold a response entirely, so blanking
    /// the catalog buys nothing — but the user has to be told which of the two happened.</para>
    /// </summary>
    public string? LiveRejectionReason { get; init; }
}
