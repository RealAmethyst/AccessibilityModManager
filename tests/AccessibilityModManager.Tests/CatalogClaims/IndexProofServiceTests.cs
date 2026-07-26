using System.Text;
using System.Text.Json;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// Attaching a proof to a real index on the way to publication, and the cases where publishing must
/// stop instead.
/// </summary>
public sealed class IndexProofServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ClaimSigningKeyStore _keyStore;
    private readonly IndexProofService _service;
    private readonly ClaimSigningConfig _signing;
    private const string IndexUrl = "https://accessibilitymods.com/registry/plugins/amethyst/index.json";

    public IndexProofServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "proofsvc-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
        var config = new AuthorConfigService(TestLogger.Create(), _root);
        _keyStore = new ClaimSigningKeyStore(config, TestLogger.Create());
        _signing = _keyStore.Create("amethyst", "pp");
        _service = new IndexProofService(_keyStore, TestLogger.Create());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private ClaimTrustAnchor Anchor(string? url = null) => new()
    {
        PluginId = "amethyst",
        RepoIndexUrl = url ?? IndexUrl,
        Scheme = ClaimTrustAnchor.SchemeV1,
        KeyId = _signing.KeyId,
        Algorithm = ClaimTrustAnchor.AlgorithmRsaPssSha256,
        PublicKeyPem = _signing.PublicKeyPem
    };

    private static byte[] IndexBytes(string pluginId = "amethyst", params ModRelease[] releases)
    {
        var index = new PluginRepoIndex
        {
            PluginId = pluginId,
            RepoVersion = "1",
            GeneratedAt = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc),
            Games = [new GameDefinition { GameId = "game1", DisplayName = "Game One", ModName = "Mod" }],
            ReleasesByGameId = new Dictionary<string, List<ModRelease>>(StringComparer.OrdinalIgnoreCase)
        };
        if (releases.Length > 0) index.ReleasesByGameId["game1"] = [.. releases];

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(index, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    private static ModRelease Release(string version, PatreonGate? gate = null) => new()
    {
        GameId = "game1",
        PluginId = "amethyst",
        Version = version,
        Channel = "stable",
        Sha256 = new string('b', 64),
        PackageUrl = gate is null ? new Uri("https://example.com/p.zip") : null,
        Patreon = gate
    };

    [Fact]
    public void A_proof_is_attached_and_verifies_against_the_registry_entry()
    {
        var result = _service.AttachProof(IndexBytes(releases: Release("1.0.0")), Anchor(), null);

        var document = ClaimProof.TryExtract(Encoding.UTF8.GetString(result.IndexJson));
        Assert.NotNull(document);

        var verified = ClaimProof.ReadVerified(document!, Anchor());
        Assert.Equal(3, verified.Count); // header, game, release
    }

    [Fact]
    public void The_rest_of_the_index_is_left_intact()
    {
        var original = IndexBytes(releases: Release("1.0.0"));
        var result = _service.AttachProof(original, Anchor(), null);

        // Old managers read these fields and must be unaffected by the proof riding along.
        using var before = JsonDocument.Parse(original);
        using var after = JsonDocument.Parse(result.IndexJson);
        Assert.Equal(
            before.RootElement.GetProperty("games").GetRawText(),
            after.RootElement.GetProperty("games").GetRawText());
        Assert.Equal("amethyst", after.RootElement.GetProperty("pluginId").GetString());
        Assert.Equal("1", after.RootElement.GetProperty("repoVersion").GetString());
    }

    [Fact]
    public void Republishing_an_unchanged_index_reuses_the_existing_claims()
    {
        var index = IndexBytes(releases: Release("1.0.0"));
        var first = _service.AttachProof(index, Anchor(), null);

        var second = _service.AttachProof(index, Anchor(), first.IndexJson);

        Assert.Equal(first.Build.Claims.Count, second.Build.Unchanged);
        Assert.Equal(0, second.Build.Added + second.Build.Updated + second.Build.Revoked);
    }

    [Fact]
    public void Sequences_continue_from_what_is_published_not_from_local_state()
    {
        // The scenario this protects: the author restores an older project folder, or publishes from
        // a second machine. Sequences must keep climbing from what is live, or a number already in
        // use would be reissued and managers that saw the first one would reject the second.
        var v1 = _service.AttachProof(IndexBytes(releases: Release("1.0.0")), Anchor(), null);
        var v2 = _service.AttachProof(
            IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), Anchor(), v1.IndexJson);
        var v3 = _service.AttachProof(
            IndexBytes(releases: Release("1.0.0")), Anchor(), v2.IndexJson);

        // 1.1.0 was deleted in v3, so its identity now carries a revocation above its old sequence.
        var live = ClaimProof.ReadVerified(
            ClaimProof.TryExtract(Encoding.UTF8.GetString(v3.IndexJson))!, Anchor());
        var revocation = live.Single(c => c.Payload.Kind == ClaimKind.Revocation);
        var oldRelease = ClaimProof.ReadVerified(
                ClaimProof.TryExtract(Encoding.UTF8.GetString(v2.IndexJson))!, Anchor())
            .Single(c => c.Payload.Identity.Version == "1.1.0");

        Assert.True(revocation.Payload.Seq > oldRelease.Payload.Seq);
    }

    [Fact]
    public void An_index_for_a_different_plugin_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.AttachProof(IndexBytes(pluginId: "someoneelse"), Anchor(), null));
        Assert.Contains("no manager would accept", ex.Message);
    }

    [Fact]
    public void A_live_proof_that_does_not_verify_stops_the_publish()
    {
        // Either the registry now names a different key, or that file was altered. Treating it as
        // "no history" would restart sequences at one — the exact equivocation the rules forbid.
        var live = _service.AttachProof(IndexBytes(releases: Release("1.0.0")), Anchor(), null);

        using var strangerKey = System.Security.Cryptography.RSA.Create(3072);
        var strangerAnchor = Anchor() with { PublicKeyPem = strangerKey.ExportSubjectPublicKeyInfoPem() };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.AttachProof(IndexBytes(releases: Release("1.0.0")), strangerAnchor, live.IndexJson));
        Assert.Contains("doesn't verify", ex.Message);
    }

    [Fact]
    public void A_live_index_from_before_claims_existed_starts_a_fresh_history()
    {
        var legacy = IndexBytes(releases: Release("1.0.0"));

        var result = _service.AttachProof(IndexBytes(releases: Release("1.0.0")), Anchor(), legacy);

        Assert.Equal(3, result.Build.Added);
        Assert.Equal(0, result.Build.Unchanged);
    }

    [Fact]
    public void Adding_a_patron_release_leaves_the_public_view_byte_identical()
    {
        var before = _service.AttachProof(IndexBytes(releases: Release("1.0.0")), Anchor(), null);

        var after = _service.AttachProof(
            IndexBytes("amethyst", Release("1.0.0"),
                Release("1.1.0", new PatreonGate { CampaignId = "c1", TierIds = ["t3"] })),
            Anchor(), before.IndexJson);

        var publicBefore = ClaimVerifier.ResolveVisible(
            ClaimProof.ReadVerified(ClaimProof.TryExtract(Encoding.UTF8.GetString(before.IndexJson))!, Anchor()),
            null, null);
        var publicAfter = ClaimVerifier.ResolveVisible(
            ClaimProof.ReadVerified(ClaimProof.TryExtract(Encoding.UTF8.GetString(after.IndexJson))!, Anchor()),
            null, null);

        Assert.Equal(publicBefore.Count, publicAfter.Count);
        foreach (var claim in publicBefore)
        {
            var match = publicAfter.Single(c =>
                c.Payload.Identity.ToStorageKey() == claim.Payload.Identity.ToStorageKey());
            Assert.Equal(claim.PayloadBytes, match.PayloadBytes);
        }
    }

    // ---- reading the anchor out of the registry ----

    [Fact]
    public void The_anchor_is_read_from_the_registry_entry()
    {
        var registry = $$"""
        {
          "registryVersion": "3",
          "plugins": [
            { "id": "someoneelse", "repoIndexUrl": "https://example.com/other.json" },
            { "id": "amethyst", "repoIndexUrl": "{{IndexUrl}}",
              "indexTrust": {
                "scheme": "signed-claims-v1",
                "keyId": "{{_signing.KeyId}}",
                "algorithm": "rsa-pss-sha256",
                "publicKeyPem": {{JsonSerializer.Serialize(_signing.PublicKeyPem)}}
              } }
          ]
        }
        """;

        var anchor = IndexProofService.TryReadAnchor(registry, "amethyst");

        Assert.NotNull(anchor);
        Assert.Equal(IndexUrl, anchor!.RepoIndexUrl);
        Assert.Equal(_signing.KeyId, anchor.KeyId);
        // And it is usable: claims signed under it verify.
        var result = _service.AttachProof(IndexBytes(releases: Release("1.0.0")), anchor, null);
        ClaimProof.ReadVerified(ClaimProof.TryExtract(Encoding.UTF8.GetString(result.IndexJson))!, anchor);
    }

    [Fact]
    public void An_entry_without_an_index_trust_block_yields_no_anchor()
    {
        // Normal before an author has published a signing key — the caller then publishes without a
        // proof rather than failing.
        var registry = $$"""
        { "registryVersion": "1",
          "plugins": [ { "id": "amethyst", "repoIndexUrl": "{{IndexUrl}}" } ] }
        """;

        Assert.Null(IndexProofService.TryReadAnchor(registry, "amethyst"));
    }

    [Fact]
    public void An_unknown_plugin_yields_no_anchor()
    {
        var registry = """{ "registryVersion": "1", "plugins": [] }""";
        Assert.Null(IndexProofService.TryReadAnchor(registry, "amethyst"));
    }
}
