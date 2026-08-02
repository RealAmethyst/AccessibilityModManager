using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// The wire contract, frozen as bytes.
///
/// The manager signs and the server verifies, and they are separate implementations of one written
/// document. Every disagreement found so far — capitalised kind names, UTF-16 length prefixes in a
/// hash preimage, a key size the verifier and the specification disagreed about — was invisible
/// until something tried to read the other side's output. Prose cannot settle those; bytes can.
///
/// The fixture beside this file is the shared artifact: a fixed test key, exact payloads, exact
/// signatures, a manifest and a digest. It is mirrored into the server repository, minus the
/// private half, and any implementation that verifies it agrees with this one.
///
/// Regenerate deliberately with AMM_REGENERATE_GOLDEN=1 — and if a change to the format makes that
/// necessary, that is the moment to tell whoever maintains the other implementation. PSS is
/// randomised, so regenerating changes every signature even when nothing about the format has.
/// </summary>
public sealed class GoldenVectorTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "CatalogClaims", "golden", "signed-claims-v1.golden.json");

    private const string PluginId = "amethyst";
    private const string RepoIndexUrl = "https://accessibilitymods.com/registry/plugins/amethyst/index.json";
    private const string KeyId = "amethyst-2026-07";

    /// <summary>Carries U+1D11E, outside the basic multilingual plane: two UTF-16 units, four UTF-8
    /// bytes, one code point. Three plausible ways to count, and only one is the contract.</summary>
    private const string NonBmpKeyId = "amethyst-\U0001D11E-2026-07";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [Fact]
    public void The_wire_format_still_produces_the_frozen_bytes()
    {
        if (Environment.GetEnvironmentVariable("AMM_REGENERATE_GOLDEN") == "1")
        {
            Regenerate();
            return;
        }

        var golden = JsonNode.Parse(File.ReadAllText(FixturePath))!.AsObject();
        var anchor = AnchorFrom(golden);

        // 1. The trust context: a hash over a preimage two implementations have to build identically.
        Assert.Equal(golden["trustContext"]!.GetValue<string>(), ClaimTrustContext.Compute(anchor));

        // 1b. And the same computation where the counting rule can actually be got wrong.
        var nonBmp = golden["nonBmpTrustContext"]!;
        Assert.Equal(
            nonBmp["trustContext"]!.GetValue<string>(),
            ClaimTrustContext.Compute(anchor with { KeyId = nonBmp["keyId"]!.GetValue<string>() }));

        // 2. Every frozen claim parses, verifies, and re-serializes to the same bytes it arrived as.
        using var verifier = new ClaimVerifier(anchor);
        var claims = new List<SignedClaim>();
        foreach (var entry in golden["claims"]!.AsArray())
        {
            var payload = Convert.FromBase64String(entry!["payload"]!.GetValue<string>());
            var signature = Convert.FromBase64String(entry["signature"]!.GetValue<string>());

            var verified = verifier.Verify(payload, signature);
            claims.Add(verified);

            Assert.Equal(payload, ClaimCodec.Serialize(verified.Payload));
            Assert.Equal(entry["json"]!.GetValue<string>(), Encoding.UTF8.GetString(payload));
        }

        // 3. The digest over the set, which is what the manifest commits to.
        Assert.Equal(golden["claimsDigest"]!.GetValue<string>(), ClaimDigest.Compute(claims));

        // 4. The manifest, under its own signing domain.
        var manifestPayload = Convert.FromBase64String(golden["manifest"]!["payload"]!.GetValue<string>());
        var manifest = verifier.VerifyManifest(
            manifestPayload, Convert.FromBase64String(golden["manifest"]!["signature"]!.GetValue<string>()));

        Assert.Equal(golden["manifest"]!["json"]!.GetValue<string>(), Encoding.UTF8.GetString(manifestPayload));
        Assert.Equal(golden["claimsDigest"]!.GetValue<string>(), manifest.Manifest.ClaimsDigest);
        Assert.Equal(manifestPayload, ManifestCodec.Serialize(manifest.Manifest));

        // 4b. The first manifest in a history, which names no parent.
        var genesisPayload = Convert.FromBase64String(golden["genesisManifest"]!["payload"]!.GetValue<string>());
        var genesis = verifier.VerifyManifest(
            genesisPayload, Convert.FromBase64String(golden["genesisManifest"]!["signature"]!.GetValue<string>()));

        Assert.Equal(1, genesis.Manifest.Generation);
        Assert.Null(genesis.Manifest.Parent);
        Assert.Equal(genesis.PayloadHash, manifest.Manifest.Parent);

        // 5. A whole proof block, read the way a publisher reads one.
        var document = ClaimProof.TryExtract(Encoding.UTF8.GetBytes(golden["index"]!.ToJsonString()))!;
        var proof = ClaimProof.ReadVerified(document, anchor, requireManifest: true);
        Assert.Equal(claims.Count, proof.Claims.Count);
    }

    [Fact]
    public void The_frozen_kind_names_are_lowercase()
    {
        // Spelled out separately because it is the divergence most likely to be reintroduced by
        // someone reaching for ToString() on the enum.
        var golden = JsonNode.Parse(File.ReadAllText(FixturePath))!.AsObject();

        foreach (var entry in golden["claims"]!.AsArray())
        {
            var json = entry!["json"]!.GetValue<string>();
            Assert.Matches("\"kind\":\"(header|game|release|revocation)\"", json);
        }
    }

    private static ClaimTrustAnchor AnchorFrom(JsonObject golden) => new()
    {
        PluginId = golden["anchor"]!["pluginId"]!.GetValue<string>(),
        RepoIndexUrl = golden["anchor"]!["repoIndexUrl"]!.GetValue<string>(),
        Scheme = golden["anchor"]!["scheme"]!.GetValue<string>(),
        KeyId = golden["anchor"]!["keyId"]!.GetValue<string>(),
        Algorithm = golden["anchor"]!["algorithm"]!.GetValue<string>(),
        PublicKeyPem = golden["anchor"]!["publicKeyPem"]!.GetValue<string>()
    };

    /// <summary>
    /// Writes the fixture from the current implementation, into the source tree rather than the
    /// build output. Only ever runs when asked for by name.
    /// </summary>
    private static void Regenerate()
    {
        using var key = RSA.Create(4096);
        var anchor = new ClaimTrustAnchor
        {
            PluginId = PluginId,
            RepoIndexUrl = RepoIndexUrl,
            Scheme = ClaimTrustAnchor.SchemeV1,
            KeyId = KeyId,
            Algorithm = ClaimTrustAnchor.AlgorithmRsaPssSha256,
            PublicKeyPem = key.ExportSubjectPublicKeyInfoPem()
        };

        const string passphrase = "golden";
        using var signer = new ClaimSigner(
            key.ExportEncryptedPkcs8PrivateKeyPem(passphrase,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)),
            passphrase, anchor);

        var claims = new List<SignedClaim>
        {
            signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header }, 1,
                ClaimAudience.Everyone,
                """{"pluginId":"amethyst","repoVersion":"1"}"""),

            signer.Sign(ClaimKind.Game, new ClaimIdentity { Kind = ClaimKind.Game, GameId = "digimonsurvive" }, 1,
                ClaimAudience.Everyone,
                """{"gameId":"digimonsurvive","displayName":"Digimon Survive","modName":"Survive Access"}"""),

            signer.Sign(ClaimKind.Release,
                new ClaimIdentity
                {
                    Kind = ClaimKind.Release, GameId = "digimonsurvive", Channel = "stable", Version = "1.2.0"
                },
                7, ClaimAudience.Everyone,
                $$"""
                {"gameId":"digimonsurvive","pluginId":"amethyst","version":"1.2.0","channel":"stable","packageUrl":"https://accessibilitymods.com/d/digimonsurvive/1.2.0/mod.zip","sha256":"{{new string('a', 64)}}"}
                """),

            signer.Sign(ClaimKind.Release,
                new ClaimIdentity
                {
                    Kind = ClaimKind.Release, GameId = "digimonsurvive", Channel = "beta", Version = "1.3.0"
                },
                2, new ClaimAudience { Public = false, CampaignId = "1234", TierIds = ["t1", "t3"] },
                // The gate and the audience say the same thing, which the acceptance rules require:
                // a release disclosed to tiers its gate does not cover is a leak with a valid
                // signature on it, and one gated tighter than its audience is a build nobody who can
                // see it can install.
                $$$"""
                {"gameId":"digimonsurvive","pluginId":"amethyst","version":"1.3.0","channel":"beta","sha256":"{{{new string('b', 64)}}}","patreon":{"campaignId":"1234","tierIds":["t1","t3"],"serverUrl":"https://accessibilitymods.com/d/digimonsurvive/1.3.0/mod.zip"}}
                """),

            signer.Sign(ClaimKind.Revocation,
                new ClaimIdentity
                {
                    Kind = ClaimKind.Release, GameId = "digimonsurvive", Channel = "stable", Version = "1.1.0"
                },
                4, ClaimAudience.Everyone, "{}")
        };

        var digest = ClaimDigest.Compute(claims);

        // Two linked manifests, not one made-up parent. A fixture with only a later generation
        // pins neither end of the rule that a first publish names no ancestor and every later one
        // must — which is exactly the kind of thing a second implementation guesses at.
        var genesis = signer.SignManifest(1, null, digest);
        var manifest = signer.SignManifest(2, genesis.PayloadHash, digest);

        var index = new JsonObject
        {
            ["pluginId"] = PluginId,
            ["repoVersion"] = "1",
            ["proof"] = JsonSerializer.SerializeToNode(
                ClaimProof.Write(anchor, manifest, claims),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        };

        var fixture = new JsonObject
        {
            ["what"] = "Frozen wire vectors for signed-claims-v1. The private key is a TEST key with " +
                       "no value outside this fixture; it exists so either implementation can " +
                       "regenerate. See docs/signed-claims-v1.md, 'Frozen wire decisions'.",
            ["anchor"] = new JsonObject
            {
                ["pluginId"] = anchor.PluginId,
                ["repoIndexUrl"] = anchor.RepoIndexUrl,
                ["scheme"] = anchor.Scheme,
                ["keyId"] = anchor.KeyId,
                ["algorithm"] = anchor.Algorithm,
                ["publicKeyPem"] = anchor.PublicKeyPem
            },
            ["testPrivateKeyPem"] = key.ExportPkcs8PrivateKeyPem(),
            ["trustContext"] = ClaimTrustContext.Compute(anchor),
            // Every field above is ASCII, where a UTF-16 character count and a UTF-8 byte count are
            // the same number — so the main vector cannot detect the very bug the length-prefix rule
            // was written to prevent. This one can: the key id carries a character outside the basic
            // multilingual plane, where .NET counts two and the bytes number four.
            ["nonBmpTrustContext"] = new JsonObject
            {
                ["why"] = "Pins the length prefix as UTF-8 BYTES. An implementation counting UTF-16 " +
                          "code units, or Unicode code points, produces a different value here and " +
                          "the same value for every other vector in this file.",
                ["keyId"] = NonBmpKeyId,
                ["trustContext"] = ClaimTrustContext.Compute(anchor with { KeyId = NonBmpKeyId })
            },
            ["publicKeyFingerprint"] = ClaimTrustContext.PublicKeyFingerprint(anchor.PublicKeyPem),
            ["claimsDigest"] = digest,
            ["claims"] = new JsonArray([.. claims.Select(c => (JsonNode)new JsonObject
            {
                ["json"] = Encoding.UTF8.GetString(c.PayloadBytes),
                ["payload"] = Convert.ToBase64String(c.PayloadBytes),
                ["signature"] = Convert.ToBase64String(c.Signature)
            })]),
            ["genesisManifest"] = new JsonObject
            {
                ["why"] = "Generation 1 names no parent. Later generations must.",
                ["json"] = Encoding.UTF8.GetString(genesis.PayloadBytes),
                ["payload"] = Convert.ToBase64String(genesis.PayloadBytes),
                ["signature"] = Convert.ToBase64String(genesis.Signature),
                ["payloadHash"] = genesis.PayloadHash
            },
            ["manifest"] = new JsonObject
            {
                ["json"] = Encoding.UTF8.GetString(manifest.PayloadBytes),
                ["payload"] = Convert.ToBase64String(manifest.PayloadBytes),
                ["signature"] = Convert.ToBase64String(manifest.Signature),
                ["payloadHash"] = manifest.PayloadHash
            },
            ["index"] = index
        };

        var source = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "CatalogClaims", "golden"));
        System.IO.Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "signed-claims-v1.golden.json"), fixture.ToJsonString(Pretty));
    }
}
