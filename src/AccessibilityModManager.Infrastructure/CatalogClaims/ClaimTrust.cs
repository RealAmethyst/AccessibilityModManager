using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// One key size, everywhere: creating, importing, signing and verifying.
///
/// Pinned rather than "at least 4096" so signature length and verification cost are the same for
/// every consumer forever. The gap this closes was the other direction, though — the contract said
/// 4096 while the verifier accepted 3072 and the signing side checked nothing at all, so a weak key
/// would have been discovered only when a manager refused the first publication made with it.
/// </summary>
public static class ClaimKeyPolicy
{
    public const int KeySizeBits = 4096;

    public static void Require(RSA key)
    {
        if (key.KeySize != KeySizeBits)
            throw new ClaimFormatException(
                $"claim signing keys must be RSA {KeySizeBits}; this one is {key.KeySize}");
    }
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
        //
        // The length is the member's UTF-8 BYTE count, not .NET's UTF-16 char count. That
        // distinction is invisible until someone outside .NET implements this — the server is a
        // second implementation of the same contract — and it diverges for any character outside
        // the basic multilingual plane, where .NET counts two and the bytes number four.
        using var buffer = new MemoryStream();
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
            var bytes = Encoding.UTF8.GetBytes(part);
            buffer.Write(Encoding.UTF8.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture)));
            buffer.WriteByte((byte)':');
            buffer.Write(bytes);
            buffer.WriteByte((byte)'\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
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
