using System.Text.Json;
using System.Text.Json.Serialization;
using AccessibilityModManager.Core.Models;

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
    public required string Scheme { get; init; }

    /// <summary>
    /// Must equal the key id the SIGNED REGISTRY names. Nothing is ever taken from here — the
    /// anchor is the registry's — but a disagreement means the file and the registry describe
    /// different keys, and publishing or trusting either reading is worse than stopping.
    /// </summary>
    [JsonPropertyName("keyId")]
    public required string KeyId { get; init; }

    [JsonPropertyName("algorithm")]
    public required string Algorithm { get; init; }

    /// <summary>
    /// The publisher's commitment to the whole set. Absent in responses the server has filtered:
    /// a manager must never require it, because the API strips it from every reply. The publisher
    /// requires it, because for the publisher its absence is the attack.
    /// </summary>
    [JsonPropertyName("manifest")]
    public ClaimProofEntry? Manifest { get; init; }

    [JsonPropertyName("claims")]
    public required IReadOnlyList<ClaimProofEntry> Claims { get; init; }
}

/// <summary>Exact signed bytes plus the signature over them, both base64.</summary>
public sealed record ClaimProofEntry(
    [property: JsonPropertyName("payload")] string PayloadBase64,
    [property: JsonPropertyName("signature")] string SignatureBase64);

/// <summary>
/// A proof that has been verified end to end, with the manifest it arrived under.
///
/// <para>Only <see cref="ClaimProof.ReadVerified"/> can make one, and that is the point rather than
/// an accident of layering. This type is the evidence that signatures were checked, that the whole
/// set was validated, and that the catalog inside it was rebuilt from claims — so a caller in
/// another assembly cannot assemble the conclusion without having done the work. A record with a
/// public constructor would let anyone declare a hostile document verified.</para>
/// </summary>
public sealed class VerifiedProof
{
    internal VerifiedProof(IReadOnlyList<SignedClaim> claims, SignedManifest? manifest, string catalogJson)
    {
        Claims = claims;
        Manifest = manifest;
        CatalogJson = catalogJson;
    }

    public IReadOnlyList<SignedClaim> Claims { get; }

    public SignedManifest? Manifest { get; }

    /// <summary>
    /// The catalog these claims describe, rebuilt from the claims alone. It carries no proof and
    /// nothing from the document the proof travelled inside — which is the point: the plaintext
    /// beside a proof is not covered by the manifest, so a server is free to rewrite it, and anyone
    /// acting on the published catalog has to read it from here rather than from what they were
    /// handed.
    /// </summary>
    public string CatalogJson { get; }
}

public static class ClaimProof
{
    /// <summary>Bounds a hostile proof block before any signature work is attempted.</summary>
    public const int MaxClaims = 10_000;

    /// <summary>
    /// Total transported size of a proof. The per-claim limit alone was not a limit: ten thousand
    /// claims at 256 KiB each is gigabytes, all of it buffered and base64-decoded before a single
    /// signature is checked.
    /// </summary>
    public const int MaxProofBytes = 8 * 1024 * 1024;

    /// <summary>The whole index, before it is parsed at all.</summary>
    public const int MaxIndexBytes = 16 * 1024 * 1024;

    public static ClaimProofDocument Write(
        ClaimTrustAnchor anchor, SignedManifest manifest, IEnumerable<SignedClaim> claims) => new()
    {
        Scheme = ClaimTrustAnchor.SchemeV1,
        KeyId = anchor.KeyId,
        Algorithm = anchor.Algorithm,
        Manifest = new ClaimProofEntry(
            Convert.ToBase64String(manifest.PayloadBytes),
            Convert.ToBase64String(manifest.Signature)),
        Claims = claims.Select(c => new ClaimProofEntry(
            Convert.ToBase64String(c.PayloadBytes),
            Convert.ToBase64String(c.Signature))).ToList()
    };

    /// <summary>
    /// Verifies every claim in a proof block against the anchor, then applies the whole-set rules.
    ///
    /// All-or-nothing on purpose: a bad claim in a published index means either an authoring fault
    /// or tampering, and "use the ones that verified" would let an attacker choose which parts of a
    /// catalog a reader sees simply by corrupting the rest.
    /// </summary>
    /// <param name="requireManifest">
    /// True for a publisher extending its own history, where a missing manifest is exactly the
    /// omission attack. False for a consumer, where the server has legitimately stripped it.
    /// </param>
    public static VerifiedProof ReadVerified(
        ClaimProofDocument document, ClaimTrustAnchor anchor, bool requireManifest)
    {
        if (!string.Equals(document.Scheme, ClaimTrustAnchor.SchemeV1, StringComparison.Ordinal))
            throw new ClaimFormatException($"unsupported proof scheme '{document.Scheme}'");
        if (!string.Equals(document.KeyId, anchor.KeyId, StringComparison.Ordinal))
            throw new ClaimFormatException(
                $"the proof names key '{document.KeyId}' but the registry names '{anchor.KeyId}'");
        if (!string.Equals(document.Algorithm, anchor.Algorithm, StringComparison.Ordinal))
            throw new ClaimFormatException(
                $"the proof names algorithm '{document.Algorithm}' but the registry names '{anchor.Algorithm}'");

        if (document.Claims.Count > MaxClaims)
            throw new ClaimFormatException($"proof carries more than {MaxClaims} claims");

        var transported = document.Claims.Sum(c => (long)c.PayloadBase64.Length + c.SignatureBase64.Length);
        if (document.Manifest is not null)
            transported += document.Manifest.PayloadBase64.Length + document.Manifest.SignatureBase64.Length;
        if (transported > MaxProofBytes)
            throw new ClaimFormatException($"proof exceeds {MaxProofBytes} bytes");

        using var verifier = new ClaimVerifier(anchor);
        var verified = new List<SignedClaim>(document.Claims.Count);

        foreach (var entry in document.Claims)
        {
            var (payload, signature) = Decode(entry, "a claim");
            verified.Add(verifier.Verify(payload, signature));
        }

        ClaimVerifier.ValidateSet(verified);

        // A valid signature is necessary and nowhere near sufficient. Everything the author's own
        // rules say about content is applied here, after the cryptography and before anyone acts on
        // it — same implementation the manager runs, so the two cannot drift apart. What comes back
        // is the catalog those claims describe, which is the only version of it anyone may act on.
        var catalog = ClaimAcceptance.Accept(verified, anchor);

        SignedManifest? manifest = null;
        if (document.Manifest is not null)
        {
            var (payload, signature) = Decode(document.Manifest, "the proof manifest");
            manifest = verifier.VerifyManifest(payload, signature);

            var actual = ClaimDigest.Compute(verified);
            if (!string.Equals(manifest.Manifest.ClaimsDigest, actual, StringComparison.Ordinal))
            {
                throw new ClaimFormatException(
                    "the claims in this proof are not the ones its manifest was signed over — one " +
                    "has been added, removed or replaced since it was published");
            }
        }
        else if (requireManifest)
        {
            throw new ClaimFormatException(
                "this proof carries no manifest, so there is no way to tell whether any of it was " +
                "removed");
        }

        return new VerifiedProof(verified, manifest, catalog);
    }

    /// <summary>
    /// Base64 has more than one spelling of the same bytes — embedded whitespace, non-canonical
    /// trailing bits. Re-encoding and comparing pins one, so two implementations decoding the same
    /// transport reach the same bytes or neither does.
    /// </summary>
    private static (byte[] Payload, byte[] Signature) Decode(ClaimProofEntry entry, string what)
    {
        return (One(entry.PayloadBase64, "payload"), One(entry.SignatureBase64, "signature"));

        byte[] One(string text, string field)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(text);
            }
            catch (FormatException ex)
            {
                throw new ClaimFormatException($"{what} has a {field} that is not valid base64", ex);
            }

            if (!string.Equals(Convert.ToBase64String(bytes), text, StringComparison.Ordinal))
                throw new ClaimFormatException($"{what} has a {field} whose base64 is not canonical");

            return bytes;
        }
    }

    /// <summary>
    /// Pulls the proof block out of a raw index document, or null when there is genuinely none —
    /// the normal state for an index published before claims existed.
    ///
    /// Read as strictly as the payloads inside it. An index carrying two <c>proof</c> members lets
    /// one implementation select the first and another the last, each internally valid, each with a
    /// manifest that commits only to decoded claim payloads and says nothing about which outer
    /// member a parser picked. And only a genuinely ABSENT proof may be reported as absent: null, a
    /// scalar, an array or a malformed object are trust violations, because "there is no proof
    /// here" is the one answer that leads to starting a history over.
    /// </summary>
    public static ClaimProofDocument? TryExtract(ReadOnlyMemory<byte> indexBytes)
    {
        // Bytes, not a string. Decoding first with .NET's default replacement fallback would turn
        // invalid UTF-8 in an unsigned outer field into U+FFFD and carry on — this implementation
        // accepting what another one rejects, over the same file. Parsing the bytes directly gets
        // strict UTF-8 validation from the parser itself. It also means the size cap counts the
        // bytes it advertises rather than UTF-16 characters, which a multi-byte document can hide
        // behind.
        if (indexBytes.Length > MaxIndexBytes)
            throw new ClaimFormatException($"the index exceeds {MaxIndexBytes} bytes");
        if (indexBytes.Length == 0)
            throw new ClaimFormatException("the index is empty");

        var span = indexBytes.Span;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            throw new ClaimFormatException("the index has a UTF-8 BOM");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(indexBytes,
                new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException ex)
        {
            throw new ClaimFormatException(
                "the index is not valid UTF-8 JSON, or repeats a member", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ClaimFormatException("the index is not a JSON object");
            if (!document.RootElement.TryGetProperty("proof", out var proof)) return null;
            if (proof.ValueKind != JsonValueKind.Object)
                throw new ClaimFormatException(
                    $"the index has a 'proof' member that is not an object (it is {proof.ValueKind})");

            ClaimCodec.RejectUnknownMembers(proof, "proof",
                ["scheme", "keyId", "algorithm", "manifest", "claims"]);

            if (!proof.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array)
                throw new ClaimFormatException("the proof has no 'claims' array");
            if (claims.GetArrayLength() > MaxClaims)
                throw new ClaimFormatException($"proof carries more than {MaxClaims} claims");

            return new ClaimProofDocument
            {
                Scheme = ClaimCodec.RequireString(proof, "scheme"),
                KeyId = ClaimCodec.RequireString(proof, "keyId"),
                Algorithm = ClaimCodec.RequireString(proof, "algorithm"),
                Manifest = proof.TryGetProperty("manifest", out var manifest)
                    ? ReadEntry(manifest, "manifest")
                    : null,
                Claims = claims.EnumerateArray().Select(c => ReadEntry(c, "claim")).ToList()
            };
        }
    }

    private static ClaimProofEntry ReadEntry(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ClaimFormatException($"a proof {what} is not an object");

        ClaimCodec.RejectUnknownMembers(element, what, ["payload", "signature"]);

        return new ClaimProofEntry(
            ClaimCodec.RequireString(element, "payload"),
            ClaimCodec.RequireString(element, "signature"));
    }
}
