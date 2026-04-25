using System.Security.Cryptography;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Security;

/// <summary>
/// Smoke test for the registry public key as embedded in App.xaml.cs. If the C# raw-string
/// indent stripping ever silently corrupts the PEM, this catches it. Keep this PEM in sync
/// with App.xaml.cs:GetRegistryPublicKey().
/// </summary>
public class EmbeddedPublicKeyTests
{
    private const string EmbeddedPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAvPTABidJcBN5V4kWommo
        arlzq5pKHXNXrkFX8HUHjwK+SBiqUzWuZyZOEw5vAv+X6oa3T3g8iF+h+Hu+NHQ+
        dw/cLy+Vmmlaz3YgBJRKMrEQspySI8cM3+4ZU54YzUCpPNSwi37P5JmC1lJEeRMJ
        KxXz3Cwots1Zr2jZOn0l39+/9Vu8lQ84mVFd4wWIAfpBvc8FNVfw2p+qsOX3xZCa
        vhV2Q7YGXgf+N09OfCSB74pU/qBYXDZ+FP2w+2ywCMWOOKmX0t9C4EusZ28QTabj
        XkzrPyB5lhpMigl9HhvYjmtCjqPR7uzohIpRNLir02po3FRMAuW4sSxp0rkxu6pX
        huQsHbfgR12aX1/Cv6fR9ez3EH8/ODXrJDANoL8NDuJ0hkfsXPSEn8tv7d7ZV/S5
        4HpK6I/uwGMhY+YrkOCtj/FKDM+JaxD1PRqLZU/4uGiOG+Z2z4Cv7oA/ZnCW4EBn
        DI+9Ibfu1Ox+PtrLTr5hxUqiqsJfIYYLaWPJSAgzK4TkzumHp64/2kVmS0bb3xJ+
        +tytJv054d2PwLgaLLioD0CnRPQhXK1JPKmqUVP3aCIWJIa/1vchqgIXcXUyaQzG
        ghi2SW1UOrX1iNzJiO6CCkO0ad4V7FnvbMS2uxFpwYQ97/Mwh/iF0BhblcFM5niO
        OrUeiLZWMTgg4PWc06FFTyECAwEAAQ==
        -----END PUBLIC KEY-----
        """;

    [Fact]
    public void EmbeddedKey_ParsesAsValidRsaPublicKey()
    {
        // RSA.ImportFromPem throws if the key is malformed.
        using var rsa = RSA.Create();
        rsa.ImportFromPem(EmbeddedPublicKey);

        // 4096-bit was generated; smoke-check the size.
        Assert.Equal(4096, rsa.KeySize);
    }

    [Fact]
    public void EmbeddedKey_CanConstructVerifier()
    {
        // Constructor calls ImportFromPem internally.
        var verifier = new RegistrySignatureVerifier(EmbeddedPublicKey, TestLogger.Create());

        // A clearly-bad signature should fail-fast (this exercises the verify code path).
        Assert.False(verifier.Verify("any content", "AAAA"));
    }
}
