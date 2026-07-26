using System.Security.Cryptography;
using AccessibilityModManager.Infrastructure.Security;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// Pins the registry's public key.
///
/// This key decides which catalog the manager will accept. It used to exist as separate copies in
/// the manager and the AuthorTool, where they could drift apart — and the failure mode was the
/// AuthorTool happily signing and publishing content every manager would refuse. There is one copy
/// now, and this test makes an accidental edit to it — a stray character, a re-wrap, the wrong key
/// pasted in — fail here instead of in the field.
/// </summary>
public sealed class RegistryTrustKeyTests
{
    [Fact]
    public void The_embedded_key_is_the_expected_one()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(RegistryTrustKey.PublicKeyPem);

        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));

        Assert.Equal(RegistryTrustKey.ExpectedFingerprint, fingerprint);
    }

    [Fact]
    public void The_embedded_key_is_a_4096_bit_rsa_key()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(RegistryTrustKey.PublicKeyPem);

        Assert.Equal(4096, rsa.KeySize);
    }

    [Fact]
    public void The_embedded_key_carries_no_private_material()
    {
        // A private key pasted here would leak the ecosystem's root of trust into a public repo.
        Assert.DoesNotContain("PRIVATE", RegistryTrustKey.PublicKeyPem, StringComparison.Ordinal);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(RegistryTrustKey.PublicKeyPem);
        Assert.Throws<CryptographicException>(() => rsa.ExportRSAPrivateKey());
    }
}
