using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// The <c>proof</c> block carried inside index.json. Kept in the same file as the catalog it
/// vouches for, so publishing stays the single atomic rename it already is and no new upload step
/// appears in the author's flow. Managers older than the claim-verifying release ignore it, since
/// unknown JSON members are dropped on deserialization.
/// </summary>
public sealed class ClaimProofDocument
{
    [JsonPropertyName("scheme")]
    public string Scheme { get; set; } = ClaimTrustAnchor.SchemeV1;

    /// <summary>
    /// Informational only. Verification uses the key id from the SIGNED REGISTRY — this one is
    /// unsigned, and trusting it would let anyone relabel a claim set into a different key's
    /// namespace.
    /// </summary>
    [JsonPropertyName("keyId")]
    public string KeyId { get; set; } = "";

    [JsonPropertyName("claims")]
    public List<ClaimProofEntry> Claims { get; set; } = [];
}

public sealed class ClaimProofEntry
{
    /// <summary>The exact signed bytes. Base64 so they survive JSON transport unchanged — a
    /// verifier checks the signature over these bytes, never over a re-serialization.</summary>
    [JsonPropertyName("payload")]
    public string PayloadBase64 { get; set; } = "";

    [JsonPropertyName("signature")]
    public string SignatureBase64 { get; set; } = "";
}

public static class ClaimProof
{
    /// <summary>Bounds a hostile proof block before any signature work is attempted.</summary>
    public const int MaxClaims = 10_000;

    public static ClaimProofDocument Write(string keyId, IEnumerable<SignedClaim> claims) => new()
    {
        Scheme = ClaimTrustAnchor.SchemeV1,
        KeyId = keyId,
        Claims = claims.Select(c => new ClaimProofEntry
        {
            PayloadBase64 = Convert.ToBase64String(c.PayloadBytes),
            SignatureBase64 = Convert.ToBase64String(c.Signature)
        }).ToList()
    };

    /// <summary>
    /// Verifies every claim in a proof block against the anchor, then applies the whole-set rules.
    ///
    /// All-or-nothing on purpose: a bad claim in a published index means either an authoring fault
    /// or tampering, and "use the ones that verified" would let an attacker choose which parts of a
    /// catalog a reader sees simply by corrupting the rest.
    /// </summary>
    public static IReadOnlyList<SignedClaim> ReadVerified(ClaimProofDocument document, ClaimTrustAnchor anchor)
    {
        if (!string.Equals(document.Scheme, ClaimTrustAnchor.SchemeV1, StringComparison.Ordinal))
            throw new ClaimFormatException($"unsupported proof scheme '{document.Scheme}'");

        if (document.Claims.Count > MaxClaims)
            throw new ClaimFormatException($"proof carries more than {MaxClaims} claims");

        var verifier = new ClaimVerifier(anchor);
        var verified = new List<SignedClaim>(document.Claims.Count);

        foreach (var entry in document.Claims)
        {
            byte[] payload, signature;
            try
            {
                payload = Convert.FromBase64String(entry.PayloadBase64);
                signature = Convert.FromBase64String(entry.SignatureBase64);
            }
            catch (FormatException ex)
            {
                throw new ClaimFormatException("a claim in the proof is not valid base64", ex);
            }

            verified.Add(verifier.Verify(payload, signature));
        }

        ClaimVerifier.ValidateSet(verified);
        return verified;
    }

    /// <summary>
    /// Pulls the proof block out of a raw index document, or null when there is none — which is the
    /// normal state for an index published before claims existed.
    /// </summary>
    public static ClaimProofDocument? TryExtract(string indexJson)
    {
        using var document = JsonDocument.Parse(indexJson);
        if (!document.RootElement.TryGetProperty("proof", out var proof)) return null;
        if (proof.ValueKind != JsonValueKind.Object) return null;

        return JsonSerializer.Deserialize<ClaimProofDocument>(proof.GetRawText());
    }
}
