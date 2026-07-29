using System.Security.Cryptography;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// Claims that are perfectly signed and still wrong.
///
/// Every one of these carries a valid signature from the right key, in the right trust context,
/// with a well-formed envelope. What makes each unacceptable is the content — the author's own key
/// asserting something the author's own rules forbid. Before these checks existed, all of them
/// verified.
/// </summary>
public sealed class ClaimAcceptanceTests : IDisposable
{
    private readonly ClaimTrustAnchor _anchor;
    private readonly ClaimSigner _signer;
    private const string Passphrase = "pp";

    public ClaimAcceptanceTests()
    {
        var key = ClaimTestKeys.Primary;
        _anchor = new ClaimTrustAnchor
        {
            PluginId = "amethyst",
            RepoIndexUrl = "https://accessibilitymods.com/registry/plugins/amethyst/index.json",
            Scheme = ClaimTrustAnchor.SchemeV1,
            KeyId = "k1",
            Algorithm = ClaimTrustAnchor.AlgorithmRsaPssSha256,
            PublicKeyPem = key.ExportSubjectPublicKeyInfoPem()
        };

        _signer = new ClaimSigner(
            key.ExportEncryptedPkcs8PrivateKeyPem(Passphrase,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)),
            Passphrase, _anchor);
    }

    public void Dispose() => _signer.Dispose();

    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private SignedClaim Header() => _signer.Sign(
        ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header }, 1, ClaimAudience.Everyone,
        """{"pluginId":"amethyst","repoVersion":"1"}""");

    private SignedClaim Game(string gameId = "game1") => _signer.Sign(
        ClaimKind.Game, new ClaimIdentity { Kind = ClaimKind.Game, GameId = gameId }, 1, ClaimAudience.Everyone,
        $$"""{"gameId":"{{gameId}}","displayName":"Game One","modName":"Mod"}""");

    private SignedClaim Release(string body, ClaimAudience? audience = null,
        string gameId = "game1", string version = "1.0.0", string channel = "stable") =>
        _signer.Sign(ClaimKind.Release,
            new ClaimIdentity { Kind = ClaimKind.Release, GameId = gameId, Channel = channel, Version = version },
            1, audience ?? ClaimAudience.Everyone, body);

    private static string PublicRelease(
        string gameId = "game1", string version = "1.0.0", string channel = "stable",
        string url = "https://example.com/p.zip", string sha = Sha) =>
        $$"""
        {"gameId":"{{gameId}}","pluginId":"amethyst","version":"{{version}}","channel":"{{channel}}","packageUrl":"{{url}}","sha256":"{{sha}}"}
        """;

    /// <summary>
    /// Runs a claim set through the door a real consumer comes in by, rather than calling the
    /// acceptance rules directly.
    ///
    /// <para>Two things fall out of that. The signatures on these claims are genuinely checked, so
    /// every test below proves its rule is reachable in production instead of only through a test
    /// seam. And nothing outside the verification code needs to be able to reach the projection at
    /// all — a claim carries its signature but no evidence that anyone checked it, so a caller
    /// holding a bare list of them must not be able to turn one into a catalog.</para>
    /// </summary>
    private void Accept(params SignedClaim[] claims) =>
        ClaimProof.ReadVerified(new ClaimProofDocument
        {
            Scheme = _anchor.Scheme,
            KeyId = _anchor.KeyId,
            Algorithm = _anchor.Algorithm,
            Claims = [.. claims.Select(c => new ClaimProofEntry(
                Convert.ToBase64String(c.PayloadBytes), Convert.ToBase64String(c.Signature)))]
        }, _anchor, requireManifest: false);

    [Fact]
    public void A_coherent_set_is_accepted()
    {
        Accept(Header(), Game(), Release(PublicRelease()));
    }

    [Fact]
    public void A_release_body_that_disagrees_with_its_envelope_is_refused()
    {
        // The envelope files it as 1.0.0; the body says 2.0.0. One of those is what a manager
        // records and the other is what it installs.
        var ex = Assert.Throws<ClaimFormatException>(() =>
            Accept(Header(), Game(), Release(PublicRelease(version: "2.0.0"))));

        Assert.Contains("carries a body for", ex.Message);
    }

    [Fact]
    public void A_release_body_naming_another_game_is_refused()
    {
        Assert.Throws<ClaimFormatException>(() =>
            Accept(Header(), Game(), Release(PublicRelease(gameId: "game2"))));
    }

    [Fact]
    public void A_release_body_naming_another_plugin_is_refused()
    {
        var body = PublicRelease().Replace("\"pluginId\":\"amethyst\"", "\"pluginId\":\"someoneelse\"");

        Assert.Throws<ClaimFormatException>(() => Accept(Header(), Game(), Release(body)));
    }

    [Fact]
    public void A_header_naming_another_plugin_is_refused()
    {
        var header = _signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header }, 1,
            ClaimAudience.Everyone, """{"pluginId":"someoneelse","repoVersion":"1"}""");

        Assert.Throws<ClaimFormatException>(() => Accept(header, Game(), Release(PublicRelease())));
    }

    [Fact]
    public void A_release_signed_to_a_tier_but_carrying_no_gate_is_refused()
    {
        // It would be disclosed only to tier 3, and then be freely downloadable by anyone who
        // learned the URL — a gate in the catalog and no gate on the file.
        var audience = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t3"] };

        var ex = Assert.Throws<ClaimFormatException>(() =>
            Accept(Header(), Game(), Release(PublicRelease(), audience)));

        Assert.Contains("carries no Patreon gate", ex.Message);
    }

    [Fact]
    public void A_release_signed_to_different_tiers_than_it_is_gated_on_is_refused()
    {
        var audience = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t3"] };
        var body = """
        {"gameId":"game1","pluginId":"amethyst","version":"1.0.0","channel":"stable","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","patreon":{"campaignId":"c1","tierIds":["t1"],"serverUrl":"https://example.com/g.zip"}}
        """;

        var ex = Assert.Throws<ClaimFormatException>(() => Accept(Header(), Game(), Release(body, audience)));

        Assert.Contains("different tiers", ex.Message);
    }

    [Fact]
    public void A_public_release_carrying_a_gate_is_refused()
    {
        var body = """
        {"gameId":"game1","pluginId":"amethyst","version":"1.0.0","channel":"stable","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","patreon":{"campaignId":"c1","tierIds":["t1"],"serverUrl":"https://example.com/g.zip"}}
        """;

        Assert.Throws<ClaimFormatException>(() => Accept(Header(), Game(), Release(body)));
    }

    [Fact]
    public void A_signed_http_package_url_is_refused()
    {
        // HTTPS is a hard rule for every plugin URL. A signature over an http one is the author's
        // key vouching for a download anyone on the path can replace.
        var ex = Assert.Throws<ClaimFormatException>(() =>
            Accept(Header(), Game(), Release(PublicRelease(url: "http://example.com/p.zip"))));

        Assert.Contains("no manager would accept", ex.Message);
    }

    [Fact]
    public void A_signed_release_with_a_malformed_hash_is_refused()
    {
        var ex = Assert.Throws<ClaimFormatException>(() =>
            Accept(Header(), Game(), Release(PublicRelease(sha: "not-a-hash"))));

        Assert.Contains("could never be installed", ex.Message);
    }

    [Fact]
    public void A_release_under_a_game_nobody_asserted_is_refused()
    {
        var ex = Assert.Throws<ClaimFormatException>(() =>
            Accept(Header(), Game("game1"), Release(PublicRelease(gameId: "game2"), gameId: "game2")));

        Assert.Contains("which no game claim describes", ex.Message);
    }

    [Fact]
    public void A_revocation_carrying_content_is_refused()
    {
        // A revocation is disclosed to an audience losing access; anything inside it hands back
        // the metadata being withdrawn.
        var revocation = _signer.Sign(ClaimKind.Revocation,
            new ClaimIdentity { Kind = ClaimKind.Release, GameId = "game1", Channel = "stable", Version = "0.9.0" },
            1, ClaimAudience.Everyone, """{"supersedes":1}""");

        var ex = Assert.Throws<ClaimFormatException>(() =>
            Accept(Header(), Game(), Release(PublicRelease()), revocation));

        Assert.Contains("revocation body must be empty", ex.Message);
    }

    [Fact]
    public void A_gated_claim_is_not_hidden_from_acceptance()
    {
        // Acceptance runs over everything, not over one caller's filtered view: a set has to be
        // coherent as a whole before any subset of it is shown to anyone. A hidden release with a
        // broken hash must fail here, not silently ship to the tier that can see it.
        var audience = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t3"] };
        var body = """
        {"gameId":"game1","pluginId":"amethyst","version":"1.1.0","channel":"stable","sha256":"nope","patreon":{"campaignId":"c1","tierIds":["t3"],"serverUrl":"https://example.com/g.zip"}}
        """;

        Assert.Throws<ClaimFormatException>(() =>
            Accept(Header(), Game(), Release(PublicRelease()), Release(body, audience, version: "1.1.0")));
    }
}
