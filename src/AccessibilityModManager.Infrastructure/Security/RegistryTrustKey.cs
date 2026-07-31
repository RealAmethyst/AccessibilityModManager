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

    /// <summary>
    /// The oldest registry this build will accept, whatever any local state says.
    ///
    /// <para><b>Why a shipped constant and not just the high-water marker.</b> The marker refuses a
    /// registry older than the newest THIS MACHINE has already seen, which is exactly nothing on a
    /// machine that has never fetched one. So a fresh install — the state every user passes through,
    /// and the state left by clearing app data or moving to a new PC — will accept any validly
    /// signed registry it is served, including one published before <c>indexTrust</c> existed. That
    /// registry names no signing key, which sends the manager down the unsigned path and discards
    /// every guarantee signing was introduced to provide. The signature is genuine, so nothing else
    /// in the chain objects.</para>
    ///
    /// <para>A constant compiled into the binary cannot be absent, corrupted or rolled back, so it
    /// holds the line where local state has nothing to say. It is a FLOOR, never an equality: a
    /// newer registry must always be accepted, or publishing one would strand every older manager.
    /// Raising it is safe once a version is live and settled; the cost of leaving it behind is only
    /// that the protection reaches less far forward.</para>
    ///
    /// <para>3 is the version that carries the live <c>indexTrust</c> anchor for <c>amethyst</c>,
    /// published 2026-07-30. Anything below it predates signing.</para>
    /// </summary>
    public const string MinimumRegistryVersion = "3";

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
