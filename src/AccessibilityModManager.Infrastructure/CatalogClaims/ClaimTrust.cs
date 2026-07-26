using System.Security.Cryptography;
using System.Text;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// The trust anchor a claim is signed under, taken from the signed registry entry for a plugin.
/// Everything here comes from the registry — never from the index being verified, which is the
/// thing under suspicion.
/// </summary>
public sealed record ClaimTrustAnchor
{
    public required string PluginId { get; init; }

    /// <summary>The exact repoIndexUrl from the signed registry, compared ordinally.</summary>
    public required string RepoIndexUrl { get; init; }

    public required string Scheme { get; init; }
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public required string PublicKeyPem { get; init; }

    public const string SchemeV1 = "signed-claims-v1";
    public const string AlgorithmRsaPssSha256 = "rsa-pss-sha256";
}

/// <summary>
/// Computes the value bound into every claim, tying it to one plugin, one exact index address, and
/// one key.
///
/// Why the URL is in here: re-pointing a plugin's repoIndexUrl is how a compromised or abandoned
/// index gets disowned. Without the URL in the signed bytes, a hostile server could take a claim
/// that was validly signed for the old address and present it under the new one — the signature
/// still verifies, the key is still the right key, and the revocation achieves nothing. Including
/// it means re-pointing requires re-signing the current claims, which is fine: re-pointing is an
/// exceptional registry operation, not a routine one.
/// </summary>
public static class ClaimTrustContext
{
    public static string Compute(ClaimTrustAnchor anchor)
    {
        // Length-prefixed parts, so no combination of values can be rearranged into a different
        // tuple that hashes the same.
        var sb = new StringBuilder();
        foreach (var part in new[]
                 {
                     anchor.PluginId,
                     anchor.RepoIndexUrl,
                     anchor.Scheme,
                     anchor.KeyId,
                     anchor.Algorithm,
                     PublicKeyFingerprint(anchor.PublicKeyPem)
                 })
        {
            sb.Append(part.Length).Append(':').Append(part).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// SHA-256 over the key's DER SubjectPublicKeyInfo — the key itself, not its PEM text, so
    /// reformatting or re-wrapping the PEM does not change the fingerprint.
    /// </summary>
    public static string PublicKeyFingerprint(string publicKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
    }
}
