namespace AccessibilityModManager.Core.Models;

/// <summary>
/// The local cache of "this user is signed in to Patreon" — access token, refresh token,
/// expiry, and the user's display name + email for the Settings UI. Persisted via DPAPI
/// (Q5=B) so a copied file can't be decrypted on another machine. Refreshed transparently
/// when the access token nears expiry.
/// </summary>
public sealed class PatreonAccount
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }

    /// <summary>UTC instant the access token stops being valid.</summary>
    public required DateTime ExpiresAt { get; init; }

    /// <summary>Patreon user id (the JSON:API <c>id</c> field on a User resource).</summary>
    public required string UserId { get; init; }

    public string? FullName { get; init; }
    public string? Email { get; init; }

    /// <summary>UTC instant the entitlement set was last fetched. Drives the Settings status line.</summary>
    public DateTime? LastEntitlementCheck { get; init; }
}

/// <summary>
/// One membership the user holds — i.e. one creator they support. Used by the gating
/// check: "is this campaign id in my memberships, and does at least one of my entitled
/// tier ids match the gate?"
/// </summary>
public sealed class PatreonMembership
{
    public required string CampaignId { get; init; }

    /// <summary>Tier ids the user is currently paying for on this campaign. Real-time —
    /// reflects cancellations, payment failures, and downgrades.</summary>
    public required List<string> CurrentlyEntitledTierIds { get; init; }

    /// <summary>Display label of the campaign for the Settings UI ("Foo Mods on Patreon").</summary>
    public string? CampaignDisplayName { get; init; }
}
