using System.Security.Cryptography;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// Signs claims with a plugin's own key. Author-side only — the server never holds a private key
/// and cannot construct one of these.
/// </summary>
public sealed class ClaimSigner : IDisposable
{
    private readonly RSA _privateKey;
    private readonly ClaimTrustAnchor _anchor;
    private readonly string _trustContext;

    /// <summary>
    /// Builds a signer from an encrypted PKCS#8 private key and the anchor the resulting claims
    /// must be bound to. The anchor's public key is checked against the private one, so a
    /// mismatched pair fails here rather than producing claims nothing can verify.
    /// </summary>
    public ClaimSigner(string encryptedPrivateKeyPem, ReadOnlySpan<char> passphrase, ClaimTrustAnchor anchor)
    {
        _privateKey = RSA.Create();
        _privateKey.ImportFromEncryptedPem(encryptedPrivateKeyPem, passphrase);
        _anchor = anchor;

        var declared = ClaimTrustContext.PublicKeyFingerprint(anchor.PublicKeyPem);
        var actual = Convert.ToHexStringLower(SHA256.HashData(_privateKey.ExportSubjectPublicKeyInfo()));
        if (!string.Equals(declared, actual, StringComparison.Ordinal))
        {
            _privateKey.Dispose();
            throw new ClaimFormatException(
                "the private key does not match the public key in the registry entry — signing with it " +
                "would produce claims no manager could verify");
        }

        _trustContext = ClaimTrustContext.Compute(anchor);
    }

    public string TrustContext => _trustContext;

    public SignedClaim Sign(ClaimKind kind, ClaimIdentity identity, long seq, ClaimAudience audience, string bodyJson)
    {
        var payload = new ClaimPayload
        {
            V = ClaimCodec.SupportedVersion,
            TrustContext = _trustContext,
            Kind = kind,
            Identity = identity,
            Seq = seq,
            Audience = audience,
            BodyJson = bodyJson
        };

        var payloadBytes = ClaimCodec.Serialize(payload);
        var signature = _privateKey.SignData(ClaimCodec.BytesToSign(payloadBytes),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        return new SignedClaim
        {
            PayloadBytes = payloadBytes,
            Signature = signature,
            // Round-trip through the strict reader rather than reusing the object we just built.
            // If anything we can emit is something the verifier would refuse, that must fail here,
            // at authoring time, and not on a user's machine after publication.
            Payload = ClaimCodec.Parse(payloadBytes)
        };
    }

    public void Dispose() => _privateKey.Dispose();
}
