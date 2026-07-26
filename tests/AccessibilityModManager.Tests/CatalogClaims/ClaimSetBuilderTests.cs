using System.Security.Cryptography;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// Turning an index into claims: what gets re-signed, what keeps its existing claim, and what gets
/// withdrawn.
///
/// The most important test here is the one asserting that adding a patron-only release leaves every
/// public claim byte-identical. If public claims churned on a hidden publish, the timing of those
/// changes would disclose the hidden activity — defeating the point of hiding it.
/// </summary>
public sealed class ClaimSetBuilderTests : IDisposable
{
    private readonly RSA _key = ClaimTestKeys.Primary;
    private readonly ClaimTrustAnchor _anchor;
    private readonly ClaimSigner _signer;
    private const string Passphrase = "pp";

    public ClaimSetBuilderTests()
    {
        _anchor = new ClaimTrustAnchor
        {
            PluginId = "amethyst",
            RepoIndexUrl = "https://accessibilitymods.com/registry/plugins/amethyst/index.json",
            Scheme = ClaimTrustAnchor.SchemeV1,
            KeyId = "k1",
            Algorithm = ClaimTrustAnchor.AlgorithmRsaPssSha256,
            PublicKeyPem = _key.ExportSubjectPublicKeyInfoPem()
        };

        var pem = _key.ExportEncryptedPkcs8PrivateKeyPem(Passphrase,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
        _signer = new ClaimSigner(pem, Passphrase, _anchor);
    }

    public void Dispose()
    {
        _signer.Dispose();
    }

    private static ModRelease NewRelease(string version, string channel = "stable", PatreonGate? gate = null) => new()
    {
        GameId = "game1",
        PluginId = "amethyst",
        Version = version,
        Channel = channel,
        Sha256 = new string('a', 64),
        PackageUrl = gate is null ? new Uri("https://example.com/pkg.zip") : null,
        Patreon = gate
    };

    private static PluginRepoIndex NewIndex(params ModRelease[] releases)
    {
        var index = new PluginRepoIndex
        {
            PluginId = "amethyst",
            RepoVersion = "1",
            GeneratedAt = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc),
            Games =
            [
                new GameDefinition { GameId = "game1", DisplayName = "Game One", ModName = "Mod One" }
            ],
            ReleasesByGameId = new Dictionary<string, List<ModRelease>>(StringComparer.OrdinalIgnoreCase)
        };

        if (releases.Length > 0) index.ReleasesByGameId["game1"] = [.. releases];
        return index;
    }

    private static PatreonGate Gate(params string[] tiers) => new()
    {
        CampaignId = "c1",
        TierIds = [.. tiers]
    };

    private SignedClaim Find(IReadOnlyList<SignedClaim> claims, ClaimKind kind, string? version = null) =>
        claims.Single(c => c.Payload.Identity.Kind == kind &&
                           (version is null || c.Payload.Identity.Version == version));

    [Fact]
    public void A_first_build_signs_a_header_a_game_and_each_release()
    {
        var result = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0")), [], _signer);

        Assert.Equal(3, result.Claims.Count);
        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Unchanged);
        ClaimVerifier.ValidateSet(result.Claims);
    }

    [Fact]
    public void Rebuilding_an_unchanged_index_reuses_every_claim_byte_for_byte()
    {
        var index = NewIndex(NewRelease("1.0.0"));
        var first = ClaimSetBuilder.Build(index, [], _signer);

        var second = ClaimSetBuilder.Build(index, first.Claims, _signer);

        Assert.Equal(first.Claims.Count, second.Unchanged);
        Assert.Equal(0, second.Added + second.Updated + second.Revoked);
        foreach (var (a, b) in first.Claims.Zip(second.Claims))
        {
            Assert.Equal(a.PayloadBytes, b.PayloadBytes);
            Assert.Equal(a.Signature, b.Signature);
        }
    }

    [Fact]
    public void Adding_a_patron_only_release_leaves_every_public_claim_untouched()
    {
        // The leak this prevents: if public claims were re-signed whenever a hidden release was
        // added, an outside observer could infer patron-only activity from the churn alone.
        var before = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0")), [], _signer);

        var after = ClaimSetBuilder.Build(
            NewIndex(NewRelease("1.0.0"), NewRelease("1.1.0", gate: Gate("t3"))),
            before.Claims, _signer);

        var publicBefore = ClaimVerifier.ResolveVisible(before.Claims, null, null);
        var publicAfter = ClaimVerifier.ResolveVisible(after.Claims, null, null);

        Assert.Equal(publicBefore.Count, publicAfter.Count);
        foreach (var claim in publicBefore)
        {
            var match = publicAfter.Single(c =>
                c.Payload.Identity.ToStorageKey() == claim.Payload.Identity.ToStorageKey());
            Assert.Equal(claim.PayloadBytes, match.PayloadBytes);
            Assert.Equal(claim.Signature, match.Signature);
        }
    }

    [Fact]
    public void A_changed_release_gets_a_new_claim_at_a_higher_sequence()
    {
        var first = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0")), [], _signer);
        var originalSeq = Find(first.Claims, ClaimKind.Release).Payload.Seq;

        var changed = new ModRelease
        {
            GameId = "game1",
            PluginId = "amethyst",
            Version = "1.0.0",
            Channel = "stable",
            Sha256 = new string('a', 64),
            PackageUrl = new Uri("https://example.com/pkg.zip"),
            Notes = "now with notes"
        };
        var second = ClaimSetBuilder.Build(NewIndex(changed), first.Claims, _signer);

        Assert.Equal(1, second.Updated);
        Assert.True(Find(second.Claims, ClaimKind.Release).Payload.Seq > originalSeq);
        ClaimVerifier.ValidateSet(second.Claims);
    }

    [Fact]
    public void A_deleted_release_is_replaced_by_a_revocation_carrying_its_old_audience()
    {
        var first = ClaimSetBuilder.Build(
            NewIndex(NewRelease("1.0.0"), NewRelease("1.1.0", gate: Gate("t3"))), [], _signer);

        var second = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0")), first.Claims, _signer);

        Assert.Equal(1, second.Revoked);
        var revocation = second.Claims.Single(c => c.Payload.Kind == ClaimKind.Revocation);
        Assert.Equal("1.1.0", revocation.Payload.Identity.Version);

        // Aimed only at the tier that could see it: nobody else learns it ever existed.
        Assert.False(revocation.Payload.Audience.Public);
        Assert.Contains("t3", revocation.Payload.Audience.TierIds);
        Assert.DoesNotContain(ClaimVerifier.ResolveVisible(second.Claims, null, null),
            c => c.Payload.Identity.Version == "1.1.0");
        ClaimVerifier.ValidateSet(second.Claims);
    }

    [Fact]
    public void Narrowing_a_release_revokes_to_the_old_audience_and_republishes_to_the_new_one()
    {
        var first = ClaimSetBuilder.Build(
            NewIndex(NewRelease("1.0.0", gate: Gate("t1", "t3"))), [], _signer);

        var second = ClaimSetBuilder.Build(
            NewIndex(NewRelease("1.0.0", gate: Gate("t3"))), first.Claims, _signer);

        Assert.Equal(1, second.Revoked);
        ClaimVerifier.ValidateSet(second.Claims);

        // The demoted tier is told, and stops seeing the release.
        Assert.DoesNotContain(ClaimVerifier.ResolveVisible(second.Claims, "c1", ["t1"]),
            c => c.Payload.Identity.Kind == ClaimKind.Release);

        // The remaining tier still has it.
        Assert.Contains(ClaimVerifier.ResolveVisible(second.Claims, "c1", ["t3"]),
            c => c.Payload.Identity.Kind == ClaimKind.Release);
    }

    [Fact]
    public void A_narrowing_revocation_survives_later_publishes()
    {
        // The failure this pins happens during ordinary honest publishing, with no attacker at all.
        // Revocations used to be dropped the moment anything else was republished, so a patron who
        // was offline for the single publish carrying their revocation would never see it again and
        // would go on trusting a release they are no longer entitled to.
        var first = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0", gate: Gate("t1", "t3"))), [], _signer);
        var narrowed = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0", gate: Gate("t3"))), first.Claims, _signer);

        // Two further publishes that do not touch the release at all.
        var later = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0", gate: Gate("t3"))), narrowed.Claims, _signer);
        var laterStill = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0", gate: Gate("t3"))), later.Claims, _signer);

        ClaimVerifier.ValidateSet(laterStill.Claims);
        Assert.Contains(laterStill.Claims, c => c.Payload.Kind == ClaimKind.Revocation);

        // A tier-1 patron catching up only now still learns the release is gone for them.
        Assert.DoesNotContain(ClaimVerifier.ResolveVisible(laterStill.Claims, "c1", ["t1"]),
            c => c.Payload.Identity.Kind == ClaimKind.Release);

        // And tier 3 still has it.
        Assert.Contains(ClaimVerifier.ResolveVisible(laterStill.Claims, "c1", ["t3"]),
            c => c.Payload.Identity.Kind == ClaimKind.Release);
    }

    [Fact]
    public void Successive_narrowings_keep_a_revocation_for_each_audience_that_lost_access()
    {
        var v1 = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0", gate: Gate("t1", "t2", "t3"))), [], _signer);
        var v2 = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0", gate: Gate("t2", "t3"))), v1.Claims, _signer);
        var v3 = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0", gate: Gate("t3"))), v2.Claims, _signer);

        ClaimVerifier.ValidateSet(v3.Claims);

        // Both demoted tiers are still told; only tier 3 keeps the release.
        Assert.DoesNotContain(ClaimVerifier.ResolveVisible(v3.Claims, "c1", ["t1"]),
            c => c.Payload.Identity.Kind == ClaimKind.Release);
        Assert.DoesNotContain(ClaimVerifier.ResolveVisible(v3.Claims, "c1", ["t2"]),
            c => c.Payload.Identity.Kind == ClaimKind.Release);
        Assert.Contains(ClaimVerifier.ResolveVisible(v3.Claims, "c1", ["t3"]),
            c => c.Payload.Identity.Kind == ClaimKind.Release);
    }

    [Fact]
    public void Widening_a_release_needs_no_revocation()
    {
        // Nobody loses access, so there is nothing to tell anyone: the newer claim simply reaches
        // more people.
        var first = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0", gate: Gate("t3"))), [], _signer);

        var second = ClaimSetBuilder.Build(
            NewIndex(NewRelease("1.0.0", gate: Gate("t1", "t3"))), first.Claims, _signer);

        Assert.Equal(0, second.Revoked);
        Assert.Contains(ClaimVerifier.ResolveVisible(second.Claims, "c1", ["t1"]),
            c => c.Payload.Identity.Kind == ClaimKind.Release);
    }

    [Fact]
    public void Making_a_gated_release_public_needs_no_revocation()
    {
        var first = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0", gate: Gate("t3"))), [], _signer);

        var second = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0")), first.Claims, _signer);

        Assert.Equal(0, second.Revoked);
        Assert.Contains(ClaimVerifier.ResolveVisible(second.Claims, null, null),
            c => c.Payload.Identity.Kind == ClaimKind.Release);
    }

    [Fact]
    public void A_stable_and_a_beta_release_at_one_version_stay_separate_objects()
    {
        var result = ClaimSetBuilder.Build(
            NewIndex(NewRelease("1.0.0"), NewRelease("1.0.0", channel: "beta")), [], _signer);

        ClaimVerifier.ValidateSet(result.Claims);
        Assert.Equal(2, result.Claims.Count(c => c.Payload.Identity.Kind == ClaimKind.Release));
    }

    [Fact]
    public void A_withdrawn_release_version_is_never_re_asserted()
    {
        // This used to be allowed, and asserted that the resurrected claim simply took a higher
        // sequence. Higher sequences are not the problem: after the deletion the proof keeps the
        // revocation but not the withdrawn body or its package hash, and a server holding the only
        // copy of the ZIP can drop that too — so "this version is already published, with these
        // bytes" has nothing left to compare against, and the same version could be re-signed over
        // entirely different bytes. That is the one invariant a release has.
        var v1 = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0")), [], _signer);
        var deleted = ClaimSetBuilder.Build(NewIndex(), v1.Claims, _signer);

        var ex = Assert.Throws<ClaimFormatException>(() =>
            ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0")), deleted.Claims, _signer));
        Assert.Contains("withdrawn", ex.Message);
    }

    [Fact]
    public void Narrowing_is_not_mistaken_for_a_resurrection()
    {
        // Within one publish, narrowing emits a revocation and then a higher-sequence live claim for
        // the same identity — which looks exactly like a resurrection to anyone reading the finished
        // set. Only the builder can tell them apart, and it must not refuse this one.
        var first = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0", gate: Gate("t1", "t3"))), [], _signer);
        var narrowed = ClaimSetBuilder.Build(
            NewIndex(NewRelease("1.0.0", gate: Gate("t3"))), first.Claims, _signer);

        // And the publish after that, with the release unchanged, is still fine.
        var again = ClaimSetBuilder.Build(
            NewIndex(NewRelease("1.0.0", gate: Gate("t3"))), narrowed.Claims, _signer);

        Assert.Equal(1, narrowed.Revoked);
        ClaimVerifier.ValidateSet(again.Claims);
        Assert.Contains(ClaimVerifier.ResolveVisible(again.Claims, "c1", ["t3"]),
            c => c.Payload.Identity.Version == "1.0.0");
    }

    [Fact]
    public void A_proof_block_round_trips_and_verifies()
    {
        var built = ClaimSetBuilder.Build(
            NewIndex(NewRelease("1.0.0"), NewRelease("1.1.0", gate: Gate("t3"))), [], _signer);

        var document = ClaimProof.Write(_anchor, Manifest(built.Claims), built.Claims);
        var verified = ClaimProof.ReadVerified(document, _anchor, requireManifest: true);

        Assert.Equal(built.Claims.Count, verified.Claims.Count);
    }

    [Fact]
    public void A_proof_block_with_one_corrupted_claim_is_refused_entirely()
    {
        // All-or-nothing: "use the ones that verified" would let an attacker choose which parts of
        // a catalog a reader sees just by corrupting the rest.
        var built = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0")), [], _signer);
        var document = ClaimProof.Write(_anchor, Manifest(built.Claims), built.Claims);
        var broken = new ClaimProofDocument
        {
            Scheme = document.Scheme,
            KeyId = document.KeyId,
            Algorithm = document.Algorithm,
            Manifest = document.Manifest,
            Claims = [.. document.Claims.SkipLast(1),
                document.Claims[^1] with { SignatureBase64 = Convert.ToBase64String(new byte[512]) }]
        };

        Assert.Throws<ClaimFormatException>(() =>
            ClaimProof.ReadVerified(broken, _anchor, requireManifest: true));
    }

    /// <summary>
    /// A consumer never receives a manifest — the server strips it — so it must not treat the
    /// absence as tampering. Only the publisher, which is entitled to the whole set, requires one.
    /// </summary>
    [Fact]
    public void A_consumer_accepts_a_proof_whose_manifest_has_been_stripped()
    {
        var built = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0")), [], _signer);
        var document = ClaimProof.Write(_anchor, Manifest(built.Claims), built.Claims);
        var filtered = new ClaimProofDocument
        {
            Scheme = document.Scheme,
            KeyId = document.KeyId,
            Algorithm = document.Algorithm,
            Manifest = null,
            Claims = document.Claims
        };

        var verified = ClaimProof.ReadVerified(filtered, _anchor, requireManifest: false);

        Assert.Equal(built.Claims.Count, verified.Claims.Count);
        Assert.Null(verified.Manifest);
    }

    [Fact]
    public void A_proof_whose_key_id_disagrees_with_the_registry_is_refused()
    {
        var built = ClaimSetBuilder.Build(NewIndex(NewRelease("1.0.0")), [], _signer);
        var document = ClaimProof.Write(_anchor, Manifest(built.Claims), built.Claims);
        var relabelled = new ClaimProofDocument
        {
            Scheme = document.Scheme,
            KeyId = "some-other-key",
            Algorithm = document.Algorithm,
            Manifest = document.Manifest,
            Claims = document.Claims
        };

        Assert.Throws<ClaimFormatException>(() =>
            ClaimProof.ReadVerified(relabelled, _anchor, requireManifest: true));
    }

    private SignedManifest Manifest(IReadOnlyList<SignedClaim> claims) =>
        _signer.SignManifest(1, null, ClaimDigest.Compute(claims));
}
