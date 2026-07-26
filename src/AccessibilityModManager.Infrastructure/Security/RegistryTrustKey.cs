namespace AccessibilityModManager.Infrastructure.Security;

/// <summary>
/// The one copy of the registry's public key.
///
/// This key is the trust anchor for the entire catalog: the manager refuses any registry it does not
/// verify, and everything downstream — which plugins exist, where their indexes live, which key
/// signs their claims — is only trustworthy because this key vouched for it.
///
/// It previously existed as separate copies in the manager and the AuthorTool. Two copies of a trust
/// anchor is a bug waiting to happen: they can drift, and the failure mode is the AuthorTool
/// cheerfully signing and publishing content the manager refuses. One copy, with its fingerprint
/// pinned by a test.
///
/// The matching private key is offline on the maintainer's machine and appears in no repository.
/// </summary>
public static class RegistryTrustKey
{
    /// <summary>
    /// SHA-256 over the key's DER SubjectPublicKeyInfo. Pinned by a test so an accidental edit to
    /// the PEM below — a stray character, a re-wrap, a paste of the wrong key — fails loudly here
    /// rather than in the field.
    /// </summary>
    public const string ExpectedFingerprint = "510d693a4a588b1c4a345675fa49d69e63fc7e0a17ec08b94bc771a246076c95";

    public const string PublicKeyPem =
        """
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
}
