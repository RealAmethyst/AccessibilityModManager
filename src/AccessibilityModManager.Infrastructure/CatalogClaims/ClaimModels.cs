namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// What a claim asserts. A revocation withdraws a previously published object from a specific
/// audience — it is not merely "deleted", because a release can also be withdrawn from one tier
/// while continuing to exist for another.
/// </summary>
public enum ClaimKind
{
    Header,
    Game,
    Release,
    Revocation
}

/// <summary>
/// The identity of the catalog object a claim is about.
///
/// Structured fields, never a delimited string. A release is identified by version AND channel —
/// the index editor matches on both, so a stable and a beta release may legitimately share a
/// version number. Packing those into "release:game:version" would have collided them into one
/// object, and version strings are not validated against the delimiter either.
/// </summary>
public sealed record ClaimIdentity
{
    public required ClaimKind Kind { get; init; }

    /// <summary>Null for the header claim, which is about the plugin as a whole.</summary>
    public string? GameId { get; init; }

    /// <summary>Release only.</summary>
    public string? Channel { get; init; }

    /// <summary>Release only.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// Ordinal, field-wise equality. Case matters: ids and versions are compared exactly
    /// everywhere else in this codebase, and treating "1.0.0-Beta" and "1.0.0-beta" as one object
    /// would let two different releases contend for a single sequence.
    /// </summary>
    public bool Matches(ClaimIdentity other) =>
        Kind == other.Kind &&
        string.Equals(GameId, other.GameId, StringComparison.Ordinal) &&
        string.Equals(Channel, other.Channel, StringComparison.Ordinal) &&
        string.Equals(Version, other.Version, StringComparison.Ordinal);

    /// <summary>
    /// A stable string form for use as a dictionary key and in the manager's replay records. Built
    /// from length-prefixed parts so no value can be crafted to collide with a different identity.
    /// </summary>
    public string ToStorageKey()
    {
        var parts = new[] { Kind.ToString(), GameId ?? "", Channel ?? "", Version ?? "" };
        return string.Join("|", parts.Select(p => $"{p.Length}:{p}"));
    }
}

/// <summary>
/// Who is allowed to be shown a claim. Signed, so the server can narrow disclosure but can never
/// widen it, and so the correct audience survives a server restart with no other state.
/// </summary>
public sealed record ClaimAudience
{
    /// <summary>Everyone, signed in or not.</summary>
    public required bool Public { get; init; }

    /// <summary>Patreon campaign, when not public.</summary>
    public string? CampaignId { get; init; }

    /// <summary>Tiers entitled to see this, when not public.</summary>
    public IReadOnlyList<string> TierIds { get; init; } = [];

    public static ClaimAudience Everyone { get; } = new() { Public = true };

    /// <summary>
    /// True when a caller holding <paramref name="entitledTierIds"/> on <paramref name="campaignId"/>
    /// may be shown this claim. A caller with no memberships sees public claims only.
    /// </summary>
    public bool Admits(string? campaignId, IReadOnlyCollection<string>? entitledTierIds)
    {
        if (Public) return true;
        if (string.IsNullOrEmpty(CampaignId) || entitledTierIds is null || entitledTierIds.Count == 0)
            return false;
        if (!string.Equals(CampaignId, campaignId, StringComparison.Ordinal)) return false;
        return TierIds.Any(t => entitledTierIds.Contains(t, StringComparer.Ordinal));
    }

    public bool SameAs(ClaimAudience other) =>
        Public == other.Public &&
        string.Equals(CampaignId, other.CampaignId, StringComparison.Ordinal) &&
        TierIds.Count == other.TierIds.Count &&
        !TierIds.Except(other.TierIds, StringComparer.Ordinal).Any();
}

/// <summary>
/// The document that gets signed. Its exact serialized bytes are what the signature covers and
/// what is transported, so a verifier never has to reproduce them — see <see cref="SignedClaim"/>.
/// </summary>
public sealed record ClaimPayload
{
    /// <summary>Format version. A verifier refuses anything it does not know.</summary>
    public required int V { get; init; }

    /// <summary>
    /// Hex SHA-256 binding this claim to the plugin, the exact index URL the signed registry
    /// names, and the key. Without it, re-pointing a plugin to a new index while keeping its key
    /// would let a hostile server replay the old source's claims into the new context.
    /// </summary>
    public required string TrustContext { get; init; }

    public required ClaimKind Kind { get; init; }

    public required ClaimIdentity Identity { get; init; }

    /// <summary>
    /// Per-object counter, raised when this object's body changes. Not derived from the index's
    /// repoVersion, which is a free-form string that the AuthorTool never increments.
    /// </summary>
    public required long Seq { get; init; }

    public required ClaimAudience Audience { get; init; }

    /// <summary>
    /// The object as the manager should read it. Kept as raw JSON so a body is never re-shaped on
    /// the way through — the bytes that were signed are the bytes that get interpreted.
    /// </summary>
    public required string BodyJson { get; init; }
}

/// <summary>
/// A claim as it travels: the exact signed bytes plus the signature over them.
///
/// The payload is transported verbatim rather than re-serialized, which is what lets verification
/// avoid canonicalisation entirely. Two implementations never need to independently produce
/// identical bytes; they only need to read the bytes they were given, strictly and identically.
/// </summary>
public sealed record SignedClaim
{
    public required byte[] PayloadBytes { get; init; }
    public required byte[] Signature { get; init; }

    /// <summary>The parsed payload. Only ever produced by a strict reader that has already
    /// rejected duplicate members, unknown envelope fields and malformed values.</summary>
    public required ClaimPayload Payload { get; init; }
}
