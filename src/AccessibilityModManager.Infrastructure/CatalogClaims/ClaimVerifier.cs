using System.Security.Cryptography;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// Verifies claims against the trust anchor from the signed registry.
///
/// A valid signature is necessary but nowhere near sufficient. Everything below is a case where an
/// authentically signed claim would still be wrong to accept — a claim lifted from another plugin,
/// another index address, or another key; a claim whose declared kind disagrees with its identity;
/// two claims contending for one object. Each of those is a trust violation, not a warning.
/// </summary>
public sealed class ClaimVerifier : IDisposable
{
    private readonly ClaimTrustAnchor _anchor;
    private readonly string _expectedTrustContext;
    private readonly RSA _publicKey;

    public ClaimVerifier(ClaimTrustAnchor anchor)
    {
        if (!string.Equals(anchor.Scheme, ClaimTrustAnchor.SchemeV1, StringComparison.Ordinal))
            throw new ClaimFormatException($"unsupported claim scheme '{anchor.Scheme}'");
        if (!string.Equals(anchor.Algorithm, ClaimTrustAnchor.AlgorithmRsaPssSha256, StringComparison.Ordinal))
            throw new ClaimFormatException($"unsupported claim algorithm '{anchor.Algorithm}'");

        _anchor = anchor;
        _expectedTrustContext = ClaimTrustContext.Compute(anchor);
        _publicKey = RSA.Create();
        try
        {
            _publicKey.ImportFromPem(anchor.PublicKeyPem);
            ClaimKeyPolicy.Require(_publicKey);
        }
        catch
        {
            _publicKey.Dispose();
            throw;
        }
    }

    public string ExpectedTrustContext => _expectedTrustContext;

    public void Dispose() => _publicKey.Dispose();

    /// <summary>
    /// Verifies one claim's signature and envelope. Throws <see cref="ClaimFormatException"/> on any
    /// failure — callers treat that as "refuse the whole index", never as "skip this one", because a
    /// bad claim in a published index means either an authoring fault or an attack, and neither is
    /// safely partially-applied.
    /// </summary>
    public SignedClaim Verify(ReadOnlySpan<byte> payloadBytes, ReadOnlySpan<byte> signature)
    {
        RequireSignatureLength(signature);
        var payload = ClaimCodec.Parse(payloadBytes);

        if (!string.Equals(payload.TrustContext, _expectedTrustContext, StringComparison.Ordinal))
        {
            throw new ClaimFormatException(
                "claim is not bound to this plugin, index address and key — it may have been " +
                "lifted from another source or from before a re-point");
        }

        if (!_publicKey.VerifyData(ClaimCodec.BytesToSign(payloadBytes), signature,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            throw new ClaimFormatException("claim signature does not verify");
        }

        ValidateShape(payload);

        return new SignedClaim
        {
            PayloadBytes = payloadBytes.ToArray(),
            Signature = signature.ToArray(),
            Payload = payload
        };
    }

    /// <summary>
    /// Verifies the publisher's commitment to a whole claim set. Same anchor, same trust context,
    /// different signing domain — so a claim can never be presented as a manifest.
    ///
    /// This does not check the digest against anything; that is the caller's job, because it needs
    /// the claims the manifest is supposed to cover.
    /// </summary>
    public SignedManifest VerifyManifest(ReadOnlySpan<byte> payloadBytes, ReadOnlySpan<byte> signature)
    {
        RequireSignatureLength(signature);
        var manifest = ManifestCodec.Parse(payloadBytes);

        if (!string.Equals(manifest.TrustContext, _expectedTrustContext, StringComparison.Ordinal))
        {
            throw new ClaimFormatException(
                "the proof manifest is not bound to this plugin, index address and key — it may " +
                "have been lifted from another source or from before a re-point");
        }

        if (!_publicKey.VerifyData(ManifestCodec.BytesToSign(payloadBytes), signature,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            throw new ClaimFormatException("the proof manifest's signature does not verify");
        }

        return new SignedManifest
        {
            PayloadBytes = payloadBytes.ToArray(),
            Signature = signature.ToArray(),
            Manifest = manifest
        };
    }

    /// <summary>
    /// An RSA-PSS signature is exactly the modulus length. Checking it here means an absurdly long
    /// "signature" is refused before it is decoded and handed to the crypto layer, rather than
    /// being carried around as an unbounded string inside an otherwise-bounded proof.
    /// </summary>
    private void RequireSignatureLength(ReadOnlySpan<byte> signature)
    {
        var expected = _publicKey.KeySize / 8;
        if (signature.Length != expected)
            throw new ClaimFormatException(
                $"signature is {signature.Length} bytes; this key's signatures are {expected}");
    }

    /// <summary>
    /// Structural rules that hold regardless of who signed: which identity fields each kind must
    /// and must not carry. A release claim with no version, or a header claim naming a game, is
    /// malformed even with a perfect signature.
    /// </summary>
    private static void ValidateShape(ClaimPayload payload)
    {
        var id = payload.Identity;

        // Shape follows the OBJECT kind, so a revocation is held to the same identity rules as the
        // thing it withdraws.
        switch (id.Kind)
        {
            case ClaimKind.Header:
                if (id.GameId is not null || id.Channel is not null || id.Version is not null)
                    throw new ClaimFormatException("a header claim must not name a game, channel or version");
                if (!payload.Audience.Public)
                    throw new ClaimFormatException("the header claim must be public");
                break;

            case ClaimKind.Game:
                if (string.IsNullOrEmpty(id.GameId))
                    throw new ClaimFormatException("a game claim must name a game");
                if (id.Channel is not null || id.Version is not null)
                    throw new ClaimFormatException("a game claim must not name a channel or version");
                break;

            case ClaimKind.Release:
                if (string.IsNullOrEmpty(id.GameId))
                    throw new ClaimFormatException("a release claim must name a game");
                if (string.IsNullOrEmpty(id.Version) || string.IsNullOrEmpty(id.Channel))
                    throw new ClaimFormatException("a release claim must name a version and a channel");
                break;
        }
    }

    /// <summary>
    /// Applies the whole-set rules that a single claim cannot express: exactly one header, at most
    /// one live claim and one revocation per object, and no two claims sharing a sequence.
    ///
    /// State is resolved by HIGHEST SEQUENCE WINS, which is what lets one mechanism cover both
    /// things a revocation has to do:
    ///
    /// - A deletion leaves only a revocation for that identity, carrying the audience that could
    ///   previously see the object. Anyone else never learns it existed.
    /// - Narrowing a release's tier leaves a revocation at the OLD audience and a new claim at the
    ///   NEW one, with the new claim's sequence higher. The demoted tier sees only the revocation
    ///   and stops trusting the claim it already holds; the remaining tier sees both and the newer
    ///   claim wins.
    ///
    /// An earlier version of this required a revocation to be newer than any claim it shared an
    /// identity with. That reads as obviously correct and makes narrowing impossible to express —
    /// the exact case the design exists to handle.
    /// </summary>
    public static void ValidateSet(IReadOnlyList<SignedClaim> claims)
    {
        var headers = claims.Count(c => c.Payload.Kind == ClaimKind.Header);
        if (headers != 1)
            throw new ClaimFormatException($"expected exactly one header claim, found {headers}");

        var byIdentity = new Dictionary<string, List<SignedClaim>>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            var key = claim.Payload.Identity.ToStorageKey();
            if (!byIdentity.TryGetValue(key, out var list))
                byIdentity[key] = list = [];
            list.Add(claim);
        }

        foreach (var (key, group) in byIdentity)
        {
            if (group.Count == 1) continue;

            var live = group.Count(c => c.Payload.Kind != ClaimKind.Revocation);
            if (live > 1)
                throw new ClaimFormatException($"two claims describe the same object ({key})");

            // Several revocations for one object is legitimate and necessary: each narrowing leaves
            // one aimed at the audience that lost access, and they are all retained so a patron who
            // was offline for that publish still learns about it later. Requiring at most one made
            // successive narrowings impossible to represent.

            // Distinct sequences within one object. Sharing one would make "highest wins"
            // ambiguous, and two different payloads under one sequence is equivocation — the author
            // asserting two truths about one version of one thing.
            if (group.Select(c => c.Payload.Seq).Distinct().Count() != group.Count)
                throw new ClaimFormatException($"two claims share a sequence for {key}");
        }
    }

    /// <summary>
    /// Resolves what a caller should actually see: for each object, the highest-sequence claim that
    /// caller's entitlements admit, dropping objects whose newest visible claim is a revocation.
    ///
    /// Filtering happens BEFORE resolution, which is the whole point. A tier-1 caller who cannot see
    /// a tier-3 replacement resolves to the revocation that was aimed at them and correctly treats
    /// the object as gone, rather than resolving to a claim they are not entitled to know about.
    /// </summary>
    public static IReadOnlyList<SignedClaim> ResolveVisible(
        IReadOnlyList<SignedClaim> claims,
        string? campaignId,
        IReadOnlyCollection<string>? entitledTierIds)
    {
        var current = new Dictionary<string, SignedClaim>(StringComparer.Ordinal);

        foreach (var claim in claims)
        {
            if (!claim.Payload.Audience.Admits(campaignId, entitledTierIds)) continue;

            var key = claim.Payload.Identity.ToStorageKey();
            if (!current.TryGetValue(key, out var existing) || claim.Payload.Seq > existing.Payload.Seq)
                current[key] = claim;
        }

        return current.Values
            .Where(c => c.Payload.Kind != ClaimKind.Revocation)
            .ToList();
    }
}
