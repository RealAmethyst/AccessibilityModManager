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
/// Opening a project, which is the one moment the tool writes over the author's folder without
/// being asked to.
///
/// <para>That makes it the place a hostile server would most like to reach: whatever lands in the
/// folder is what the author edits, and what the author edits is what they later put their signing
/// key behind. So the question every test here asks is the same one — under what circumstances do
/// bytes from the server end up on disk, and did anything actually verify them first.</para>
/// </summary>
public sealed class ProjectReconcilerTests : IDisposable
{
    private const string PluginId = "amethyst";
    private const string IndexUrl = "https://accessibilitymods.com/registry/plugins/amethyst/index.json";

    private readonly string _root;
    private readonly PublisherHeadStore _heads;
    private readonly ClaimSigningKeyStore _keys;
    private readonly IndexProofService _proofs;
    private readonly ClaimSigningConfig _signing;
    private readonly ProjectReconciler _reconciler;
    private readonly FakeReader _reader = new();
    private readonly FakeRegistry _registry;

    public ProjectReconcilerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reconcile-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);

        var config = new AuthorConfigService(TestLogger.Create(), _root);
        _heads = new PublisherHeadStore(config, TestLogger.Create());
        _keys = new ClaimSigningKeyStore(config, _heads, TestLogger.Create());
        _signing = _keys.Create(PluginId, "pp");
        _proofs = new IndexProofService(_keys, _heads, TestLogger.Create());
        _reconciler = new ProjectReconciler(_heads, _proofs, TestLogger.Create());
        _registry = new FakeRegistry { Json = Registry() };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    // ---- the world ----

    private string Registry(string? publicKeyPem = null) => $$"""
        {
          "registryVersion": "1",
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

    private static string UnsignedRegistry() => $$"""
        { "registryVersion": "1",
          "plugins": [ { "id": "amethyst", "repoIndexUrl": "{{IndexUrl}}" } ] }
        """;

    private ClaimTrustAnchor Anchor() => new()
    {
        PluginId = PluginId,
        RepoIndexUrl = IndexUrl,
        Scheme = ClaimTrustAnchor.SchemeV1,
        KeyId = _signing.KeyId,
        Algorithm = ClaimTrustAnchor.AlgorithmRsaPssSha256,
        PublicKeyPem = _signing.PublicKeyPem
    };

    private static byte[] IndexBytes(params string[] versions) => IndexBytes(null, versions);

    private static byte[] IndexBytes(Action<JsonObject>? edit, params string[] versions)
    {
        var index = new PluginRepoIndex
        {
            PluginId = PluginId,
            RepoVersion = "1",
            GeneratedAt = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
            Games = [new GameDefinition { GameId = "game1", DisplayName = "Game one", ModName = "Mod" }],
            ReleasesByGameId = new Dictionary<string, List<ModRelease>>(StringComparer.OrdinalIgnoreCase)
            {
                ["game1"] = [.. versions.Select(v => new ModRelease
                {
                    GameId = "game1",
                    PluginId = PluginId,
                    Version = v,
                    Channel = "stable",
                    Sha256 = new string('b', 64),
                    PackageUrl = new Uri("https://example.com/p.zip")
                })]
            }
        };

        var json = JsonSerializer.Serialize(index, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        if (edit is null) return Encoding.UTF8.GetBytes(json);

        var root = JsonNode.Parse(json)!.AsObject();
        edit(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Publishes for real, so the head this machine remembers is one it actually confirmed.</summary>
    private byte[] Publish(byte[] index, byte[]? live)
    {
        var prepared = _proofs.PreparePublish(index, Registry(), PluginId, live,
            allowBootstrap: live is null,
            confirmedDeletions: _proofs.PreviewPublish(index, Registry(), PluginId, live).DeletionsToken);
        _proofs.ConfirmPublished(Anchor(), prepared.IndexJson);
        _reader.Live = prepared.IndexJson;
        return prepared.IndexJson;
    }

    /// <param name="withoutAReader">
    /// Stands in for a machine with no server connection configured. A flag rather than a nullable
    /// parameter, because "pass null" and "leave it out" would then be the same call, and one of
    /// them is the case under test.
    /// </param>
    /// <param name="publishedLocal">
    /// The local bytes this folder last published or adopted. Defaults to <paramref name="local"/>,
    /// meaning the folder has nothing in it that was never published.
    ///
    /// <para>Passed as BYTES rather than as a flag, and hashed here the way the tool does, so a test
    /// for unpublished work exercises a real marker that disagrees with the file — which is the
    /// actual situation. Modelling it as a missing marker instead would leave the mismatch branch
    /// untested, and a predicate narrowed to "no marker at all" would keep the suite green while the
    /// case that loses an author's release went unguarded.</para>
    /// </param>
    /// <param name="noMarker">This folder has never published or adopted anything.</param>
    private Task<ReconcileOutcome> InspectAsync(
        byte[] local, bool withoutAReader = false, byte[]? publishedLocal = null, bool noMarker = false) =>
        _reconciler.InspectAsync(
            withoutAReader ? null : _reader, _registry, PluginId, local,
            noMarker
                ? null
                : Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(publishedLocal ?? local)),
            CancellationToken.None);

    // ---- the anchor decides whether any of this applies ----

    [Fact]
    public async Task A_plugin_the_registry_anchors_no_key_for_is_left_to_the_unsigned_path()
    {
        _registry.Json = UnsignedRegistry();

        var outcome = await InspectAsync(IndexBytes("1.0.0"));

        // A key exists on this machine and it changes nothing: switching signing on must be one
        // deliberate registry publication, not a side effect of having made a key.
        Assert.Equal(ReconcileAction.Unsigned, outcome.Action);
        Assert.Equal(0, _reader.Reads);
    }

    [Fact]
    public async Task An_anchor_that_disappeared_after_a_signed_publish_is_never_treated_as_unsigned()
    {
        Publish(IndexBytes("1.0.0"), null);
        _registry.Json = UnsignedRegistry();

        var outcome = await InspectAsync(IndexBytes("1.0.0"));

        Assert.Equal(ReconcileAction.Explain, outcome.Action);
        Assert.Null(outcome.Document);
    }

    [Fact]
    public async Task An_unreadable_registry_changes_nothing_and_says_nothing()
    {
        _registry.Fail = new RegistryUnusableException("no route to host");

        var outcome = await InspectAsync(IndexBytes("1.0.0"));

        // Opening a project offline is ordinary. Nothing is adopted, and there is nothing here the
        // author needs told — the registry banner on this screen already says it.
        Assert.Equal(ReconcileAction.Nothing, outcome.Action);
        Assert.Null(outcome.Message);
    }

    // ---- nothing is adopted that was not verified ----

    [Fact]
    public async Task An_unsigned_published_index_is_never_adopted_under_an_anchor()
    {
        _reader.Live = IndexBytes("1.0.0", "1.1.0");

        var outcome = await InspectAsync(IndexBytes("1.0.0"));

        // The state between anchoring a key and the first signed publish. There is nothing here that
        // can be checked, so there is nothing here to take.
        Assert.Equal(ReconcileAction.Explain, outcome.Action);
        Assert.Null(outcome.Document);
    }

    [Fact]
    public async Task A_proof_that_does_not_verify_is_never_adopted()
    {
        var published = Publish(IndexBytes("1.0.0"), null);
        _reader.Live = Tamper(published, root =>
            root["proof"]!["claims"]![0]!["signature"] = Convert.ToBase64String(new byte[512]));

        var outcome = await InspectAsync(IndexBytes("1.0.0"));

        Assert.Equal(ReconcileAction.Explain, outcome.Action);
        Assert.Null(outcome.Document);
    }

    [Fact]
    public async Task A_signed_catalog_this_machine_did_not_publish_is_never_adopted()
    {
        Publish(IndexBytes("1.0.0"), null);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, "publisher"), "*.json"))
            if (Path.GetFileNameWithoutExtension(file).Length == 64) File.Delete(file);

        var outcome = await InspectAsync(IndexBytes("9.9.9"));

        // Verified, and still not ours. This is what a new computer looks like, and it is also what
        // a rolled-back server looks like; from here they are the same picture.
        Assert.Equal(ReconcileAction.Explain, outcome.Action);
        Assert.Null(outcome.Document);
    }

    [Fact]
    public async Task A_head_this_machine_has_moved_past_is_never_adopted()
    {
        var first = Publish(IndexBytes("1.0.0"), null);
        Publish(IndexBytes("1.0.0", "1.1.0"), first);
        _reader.Live = first; // the server serving back the publish before this one

        var outcome = await InspectAsync(IndexBytes("1.0.0", "1.1.0"));

        Assert.Equal(ReconcileAction.Explain, outcome.Action);
        Assert.Null(outcome.Document);
    }

    [Fact]
    public async Task An_unsettled_publish_stops_adoption_until_it_is_resolved()
    {
        Publish(IndexBytes("1.0.0"), null);
        _heads.WritePending(ClaimTrustContext.Compute(Anchor()), PluginId,
            _heads.TryLoad(ClaimTrustContext.Compute(Anchor()))!.Committed,
            new PendingPublish
            {
                Generation = 2, ManifestHash = new string('a', 64), IndexSha256 = new string('c', 64)
            }, [1, 2, 3]);

        var outcome = await InspectAsync(IndexBytes("1.0.0"));

        Assert.Equal(ReconcileAction.Explain, outcome.Action);
        Assert.Null(outcome.Document);
    }

    [Fact]
    public async Task Restored_state_stops_adoption_until_a_publish_confirms_it()
    {
        Publish(IndexBytes("1.0.0"), null);
        var mine = _heads.RecordsFor(PluginId).Single();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, "publisher"), "*.json"))
            if (Path.GetFileNameWithoutExtension(file).Length == 64) File.Delete(file);
        _heads.RestoreFromBackup(PluginId, [mine], null);

        var outcome = await InspectAsync(IndexBytes("1.0.0"));

        // The head matches exactly — which is precisely the situation a replayed server produces,
        // and the reason a restore has to be confirmed by a publish rather than by agreeing with
        // whatever is being served.
        Assert.Equal(ReconcileAction.Explain, outcome.Action);
        Assert.Null(outcome.Document);
    }

    [Fact]
    public async Task A_signed_catalog_with_no_way_to_read_it_is_reported_not_ignored()
    {
        Publish(IndexBytes("1.0.0"), null);

        var outcome = await InspectAsync(IndexBytes("1.0.0"), withoutAReader: true);

        Assert.Equal(ReconcileAction.Explain, outcome.Action);
        Assert.Null(outcome.Document);
    }

    // ---- what adoption actually writes ----

    [Fact]
    public async Task The_confirmed_publish_is_adopted()
    {
        Publish(IndexBytes("1.0.0", "1.1.0"), null);

        var outcome = await InspectAsync(IndexBytes("1.0.0"));

        Assert.Equal(ReconcileAction.Adopt, outcome.Action);
        Assert.Equal(1, outcome.Generation);
        Assert.Contains("1.1.0", Encoding.UTF8.GetString(outcome.Document!));
    }

    [Fact]
    public async Task What_is_adopted_comes_from_the_claims_and_not_from_the_file_around_them()
    {
        var published = Publish(IndexBytes("1.0.0"), null);

        // The plaintext beside a proof is not covered by the manifest, so the server can rewrite it
        // and every signature still verifies. A package URL and a matching hash, rewritten together,
        // is an install the manager's own hash gate would wave straight through — and this is how it
        // would reach the author: in their own folder, waiting to be published again under their key.
        _reader.Live = Tamper(published, root =>
        {
            root["releasesByGameId"]!["game1"]![0]!["packageUrl"] = "https://evil.example.com/p.zip";
            root["releasesByGameId"]!["game1"]![0]!["sha256"] = new string('f', 64);
        });

        var outcome = await InspectAsync(IndexBytes("1.0.0"));
        var document = Encoding.UTF8.GetString(outcome.Document!);

        Assert.Equal(ReconcileAction.Adopt, outcome.Action);
        Assert.DoesNotContain("evil.example.com", document);
        Assert.DoesNotContain(new string('f', 64), document);
    }

    [Fact]
    public async Task What_is_adopted_takes_the_authors_own_fields_from_the_folder_and_not_the_server()
    {
        // Published carrying the author's own values, then the server's copy is rewritten to carry
        // DIFFERENT ones. If the two agreed, this test could not tell which source they came from —
        // and the one that matters is the local one, because no claim will ever cover these fields
        // and a preset supplies a download URL and hash for a dependency that can run an installer.
        var published = Publish(IndexBytes(root => Author(root, "server", "1999-01-01T00:00:00Z"), "1.0.0"), null);
        _reader.Live = Tamper(published, root => Author(root, "server", "1999-01-01T00:00:00Z"));

        var local = IndexBytes(root => Author(root, "mine", "2030-01-01T00:00:00Z"), "1.0.0", "9.9.9");

        var outcome = await InspectAsync(local);
        var document = Encoding.UTF8.GetString(outcome.Document!);

        Assert.Equal(ReconcileAction.Adopt, outcome.Action);
        Assert.Contains("\"mine\"", document);
        Assert.Contains("2030-01-01", document);
        Assert.DoesNotContain("\"server\"", document);
        Assert.DoesNotContain("1999-01-01", document);
        Assert.DoesNotContain("\"proof\"", document);

        // ...while the catalog itself still came from the claims, not from the folder.
        Assert.DoesNotContain("9.9.9", document);
    }

    private static void Author(JsonObject root, string id, string generatedAt)
    {
        root["generatedAt"] = generatedAt;
        root["dependencyPresets"] = new JsonArray(new JsonObject
        {
            ["id"] = id,
            ["displayName"] = id,
            ["dependency"] = new JsonObject
            {
                ["id"] = id, ["type"] = "framework", ["downloadUrl"] = "https://example.com/dep.zip"
            }
        });
    }

    // ---- unpublished work is the one thing here that exists nowhere else ----

    [Fact]
    public async Task Work_this_folder_never_published_is_not_replaced_without_asking()
    {
        // The sequence that loses a release, exactly: publish 1.0.0, add 1.1.0, save, close, reopen.
        // The marker still names the 1.0.0 file that WAS published — it is present and it disagrees
        // with the folder, which is the whole signal. The server head still matches this machine's
        // record, so every trust check passes and adoption would otherwise be silent, and 1.1.0
        // exists in exactly one place in the world.
        var published = IndexBytes("1.0.0");
        Publish(published, null);

        var outcome = await InspectAsync(IndexBytes("1.0.0", "1.1.0"), publishedLocal: published);

        Assert.Equal(ReconcileAction.AdoptWithConsent, outcome.Action);
        Assert.NotNull(outcome.Message);
        Assert.NotNull(outcome.Document);
    }

    [Fact]
    public async Task A_folder_that_has_never_published_anything_is_not_replaced_without_asking_either()
    {
        // No marker at all: nothing here can say whether this folder holds work or is simply out of
        // date, and the answer that loses nothing is to ask.
        Publish(IndexBytes("1.0.0"), null);

        var outcome = await InspectAsync(IndexBytes("1.0.0", "1.1.0"), noMarker: true);

        Assert.Equal(ReconcileAction.AdoptWithConsent, outcome.Action);
    }

    [Fact]
    public async Task A_folder_that_is_only_stale_is_replaced_without_a_question()
    {
        Publish(IndexBytes("1.0.0", "1.1.0"), null);

        var outcome = await InspectAsync(IndexBytes("1.0.0"));

        Assert.Equal(ReconcileAction.Adopt, outcome.Action);
    }

    [Fact]
    public async Task Adoption_settles_and_then_leaves_the_folder_alone_quietly()
    {
        var local = IndexBytes("1.0.0");
        Publish(local, null);

        // The first open still adopts: the folder says the same things, but not in the form the
        // claims serialize to, and "the same but for whitespace and absent nulls" is not something
        // to decide by guessing. So it is normalised once...
        var first = await InspectAsync(local);
        Assert.Equal(ReconcileAction.Adopt, first.Action);

        // ...and every open after that has nothing to do. Rewriting a file to its own contents is a
        // modification time nobody asked for, and announcing it each time is noise that makes the
        // messages which do matter easier to miss.
        var second = await InspectAsync(first.Document!);

        Assert.Equal(ReconcileAction.Nothing, second.Action);
        Assert.Null(second.Message);
    }

    [Fact]
    public async Task A_failed_read_never_hides_an_interrupted_publish()
    {
        Publish(IndexBytes("1.0.0"), null);
        var context = ClaimTrustContext.Compute(Anchor());
        _heads.WritePending(context, PluginId, _heads.TryLoad(context)!.Committed,
            new PendingPublish
            {
                Generation = 2, ManifestHash = new string('a', 64), IndexSha256 = new string('c', 64)
            }, [1, 2, 3]);
        _reader.Throws = true;

        var outcome = await InspectAsync(IndexBytes("1.0.0"));

        // The blocker is this machine's own and the author has to act on it. A dropped connection
        // must not be able to turn it into silence.
        Assert.Equal(ReconcileAction.Explain, outcome.Action);
        Assert.Contains("interrupted", outcome.Message!);
        Assert.Equal(0, _reader.Reads);
    }

    [Fact]
    public async Task A_failed_read_never_hides_restored_state_either()
    {
        Publish(IndexBytes("1.0.0"), null);
        var mine = _heads.RecordsFor(PluginId).Single();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, "publisher"), "*.json"))
            if (Path.GetFileNameWithoutExtension(file).Length == 64) File.Delete(file);
        _heads.RestoreFromBackup(PluginId, [mine], null);
        _reader.Throws = true;

        var outcome = await InspectAsync(IndexBytes("1.0.0"));

        Assert.Equal(ReconcileAction.Explain, outcome.Action);
        Assert.Contains("backup", outcome.Message!);
        Assert.Equal(0, _reader.Reads);
    }

    [Fact]
    public async Task A_failed_read_is_reported_rather_than_shrugged_off()
    {
        Publish(IndexBytes("1.0.0"), null);
        _reader.Throws = true;

        var outcome = await InspectAsync(IndexBytes("1.0.0"));

        Assert.Equal(ReconcileAction.Explain, outcome.Action);
        Assert.Null(outcome.Document);
    }

    // ---- helpers ----

    private static byte[] Tamper(byte[] indexJson, Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(indexJson)!.AsObject();
        edit(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private sealed class FakeReader : IPublishedIndexReader
    {
        public byte[]? Live;
        public bool Throws;
        public int Reads;

        public Task<ServerUploadService.RemoteIndex> ReadIndexAsync(string pluginId, CancellationToken ct)
        {
            Reads++;
            if (Throws) throw new IOException("the read failed");

            return Task.FromResult(new ServerUploadService.RemoteIndex(Live is not null, Live));
        }
    }

    private sealed class FakeRegistry : IVerifiedRegistrySource
    {
        public string Json = "";
        public RegistryUnusableException? Fail;

        public Task<string> ReadVerifiedAsync(string pluginId, CancellationToken ct) =>
            Fail is not null ? throw Fail : Task.FromResult(Json);
    }
}
