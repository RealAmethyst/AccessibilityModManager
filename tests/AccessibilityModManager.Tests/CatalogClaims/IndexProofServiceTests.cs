using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// Publishing a real index, and — mostly — the cases where publishing must stop instead.
///
/// The attacks below all share a shape: the server never forges anything, it only withholds. Each
/// one ends with the author's own offline key signing two different truths about one object, which
/// is indistinguishable from the author lying.
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
        _service = new IndexProofService(
            _keyStore, new PublisherHeadStore(config, TestLogger.Create()), TestLogger.Create());
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


    /// <summary>
    /// A signed registry as the caller would hand it over — already signature-verified, which is
    /// the precondition, not something this class checks.
    /// </summary>
    private string Registry(string? publicKeyPem = null, int version = 1) => $$"""
        {
          "registryVersion": "{{version}}",
          "plugins": [
            { "id": "amethyst", "repoIndexUrl": "{{IndexUrl}}",
              "indexTrust": {
                "scheme": "signed-claims-v1",
                "keyId": "{{_signing.KeyId}}",
                "algorithm": "rsa-pss-sha256",
                "publicKeyPem": {{JsonSerializer.Serialize(publicKeyPem ?? _signing.PublicKeyPem)}}
              } }
          ]
        }
        """;

    /// <summary>A whole publish: prepare, then confirm what came back is what went out.</summary>
    private byte[] Publish(byte[] index, byte[]? live)
    {
        var prepared = _service.PreparePublish(index, Registry(), "amethyst", live, allowBootstrap: live is null);
        _service.ConfirmPublished(Anchor(), prepared.IndexJson);
        return prepared.IndexJson;
    }

    private IReadOnlyList<SignedClaim> ClaimsIn(byte[] indexJson) =>
        ClaimProof.ReadVerified(
            ClaimProof.TryExtract(indexJson)!, Anchor(), requireManifest: true).Claims;

    private static byte[] IndexBytes(string pluginId = "amethyst", params ModRelease[] releases) =>
        IndexBytes(pluginId, ["game1"], releases);

    private static byte[] IndexBytes(string pluginId, string[] gameIds, params ModRelease[] releases)
    {
        var index = new PluginRepoIndex
        {
            PluginId = pluginId,
            RepoVersion = "1",
            GeneratedAt = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc),
            Games = [.. gameIds.Select(g => new GameDefinition
            {
                GameId = g, DisplayName = "Game " + g, ModName = "Mod"
            })],
            ReleasesByGameId = new Dictionary<string, List<ModRelease>>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var group in releases.GroupBy(r => r.GameId))
            index.ReleasesByGameId[group.Key] = [.. group];

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(index, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    private static ModRelease Release(string version, PatreonGate? gate = null, string gameId = "game1") => new()
    {
        GameId = gameId,
        PluginId = "amethyst",
        Version = version,
        Channel = "stable",
        Sha256 = new string('b', 64),
        PackageUrl = gate is null ? new Uri("https://example.com/p.zip") : null,
        Patreon = gate
    };

    /// <summary>Rewrites a published index through a JSON edit, the way a server holding the file could.</summary>
    private static byte[] Tamper(byte[] indexJson, Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(indexJson))!.AsObject();
        edit(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    // ---- the ordinary path ----

    [Fact]
    public void A_proof_is_attached_and_verifies_against_the_registry_entry()
    {
        var published = Publish(IndexBytes(releases: Release("1.0.0")), null);

        Assert.Equal(3, ClaimsIn(published).Count); // header, game, release
    }

    [Fact]
    public void The_rest_of_the_index_is_left_intact()
    {
        var original = IndexBytes(releases: Release("1.0.0"));
        var published = Publish(original, null);

        // Old managers read these fields and must be unaffected by the proof riding along.
        using var before = JsonDocument.Parse(original);
        using var after = JsonDocument.Parse(published);
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
        var first = Publish(index, null);

        var prepared = _service.PreparePublish(index, Registry(), "amethyst", first, allowBootstrap: false);

        Assert.Equal(3, prepared.Build.Unchanged);
        Assert.Equal(0, prepared.Build.Added + prepared.Build.Updated + prepared.Build.Revoked);
    }

    [Fact]
    public void Each_publish_advances_the_generation_and_names_its_parent()
    {
        var first = Publish(IndexBytes(releases: Release("1.0.0")), null);
        var firstManifest = ManifestOf(first);

        var second = Publish(IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), first);
        var secondManifest = ManifestOf(second);

        Assert.Equal(1, firstManifest.Manifest.Generation);
        Assert.Null(firstManifest.Manifest.Parent);
        Assert.Equal(2, secondManifest.Manifest.Generation);
        Assert.Equal(firstManifest.PayloadHash, secondManifest.Manifest.Parent);
    }

    private SignedManifest ManifestOf(byte[] indexJson) =>
        ClaimProof.ReadVerified(
            ClaimProof.TryExtract(indexJson)!, Anchor(), requireManifest: true).Manifest!;

    [Fact]
    public void Sequences_continue_from_what_is_published()
    {
        var v1 = Publish(IndexBytes(releases: Release("1.0.0")), null);
        var v2 = Publish(IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), v1);
        var v3 = Publish(IndexBytes(releases: Release("1.0.0")), v2);

        // 1.1.0 was deleted in v3, so its identity now carries a revocation above its old sequence.
        var revocation = ClaimsIn(v3).Single(c => c.Payload.Kind == ClaimKind.Revocation);
        var oldRelease = ClaimsIn(v2).Single(c => c.Payload.Identity.Version == "1.1.0");

        Assert.True(revocation.Payload.Seq > oldRelease.Payload.Seq);
    }

    [Fact]
    public void An_index_for_a_different_plugin_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(pluginId: "someoneelse"), Registry(), "amethyst", null, allowBootstrap: true));
        Assert.Contains("no manager would accept", ex.Message);
    }

    // ---- what the manifest is for: omission ----

    [Fact]
    public void Removing_one_claim_from_the_published_proof_stops_the_next_publish()
    {
        // The attack the manifest exists for. Drop a single release claim and every remaining claim
        // still verifies; without a commitment to the whole set, the next publish would see no
        // history for that release and sign sequence 1 over a sequence already in the wild.
        var live = Publish(IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), null);

        var trimmed = Tamper(live, root =>
        {
            var claims = root["proof"]!["claims"]!.AsArray();
            claims.RemoveAt(claims.Count - 1);
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")),
                Registry(), "amethyst", trimmed, allowBootstrap: false));
        Assert.Contains("added, removed or replaced", ex.Message);
    }

    [Fact]
    public void Duplicating_a_claim_in_the_published_proof_stops_the_next_publish()
    {
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);

        var doubled = Tamper(live, root =>
        {
            var claims = root["proof"]!["claims"]!.AsArray();
            claims.Add(JsonNode.Parse(claims[0]!.ToJsonString())!);
        });

        Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(releases: Release("1.0.0")), Registry(), "amethyst", doubled, false));
    }

    [Fact]
    public void Removing_the_manifest_stops_the_next_publish()
    {
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);

        var stripped = Tamper(live, root => root["proof"]!.AsObject().Remove("manifest"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(releases: Release("1.0.0")), Registry(), "amethyst", stripped, false));
        Assert.Contains("no way to tell whether any of it was removed", ex.Message);
    }

    [Fact]
    public void Removing_the_whole_proof_does_not_restart_the_history()
    {
        // This used to read as "an index from before claims existed" and start again at sequence 1 —
        // so deleting one member of one file reset every counter the catalog had.
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);
        var stripped = Tamper(live, root => root.Remove("proof"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(releases: Release("1.0.0")), Registry(), "amethyst", stripped, false));
        Assert.Contains("nothing at all", ex.Message);
    }

    [Fact]
    public void A_proof_member_that_is_not_an_object_is_a_violation_rather_than_an_absence()
    {
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);
        var wrongShape = Tamper(live, root => root["proof"] = JsonValue.Create("gone"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(releases: Release("1.0.0")), Registry(), "amethyst", wrongShape, false));
        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public void An_index_that_repeats_the_proof_member_is_refused()
    {
        // Two proofs, each internally valid: one implementation takes the first, another the last,
        // and the manifest commits only to decoded payloads — it says nothing about which outer
        // member a parser picked.
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);
        var text = Encoding.UTF8.GetString(live);
        var doubled = text.TrimEnd()[..^1] + ", \"proof\": {\"scheme\":\"signed-claims-v1\"}}";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(releases: Release("1.0.0")),
                Registry(), "amethyst", Encoding.UTF8.GetBytes(doubled), false));
        Assert.Contains("could not be read", ex.Message);
    }

    // ---- what the local head is for: replay ----

    [Fact]
    public void Replaying_an_older_complete_publish_is_refused()
    {
        // Everything in the older publish verifies and its digest is whole. Only this machine's own
        // record knows it has already moved past it. Without that, the next publish would sign a
        // second, different version of a generation that already exists.
        var v1 = Publish(IndexBytes(releases: Release("1.0.0")), null);
        var v2 = Publish(IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), v1);
        Assert.NotEqual(v1, v2);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes("amethyst", Release("1.0.0"), Release("1.2.0")),
                Registry(), "amethyst", v1, allowBootstrap: false));
        Assert.Contains("not the one this machine last published", ex.Message);
    }

    [Fact]
    public void A_live_proof_this_machine_has_no_record_of_is_refused()
    {
        // The second-machine case, and also what a rolled-back server looks like. The two are
        // indistinguishable from here, so neither is adopted automatically.
        var published = Publish(IndexBytes(releases: Release("1.0.0")), null);

        var elsewhere = new IndexProofService(
            _keyStore,
            new PublisherHeadStore(
                new AuthorConfigService(TestLogger.Create(), Path.Combine(_root, "other")), TestLogger.Create()),
            TestLogger.Create());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            elsewhere.PreparePublish(IndexBytes(releases: Release("1.0.0")), Registry(), "amethyst", published, false));
        Assert.Contains("no record of publishing it", ex.Message);
    }

    [Fact]
    public void Starting_a_history_takes_a_deliberate_confirmation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(releases: Release("1.0.0")), Registry(), "amethyst", null, allowBootstrap: false));
        Assert.Contains("deliberate step", ex.Message);
    }

    [Fact]
    public void An_index_from_before_claims_existed_is_not_a_reason_to_bootstrap_silently()
    {
        var legacy = IndexBytes(releases: Release("1.0.0"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(releases: Release("1.0.0")), Registry(), "amethyst", legacy, allowBootstrap: false));
        Assert.Contains("deliberate step", ex.Message);
    }

    [Fact]
    public void A_live_proof_that_does_not_verify_stops_the_publish()
    {
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);

        // A rotation: the registry moves on to a different key, at a higher version, so the
        // freshness check passes and it is the proof itself that cannot be continued.
        var strangerKey = ClaimTestKeys.Secondary;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(releases: Release("1.0.0")),
                Registry(strangerKey.ExportSubjectPublicKeyInfoPem(), version: 2), "amethyst", live, false));
        Assert.Contains("doesn't verify", ex.Message);
    }

    // ---- the registry is a document that can be replayed too ----

    [Fact]
    public void A_registry_older_than_one_this_machine_has_acted_on_is_refused()
    {
        // Re-pointing a plugin's index address, or rotating its key, is how a compromised source
        // gets disowned — and both live in the registry, which is signed and therefore replays
        // perfectly. The head rules cannot catch it: each trust context keeps its own head, and the
        // retired one's head is genuinely this machine's.
        _service.PreparePublish(IndexBytes(releases: Release("1.0.0")), Registry(version: 5), "amethyst",
            null, allowBootstrap: true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(releases: Release("1.0.0")), Registry(version: 4), "amethyst",
                null, allowBootstrap: true));

        Assert.Contains("already seen version 5", ex.Message);
    }

    [Fact]
    public void A_registry_changed_without_raising_its_version_is_refused()
    {
        Publish(IndexBytes(releases: Release("1.0.0")), null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(releases: Release("1.0.0")),
                Registry(ClaimTestKeys.Secondary.ExportSubjectPublicKeyInfoPem()), "amethyst", null, true));

        Assert.Contains("was not raised", ex.Message);
    }

    [Fact]
    public void A_registry_with_no_orderable_version_is_refused()
    {
        var unorderable = Registry().Replace("\"registryVersion\": \"1\"", "\"registryVersion\": \"spring\"");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(releases: Release("1.0.0")), unorderable, "amethyst", null, true));

        Assert.Contains("no way to tell whether it is the current one", ex.Message);
    }

    // ---- the crash window ----

    [Fact]
    public void An_unconfirmed_publish_blocks_the_next_one()
    {
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);
        _service.PreparePublish(IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), Registry(), "amethyst", live, false);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PreparePublish(IndexBytes(releases: Release("1.0.0")), Registry(), "amethyst", live, false));
        Assert.Contains("interrupted", ex.Message);
    }

    [Fact]
    public void An_interrupted_publish_that_landed_is_recognised_and_committed()
    {
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);
        var prepared = _service.PreparePublish(
            IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), Registry(), "amethyst", live, false);

        // The rename happened; the process died before the local commit.
        Assert.Equal(IndexProofService.PendingOutcome.Landed,
            _service.ResolvePending(Anchor(), prepared.IndexJson));

        _service.CommitPending(Anchor());
        var next = _service.PreparePublish(
            IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), Registry(), "amethyst", prepared.IndexJson, false);
        Assert.Equal(3, next.Pending.Generation);
    }

    [Fact]
    public void An_interrupted_publish_that_never_left_is_recognised_and_its_bytes_kept()
    {
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);
        var prepared = _service.PreparePublish(
            IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), Registry(), "amethyst", live, false);

        Assert.Equal(IndexProofService.PendingOutcome.NotSent, _service.ResolvePending(Anchor(), live));

        // Kept verbatim rather than rebuilt: PSS is randomised, so rebuilding would produce
        // different bytes under the same generation — the fork, arriving through recovery.
        Assert.Equal(prepared.IndexJson, _service.ReadPendingIndex(Anchor()));
    }

    [Fact]
    public void An_interrupted_publish_that_finds_something_else_live_reports_divergence()
    {
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);
        _service.PreparePublish(IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), Registry(), "amethyst", live, false);

        // A different publish under the same key — neither the head this attempt was extending nor
        // the one it prepared. Something happened that this machine cannot account for, and the
        // answer is to say so rather than to guess.
        var elsewhere = new IndexProofService(
            _keyStore,
            new PublisherHeadStore(
                new AuthorConfigService(TestLogger.Create(), Path.Combine(_root, "other")), TestLogger.Create()),
            TestLogger.Create());
        var theirs = elsewhere.PreparePublish(
            IndexBytes(releases: Release("2.0.0")), Registry(), "amethyst", null, allowBootstrap: true).IndexJson;

        Assert.Equal(IndexProofService.PendingOutcome.Diverged, _service.ResolvePending(Anchor(), theirs));
    }

    [Fact]
    public void An_interrupted_publish_whose_plaintext_was_rewritten_is_not_landed()
    {
        // The manifest commits to the claims. The compatibility plaintext beside them — which is
        // what every current manager actually reads — is not covered by it, so a server can keep
        // the proof intact and rewrite the rest. Matching manifests are not the same as "this is
        // the document I sent".
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);
        var prepared = _service.PreparePublish(
            IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), Registry(), "amethyst", live, false);

        var rewritten = Tamper(prepared.IndexJson, root => root["repoVersion"] = "9");

        Assert.Equal(IndexProofService.PendingOutcome.Diverged, _service.ResolvePending(Anchor(), rewritten));
    }

    [Fact]
    public void Prepared_bytes_that_were_damaged_on_disk_are_refused_rather_than_re_sent()
    {
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);
        _service.PreparePublish(IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), Registry(), "amethyst", live, false);

        var pendingPath = Directory
            .EnumerateFiles(Path.Combine(_root, "publisher"), "*.pending-index.json")
            .Single();
        File.WriteAllText(pendingPath, "{}");

        var ex = Assert.Throws<InvalidOperationException>(() => _service.ReadPendingIndex(Anchor()));
        Assert.Contains("not the one that was prepared", ex.Message);
    }

    [Fact]
    public void Confirming_a_publish_that_is_not_what_went_out_is_refused()
    {
        var live = Publish(IndexBytes(releases: Release("1.0.0")), null);
        _service.PreparePublish(IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), Registry(), "amethyst", live, false);

        var ex = Assert.Throws<InvalidOperationException>(() => _service.ConfirmPublished(Anchor(), live));
        Assert.Contains("not the one that was just published", ex.Message);
    }

    // ---- immutability ----

    [Fact]
    public void A_withdrawn_release_version_cannot_be_published_again()
    {
        var v1 = Publish(IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), null);
        var v2 = Publish(IndexBytes(releases: Release("1.0.0")), v1);

        var ex = Assert.Throws<ClaimFormatException>(() =>
            _service.PreparePublish(IndexBytes("amethyst", Release("1.0.0"), Release("1.1.0")), Registry(), "amethyst", v2, false));
        Assert.Contains("withdrawn", ex.Message);
    }

    [Fact]
    public void A_removed_game_can_be_added_back()
    {
        // Games carry no byte-immutability promise, so removing one is not permanent the way
        // withdrawing a release is.
        var v1 = Publish(IndexBytes("amethyst", ["game1", "game2"]), null);
        var v2 = Publish(IndexBytes("amethyst", ["game1"]), v1);
        var v3 = Publish(IndexBytes("amethyst", ["game1", "game2"]), v2);

        var game2 = ClaimsIn(v3).Single(c =>
            c.Payload.Identity.GameId == "game2" && c.Payload.Kind == ClaimKind.Game);
        Assert.Equal(ClaimKind.Game, game2.Payload.Kind);
    }

    // ---- disclosure ----

    [Fact]
    public void Adding_a_patron_release_reuses_the_public_claims_unchanged()
    {
        var before = Publish(IndexBytes(releases: Release("1.0.0")), null);
        var after = Publish(
            IndexBytes("amethyst", Release("1.0.0"),
                Release("1.1.0", new PatreonGate { CampaignId = "c1", TierIds = ["t3"], ServerUrl = "https://example.com/g.zip" })),
            before);

        var publicBefore = ClaimVerifier.ResolveVisible(ClaimsIn(before), null, null);
        var publicAfter = ClaimVerifier.ResolveVisible(ClaimsIn(after), null, null);

        Assert.Equal(publicBefore.Count, publicAfter.Count);
        foreach (var claim in publicBefore)
        {
            var match = publicAfter.Single(c =>
                c.Payload.Identity.ToStorageKey() == claim.Payload.Identity.ToStorageKey());
            Assert.Equal(claim.PayloadBytes, match.PayloadBytes);
        }
    }

    [Fact]
    public void Author_only_game_fields_never_reach_a_claim()
    {
        var index = JsonNode.Parse(Encoding.UTF8.GetString(IndexBytes()))!.AsObject();
        index["games"]![0]!["defaultPostInstall"] = JsonNode.Parse("""
            {"executable":"setup.ps1","what":"w","why":"y","modifies":"the game folder"}
            """);

        var published = Publish(Encoding.UTF8.GetBytes(index.ToJsonString()), null);

        var game = ClaimsIn(published).Single(c => c.Payload.Kind == ClaimKind.Game);
        Assert.DoesNotContain("defaultPostInstall", game.Payload.BodyJson);
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
        var prepared = _service.PreparePublish(
            IndexBytes(releases: Release("1.0.0")), registry, "amethyst", null, allowBootstrap: true);
        ClaimProof.ReadVerified(
            ClaimProof.TryExtract(prepared.IndexJson)!, anchor, requireManifest: true);
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
