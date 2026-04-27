using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

/// <summary>
/// Persists the user's Patreon access + refresh tokens locally, encrypted via Windows
/// DPAPI (Q5=B). Same machine + same Windows user can read; copying the file to another
/// machine yields garbage. Returns null when the user isn't signed in.
/// </summary>
public interface IPatreonAccountStore
{
    Task<PatreonAccount?> LoadAsync();
    Task SaveAsync(PatreonAccount account);
    Task ClearAsync();
}

/// <summary>
/// In-memory cache of the user's currently-entitled tier IDs per campaign. Manager calls
/// the Patreon API at install time per Q4=A and tucks the result here so back-to-back
/// installs in one session don't re-query. The cache is invalidated when the user signs
/// out, the token refresh fails, or the user clicks "Refresh Patreon status" in Settings.
/// </summary>
public interface IPatreonEntitlementCache
{
    /// <summary>
    /// True when the user is signed in (token present) AND we have a fresh entitlement
    /// snapshot. False if either is missing — caller should kick off a fetch.
    /// </summary>
    bool HasFresh { get; }

    /// <summary>The user's current memberships, or empty when not signed in.</summary>
    IReadOnlyList<PatreonMembership> Memberships { get; }

    void Set(IReadOnlyList<PatreonMembership> memberships);
    void Invalidate();
}
