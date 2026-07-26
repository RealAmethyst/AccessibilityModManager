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
public sealed class ClaimVerifier
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
        _publicKey.ImportFromPem(anchor.PublicKeyPem);

        if (_publicKey.KeySize < 3072)
            throw new ClaimFormatException($"claim signing key is only {_publicKey.KeySize} bits");
    }

    public string ExpectedTrustContext => _expectedTrustContext;

    /// <summary>
    /// Verifies one claim's signature and envelope. Throws <see cref="ClaimFormatException"/> on any
    /// failure — callers treat that as "refuse the whole index", never as "skip this one", because a
    /// bad claim in a published index means either an authoring fault or an attack, and neither is
    /// safely partially-applied.
    /// </summary>
    public SignedClaim Verify(ReadOnlySpan<byte> payloadBytes, ReadOnlySpan<byte> signature)
    {
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
    /// Applies the whole-set rules that a single claim cannot express: one header, no duplicate
    /// identities, and no two claims contending for the same object.
    ///
    /// A revocation is allowed to share an identity with the release it withdraws — that is its
    /// purpose — but only from a strictly higher sequence, so a stale revocation can never suppress
    /// a newer republication.
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

            var live = group.Where(c => c.Payload.Kind != ClaimKind.Revocation).ToList();
            var revocations = group.Where(c => c.Payload.Kind == ClaimKind.Revocation).ToList();

            if (live.Count > 1)
                throw new ClaimFormatException($"two claims describe the same object ({key})");

            // Two claims at the same sequence with different content is equivocation: the author
            // has signed two different truths under one version of one object.
            var seqs = group.Select(c => (c.Payload.Seq, Hash: ClaimCodec.ContentHash(c.PayloadBytes)))
                .GroupBy(x => x.Seq);
            foreach (var bySeq in seqs)
            {
                if (bySeq.Select(x => x.Hash).Distinct(StringComparer.Ordinal).Count() > 1)
                    throw new ClaimFormatException($"two different claims share sequence {bySeq.Key} for {key}");
            }

            foreach (var revocation in revocations)
            {
                foreach (var current in live)
                {
                    if (revocation.Payload.Seq <= current.Payload.Seq)
                        throw new ClaimFormatException(
                            $"a revocation for {key} is not newer than the claim it withdraws");
                }
            }
        }
    }
}
