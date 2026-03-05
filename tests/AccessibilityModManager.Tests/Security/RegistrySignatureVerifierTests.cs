using System.Security.Cryptography;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Security;

public class RegistrySignatureVerifierTests
{
    private static (string publicPem, string privatePem) GenerateTestKeyPair()
    {
        using var rsa = RSA.Create(2048);
        var privatePem = rsa.ExportRSAPrivateKeyPem();
        var publicPem = rsa.ExportRSAPublicKeyPem();
        // Wrap in proper PEM headers for ImportFromPem
        var publicFull = $"-----BEGIN PUBLIC KEY-----\n{Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo(), Base64FormattingOptions.InsertLineBreaks)}\n-----END PUBLIC KEY-----";
        return (publicFull, privatePem);
    }

    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var (publicPem, privatePem) = GenerateTestKeyPair();
        var verifier = new RegistrySignatureVerifier(publicPem, TestLogger.Create());

        var json = """{"registryVersion": "1.0", "plugins": []}""";
        var signature = RegistrySignatureVerifier.Sign(json, privatePem);

        Assert.True(verifier.Verify(json, signature));
    }

    [Fact]
    public void Verify_TamperedContent_ReturnsFalse()
    {
        var (publicPem, privatePem) = GenerateTestKeyPair();
        var verifier = new RegistrySignatureVerifier(publicPem, TestLogger.Create());

        var json = """{"registryVersion": "1.0", "plugins": []}""";
        var signature = RegistrySignatureVerifier.Sign(json, privatePem);

        var tampered = """{"registryVersion": "1.0", "plugins": [{"id":"evil"}]}""";
        Assert.False(verifier.Verify(tampered, signature));
    }

    [Fact]
    public void Verify_WrongKey_ReturnsFalse()
    {
        var (_, privatePem) = GenerateTestKeyPair();
        var (otherPublicPem, _) = GenerateTestKeyPair();
        var verifier = new RegistrySignatureVerifier(otherPublicPem, TestLogger.Create());

        var json = """{"registryVersion": "1.0", "plugins": []}""";
        var signature = RegistrySignatureVerifier.Sign(json, privatePem);

        Assert.False(verifier.Verify(json, signature));
    }

    [Fact]
    public void Verify_InvalidBase64Signature_ReturnsFalse()
    {
        var (publicPem, _) = GenerateTestKeyPair();
        var verifier = new RegistrySignatureVerifier(publicPem, TestLogger.Create());

        Assert.False(verifier.Verify("some content", "not-valid-base64!!!"));
    }

    [Fact]
    public void Sign_ProducesVerifiableSignature()
    {
        var (publicPem, privatePem) = GenerateTestKeyPair();
        var verifier = new RegistrySignatureVerifier(publicPem, TestLogger.Create());

        var json = """{"test": true, "data": [1, 2, 3]}""";
        var sig1 = RegistrySignatureVerifier.Sign(json, privatePem);
        var sig2 = RegistrySignatureVerifier.Sign(json, privatePem);

        // Both signatures should verify (PSS is randomized, so sigs differ)
        Assert.True(verifier.Verify(json, sig1));
        Assert.True(verifier.Verify(json, sig2));
    }
}
