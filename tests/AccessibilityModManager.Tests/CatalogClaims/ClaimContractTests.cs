using System.Security.Cryptography;
using System.Text;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// The acceptance contract for signed catalog claims. Most of these are adversarial: a valid
/// signature over bytes that should still be refused.
/// </summary>
public sealed class ClaimContractTests : IDisposable
{
    private readonly RSA _key = RSA.Create(3072);
    private readonly RSA _otherKey = RSA.Create(3072);
    private readonly ClaimTrustAnchor _anchor;
    private readonly ClaimSigner _signer;
    private const string Passphrase = "test-passphrase";

    public ClaimContractTests()
    {
        _anchor = NewAnchor(_key, "https://accessibilitymods.com/registry/plugins/amethyst/index.json");
        _signer = NewSigner(_key, _anchor);
    }

    public void Dispose()
    {
        _signer.Dispose();
        _key.Dispose();
        _otherKey.Dispose();
    }

    private static ClaimTrustAnchor NewAnchor(RSA key, string url, string pluginId = "amethyst", string keyId = "k1") => new()
    {
        PluginId = pluginId,
        RepoIndexUrl = url,
        Scheme = ClaimTrustAnchor.SchemeV1,
        KeyId = keyId,
        Algorithm = ClaimTrustAnchor.AlgorithmRsaPssSha256,
        PublicKeyPem = key.ExportSubjectPublicKeyInfoPem()
    };

    private static ClaimSigner NewSigner(RSA key, ClaimTrustAnchor anchor)
    {
        var pem = key.ExportEncryptedPkcs8PrivateKeyPem(Passphrase,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
        return new ClaimSigner(pem, Passphrase, anchor);
    }

    private static ClaimIdentity Release(string game = "digimonsurvive", string version = "1.0.0", string channel = "stable") =>
        new() { Kind = ClaimKind.Release, GameId = game, Channel = channel, Version = version };

    private SignedClaim SignRelease(long seq = 1, ClaimAudience? audience = null, string body = """{"version":"1.0.0"}""") =>
        _signer.Sign(ClaimKind.Release, Release(), seq, audience ?? ClaimAudience.Everyone, body);

    // ---- the basics ----

    [Fact]
    public void A_signed_claim_verifies()
    {
        var claim = SignRelease();
        var verifier = new ClaimVerifier(_anchor);

        var verified = verifier.Verify(claim.PayloadBytes, claim.Signature);

        Assert.Equal(ClaimKind.Release, verified.Payload.Kind);
        Assert.Equal("1.0.0", verified.Payload.Identity.Version);
    }

    [Fact]
    public void Tampering_with_the_body_breaks_the_signature()
    {
        var claim = SignRelease();
        var tampered = Encoding.UTF8.GetString(claim.PayloadBytes).Replace("1.0.0", "9.9.9");
        var verifier = new ClaimVerifier(_anchor);

        Assert.Throws<ClaimFormatException>(() =>
            verifier.Verify(Encoding.UTF8.GetBytes(tampered), claim.Signature));
    }

    [Fact]
    public void A_claim_signed_by_a_different_key_is_refused()
    {
        using var rogueSigner = NewSigner(_otherKey, NewAnchor(_otherKey, _anchor.RepoIndexUrl));
        var claim = rogueSigner.Sign(ClaimKind.Release, Release(), 1, ClaimAudience.Everyone, "{}");
        var verifier = new ClaimVerifier(_anchor);

        Assert.Throws<ClaimFormatException>(() => verifier.Verify(claim.PayloadBytes, claim.Signature));
    }

    // ---- the trust context: the reason re-pointing actually revokes ----

    [Fact]
    public void A_claim_from_the_old_index_address_is_refused_after_a_repoint()
    {
        // Exactly the attack the trust context exists to stop: the registry disowns a plugin's old
        // index by pointing it somewhere new, keeping the same key. Without the address in the
        // signed bytes, the old source's claims would still verify under the new anchor.
        var claim = SignRelease();

        var repointed = NewAnchor(_key, "https://accessibilitymods.com/registry/plugins/amethyst/index-v2.json");
        var verifier = new ClaimVerifier(repointed);

        var ex = Assert.Throws<ClaimFormatException>(() => verifier.Verify(claim.PayloadBytes, claim.Signature));
        Assert.Contains("lifted from another source", ex.Message);
    }

    [Fact]
    public void A_claim_from_another_plugin_is_refused()
    {
        var claim = SignRelease();
        var otherPlugin = NewAnchor(_key, _anchor.RepoIndexUrl, pluginId: "someoneelse");

        Assert.Throws<ClaimFormatException>(() =>
            new ClaimVerifier(otherPlugin).Verify(claim.PayloadBytes, claim.Signature));
    }

    [Fact]
    public void Changing_only_the_key_id_changes_the_trust_context()
    {
        var rotated = NewAnchor(_key, _anchor.RepoIndexUrl, keyId: "k2");
        Assert.NotEqual(ClaimTrustContext.Compute(_anchor), ClaimTrustContext.Compute(rotated));
    }

    [Fact]
    public void The_key_fingerprint_ignores_pem_formatting()
    {
        var pem = _key.ExportSubjectPublicKeyInfoPem();
        var reflowed = pem.Replace("\n", "\r\n");
        Assert.Equal(ClaimTrustContext.PublicKeyFingerprint(pem),
            ClaimTrustContext.PublicKeyFingerprint(reflowed));
    }

    // ---- strict reading: same bytes, one meaning ----

    [Fact]
    public void A_duplicate_envelope_member_is_refused()
    {
        // System.Text.Json silently keeps the last duplicate. A payload carrying two "seq" members
        // could be read one way here and another way elsewhere, with one valid signature covering
        // both readings.
        var claim = SignRelease(seq: 5);
        var text = Encoding.UTF8.GetString(claim.PayloadBytes);
        var doctored = text.Replace("\"seq\":5", "\"seq\":5,\"seq\":99");

        Assert.Throws<ClaimFormatException>(() => ClaimCodec.Parse(Encoding.UTF8.GetBytes(doctored)));
    }

    [Fact]
    public void An_unknown_envelope_member_is_refused()
    {
        var claim = SignRelease();
        var text = Encoding.UTF8.GetString(claim.PayloadBytes).Replace("{\"v\":1", "{\"surprise\":true,\"v\":1");

        Assert.Throws<ClaimFormatException>(() => ClaimCodec.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void A_payload_with_a_bom_is_refused()
    {
        var claim = SignRelease();
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. claim.PayloadBytes];

        Assert.Throws<ClaimFormatException>(() => ClaimCodec.Parse(withBom));
    }

    [Theory]
    [InlineData("\"kind\":\"Release\"", "\"kind\":\"release\"")]
    [InlineData("\"kind\":\"Release\"", "\"kind\":\"Nonsense\"")]
    public void Claim_kinds_are_matched_exactly(string from, string to)
    {
        var claim = SignRelease();
        var text = Encoding.UTF8.GetString(claim.PayloadBytes);
        var index = text.IndexOf(from, StringComparison.Ordinal);
        var doctored = string.Concat(text.AsSpan(0, index), to, text.AsSpan(index + from.Length));

        Assert.Throws<ClaimFormatException>(() => ClaimCodec.Parse(Encoding.UTF8.GetBytes(doctored)));
    }

    // ---- shape rules that survive a perfect signature ----

    [Fact]
    public void A_header_claim_may_not_be_gated()
    {
        var gated = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t1"] };
        var claim = _signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header }, 1, gated, "{}");

        Assert.Throws<ClaimFormatException>(() => new ClaimVerifier(_anchor).Verify(claim.PayloadBytes, claim.Signature));
    }

    [Fact]
    public void A_non_public_audience_must_name_a_campaign_and_a_tier()
    {
        // The signer round-trips through the strict reader, so an unpublishable claim fails here,
        // at authoring time, rather than on a user's machine after it has gone out.
        var broken = new ClaimAudience { Public = false, CampaignId = "", TierIds = [] };

        Assert.Throws<ClaimFormatException>(() => _signer.Sign(ClaimKind.Release, Release(), 1, broken, "{}"));
    }

    [Fact]
    public void A_signer_refuses_a_key_that_does_not_match_the_registry_entry()
    {
        // Otherwise the author would happily publish claims that no manager on earth could verify.
        var pem = _otherKey.ExportEncryptedPkcs8PrivateKeyPem(Passphrase,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));

        Assert.Throws<ClaimFormatException>(() => new ClaimSigner(pem, Passphrase, _anchor));
    }

    // ---- audience ----

    [Theory]
    [InlineData(true, null, new string[0], true)]      // public: everyone
    [InlineData(false, "c1", new[] { "t3" }, false)]   // tier-1 caller must not see a tier-3 claim
    public void Audience_admits_only_the_entitled(bool isPublic, string? campaign, string[] tiers, bool expected)
    {
        var audience = isPublic
            ? ClaimAudience.Everyone
            : new ClaimAudience { Public = false, CampaignId = campaign, TierIds = tiers };

        Assert.Equal(expected, audience.Admits("c1", ["t1"]));
    }

    [Fact]
    public void A_gated_claim_is_hidden_from_a_signed_out_caller()
    {
        var audience = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t1"] };
        Assert.False(audience.Admits(null, null));
    }

    [Fact]
    public void A_gated_claim_is_hidden_from_a_patron_of_a_different_campaign()
    {
        var audience = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t1"] };
        Assert.False(audience.Admits("c2", ["t1"]));
    }

    // ---- whole-set rules ----

    [Fact]
    public void Two_claims_for_the_same_object_are_refused()
    {
        var header = _signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header }, 1, ClaimAudience.Everyone, "{}");
        var a = SignRelease(seq: 1, body: """{"version":"1.0.0","a":1}""");
        var b = SignRelease(seq: 2, body: """{"version":"1.0.0","b":2}""");

        var ex = Assert.Throws<ClaimFormatException>(() => ClaimVerifier.ValidateSet([header, a, b]));
        Assert.Contains("same object", ex.Message);
    }

    [Fact]
    public void Two_claims_sharing_a_sequence_for_one_object_are_refused()
    {
        // Sharing a sequence makes "highest wins" ambiguous, and two different payloads under one
        // sequence is the author asserting two truths about one version of one thing.
        var header = _signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header }, 1, ClaimAudience.Everyone, "{}");
        var a = SignRelease(seq: 4, body: """{"packageUrl":"https://example.com/a.zip"}""");
        var revocationAtSameSeq = _signer.Sign(ClaimKind.Revocation, Release(), 4, ClaimAudience.Everyone, "{}");

        var ex = Assert.Throws<ClaimFormatException>(() => ClaimVerifier.ValidateSet([header, a, revocationAtSameSeq]));
        Assert.Contains("share a sequence", ex.Message);
    }

    [Fact]
    public void A_deleted_release_leaves_only_a_revocation_and_disappears()
    {
        var header = _signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header }, 1, ClaimAudience.Everyone, "{}");
        var revocation = _signer.Sign(ClaimKind.Revocation, Release(), 9, ClaimAudience.Everyone, "{}");

        ClaimVerifier.ValidateSet([header, revocation]);

        Assert.DoesNotContain(ClaimVerifier.ResolveVisible([header, revocation], null, null),
            c => c.Payload.Identity.Kind == ClaimKind.Release);
    }

    [Fact]
    public void Narrowing_a_release_to_a_higher_tier_hides_it_from_the_demoted_tier()
    {
        // The case this design exists for, and the one my first ordering rule made impossible to
        // express: a tier-1 release becomes tier-3-only. Tier 1 must stop seeing it AND be told so,
        // because they already hold a claim for it. Tier 3 must see the replacement.
        var header = _signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header }, 1, ClaimAudience.Everyone, "{}");
        var oldAudience = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t1", "t3"] };
        var narrowed = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t3"] };

        var revocationToOldAudience = _signer.Sign(ClaimKind.Revocation, Release(), 5, oldAudience, "{}");
        var replacementForTier3 = _signer.Sign(ClaimKind.Release, Release(), 6, narrowed,
            """{"version":"1.0.0","note":"patron build"}""");

        SignedClaim[] set = [header, revocationToOldAudience, replacementForTier3];
        ClaimVerifier.ValidateSet(set);

        // Tier 1 sees the revocation aimed at them and never the replacement, so it is gone.
        Assert.DoesNotContain(ClaimVerifier.ResolveVisible(set, "c1", ["t1"]),
            c => c.Payload.Identity.Kind == ClaimKind.Release);

        // Tier 3 sees both; the newer claim wins, so the release is still there.
        var forTier3 = ClaimVerifier.ResolveVisible(set, "c1", ["t3"])
            .Where(c => c.Payload.Identity.Kind == ClaimKind.Release);
        Assert.Equal(6, Assert.Single(forTier3).Payload.Seq);

        // Signed out: never learns it existed, before or after.
        Assert.DoesNotContain(ClaimVerifier.ResolveVisible(set, null, null),
            c => c.Payload.Identity.Kind == ClaimKind.Release);
    }

    [Fact]
    public void A_republished_object_beats_an_older_revocation()
    {
        var header = _signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header }, 1, ClaimAudience.Everyone, "{}");
        var oldRevocation = _signer.Sign(ClaimKind.Revocation, Release(), 2, ClaimAudience.Everyone, "{}");
        var republished = SignRelease(seq: 7, body: """{"version":"1.0.0","again":true}""");

        SignedClaim[] set = [header, oldRevocation, republished];
        ClaimVerifier.ValidateSet(set);

        Assert.Single(ClaimVerifier.ResolveVisible(set, null, null),
            c => c.Payload.Identity.Kind == ClaimKind.Release);
    }

    [Fact]
    public void Two_revocations_for_one_object_are_refused()
    {
        var header = _signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header }, 1, ClaimAudience.Everyone, "{}");
        var first = _signer.Sign(ClaimKind.Revocation, Release(), 8, ClaimAudience.Everyone, "{}");
        var second = _signer.Sign(ClaimKind.Revocation, Release(), 9, ClaimAudience.Everyone, "{}");

        Assert.Throws<ClaimFormatException>(() => ClaimVerifier.ValidateSet([header, first, second]));
    }

    [Fact]
    public void Exactly_one_header_is_required()
    {
        var release = SignRelease();
        Assert.Throws<ClaimFormatException>(() => ClaimVerifier.ValidateSet([release]));
    }

    [Fact]
    public void A_stable_and_a_beta_release_at_the_same_version_are_different_objects()
    {
        // The v1 draft keyed releases on game plus version, which collided these into one.
        var stable = new ClaimIdentity { Kind = ClaimKind.Release, GameId = "g", Channel = "stable", Version = "1.0.0" };
        var beta = new ClaimIdentity { Kind = ClaimKind.Release, GameId = "g", Channel = "beta", Version = "1.0.0" };

        Assert.False(stable.Matches(beta));
        Assert.NotEqual(stable.ToStorageKey(), beta.ToStorageKey());
    }

    [Fact]
    public void Storage_keys_cannot_be_crafted_to_collide()
    {
        // Length-prefixed parts: without them, a gameId containing the separator could impersonate
        // a different identity.
        var a = new ClaimIdentity { Kind = ClaimKind.Release, GameId = "a|b", Channel = "c", Version = "1" };
        var b = new ClaimIdentity { Kind = ClaimKind.Release, GameId = "a", Channel = "b|c", Version = "1" };

        Assert.NotEqual(a.ToStorageKey(), b.ToStorageKey());
    }

    // ---- determinism and content hashing ----

    [Fact]
    public void Re_serializing_an_unchanged_payload_gives_identical_bytes()
    {
        var claim = SignRelease();
        var reserialized = ClaimCodec.Serialize(claim.Payload);

        Assert.Equal(claim.PayloadBytes, reserialized);
    }

    [Fact]
    public void Content_hashing_ignores_the_randomised_signature()
    {
        // RSA-PSS is randomised, so re-signing the same payload gives different signature bytes.
        // Comparing signatures would make legitimate re-signing look like equivocation.
        var first = SignRelease(seq: 3);
        var second = SignRelease(seq: 3);

        Assert.NotEqual(first.Signature, second.Signature);
        Assert.Equal(ClaimCodec.ContentHash(first.PayloadBytes), ClaimCodec.ContentHash(second.PayloadBytes));
    }

    [Fact]
    public void An_oversized_payload_is_refused_before_parsing()
    {
        var huge = Encoding.UTF8.GetBytes("{\"v\":1,\"pad\":\"" + new string('x', ClaimCodec.MaxPayloadBytes) + "\"}");
        Assert.Throws<ClaimFormatException>(() => ClaimCodec.Parse(huge));
    }
}
