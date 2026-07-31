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
/// The publish state machine, driven through every boundary it can fail at.
///
/// <para>The sharpest assertion in here is about the journal: it may be dropped only when the switch
/// was provably never reached. Getting that backwards deletes the only record that this machine may
/// already have published, and the next attempt then signs a second version of the same publish —
/// which is indistinguishable, to everyone downstream, from the author lying.</para>
/// </summary>
public sealed class IndexPublishCoordinatorTests : IDisposable
{
    private const string PluginId = "amethyst";
    private const string IndexUrl = "https://accessibilitymods.com/registry/plugins/amethyst/index.json";

    private readonly string _root;
    private readonly AuthorConfigService _config;
    private readonly PublisherHeadStore _heads;
    private readonly ClaimSigningKeyStore _keys;
    private readonly IndexProofService _proofs;
    private readonly ClaimSigningConfig _signing;
    private readonly IndexPublishCoordinator _coordinator;

    private readonly FakeTransport _transport = new();
    private readonly FakeRegistry _registry;
    private readonly List<string> _events = [];
    private readonly Dictionary<PublishQuestion, bool> _answers = [];

    public IndexPublishCoordinatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "publishco-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);

        _config = new AuthorConfigService(TestLogger.Create(), _root);
        _heads = new PublisherHeadStore(_config, TestLogger.Create());
        _keys = new ClaimSigningKeyStore(_config, _heads, TestLogger.Create());
        _signing = _keys.Create(PluginId, "pp");
        _proofs = new IndexProofService(_keys, _heads, TestLogger.Create());
        _coordinator = new IndexPublishCoordinator(_keys, _heads, _proofs, TestLogger.Create());

        _transport.Events = _events;
        _registry = new FakeRegistry { Events = _events, Json = Registry() };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    // ---- the world ----

    private string Registry(int version = 1, string? indexUrl = null, string? publicKeyPem = null) => $$"""
        {
          "registryVersion": "{{version}}",
          "plugins": [
            { "id": "amethyst", "repoIndexUrl": "{{indexUrl ?? IndexUrl}}",
              "indexTrust": {
                "scheme": "signed-claims-v1",
                "keyId": "{{_signing.KeyId}}",
                "algorithm": "rsa-pss-sha256",
                "publicKeyPem": {{JsonSerializer.Serialize(publicKeyPem ?? _signing.PublicKeyPem)}}
              } }
          ]
        }
        """;

    /// <summary>A registry entry with no signing key at all — every catalog's state before today.</summary>
    private static string UnsignedRegistry() => $$"""
        { "registryVersion": "1",
          "plugins": [ { "id": "amethyst", "repoIndexUrl": "{{IndexUrl}}" } ] }
        """;

    private ClaimTrustAnchor Anchor(string? url = null) => new()
    {
        PluginId = PluginId,
        RepoIndexUrl = url ?? IndexUrl,
        Scheme = ClaimTrustAnchor.SchemeV1,
        KeyId = _signing.KeyId,
        Algorithm = ClaimTrustAnchor.AlgorithmRsaPssSha256,
        PublicKeyPem = _signing.PublicKeyPem
    };

    private static byte[] IndexBytes(params string[] versions)
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

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(index, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    // ---- driving it ----

    private Task<PublishResult> PublishAsync(byte[] candidate, bool confirmOrdinary = true) =>
        _coordinator.PublishAsync(_transport, _registry,
            new PublishRequest(PluginId, candidate) { ConfirmOrdinary = confirmOrdinary },
            Answer, CancellationToken.None);

    /// <summary>
    /// Answers a question, and checks the invariant that makes the whole ordering worth having:
    /// nothing is asked once a publish has been journalled.
    ///
    /// <para>Asserted here rather than in one test, so it holds for every path through the state
    /// machine — and asserted against the journal itself rather than against the upload that follows
    /// it, so moving a confirmation to the wrong side of <c>PreparePublish</c> is caught even though
    /// the events would still look the same.</para>
    ///
    /// <para>Resuming is the one question asked with a journal on disk: that journal belongs to an
    /// earlier attempt whose fate is exactly what is being settled, and there is nothing to spoil.</para>
    /// </summary>
    private bool Answer(PublishConfirmation question)
    {
        _events.Add("confirm:" + question.Question);

        if (question.Question != PublishQuestion.ResumeInterrupted)
            Assert.Null(Pending());

        return _answers.TryGetValue(question.Question, out var answer) ? answer : true;
    }

    /// <summary>Gets a signed catalog live, the way the author would: bootstrap, confirmed.</summary>
    private async Task<byte[]> BootstrapAsync(byte[]? candidate = null)
    {
        var result = await PublishAsync(candidate ?? IndexBytes("1.0.0"));
        Assert.Equal(PublishStatus.Published, result.Status);
        _events.Clear();
        return _transport.Live!;
    }

    private IReadOnlyList<PublisherRecord> Records() => _heads.RecordsFor(PluginId);

    private PendingPublish? Pending() => Records().FirstOrDefault(r => r.Pending is not null)?.Pending;

    // ---- the ordinary path ----

    [Fact]
    public async Task The_first_signed_publish_runs_in_the_order_the_design_requires()
    {
        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Published, result.Status);
        Assert.Equal(1, result.Generation);
        Assert.True(result.StartedHistory);

        Assert.Equal(
        [
            "registry",                       // preflight: better errors, no authority
            "lock",
            "registry",                       // the authoritative read, under the lock
            "read",                           // what is live, over SFTP
            "confirm:StartSignedHistory",     // every question, before the journal
            "upload",                         // journal is written immediately before this
            "registry",                       // the last look, between staging and the switch
            "rename",
            "read",                           // read back what actually landed
            "unlock"
        ], _events);
    }

    [Fact]
    public async Task Nothing_is_confirmed_once_the_journal_has_been_written()
    {
        // Every confirmation in every test goes through Answer, which refuses to answer one asked
        // with a journal already on disk. This one pins that questions were asked at all, so the
        // invariant is not being satisfied by a path that simply never asks anything.
        await PublishAsync(IndexBytes("1.0.0"));

        Assert.Contains("confirm:StartSignedHistory", _events);
        Assert.True(_events.IndexOf("confirm:StartSignedHistory") < _events.IndexOf("upload"));
    }

    [Fact]
    public async Task Nothing_after_the_journal_can_be_cancelled()
    {
        using var cancellable = new CancellationTokenSource();

        await _coordinator.PublishAsync(_transport, _registry,
            new PublishRequest(PluginId, IndexBytes("1.0.0")), Answer, cancellable.Token);

        // The caller's token reaches the part where stopping is still free, and nothing beyond it.
        // Giving up between the switch and the commit is not one of the available answers: it would
        // leave a publish that may be live with nothing on this machine that knows.
        Assert.True(_transport.ReadTokensCancellable[0]);
        Assert.False(_transport.PublishTokenCancellable);
        Assert.False(_transport.ReadTokensCancellable[^1]);
    }

    [Fact]
    public async Task A_publish_that_lands_leaves_no_unfinished_attempt_behind()
    {
        await PublishAsync(IndexBytes("1.0.0"));

        Assert.Null(Pending());
        Assert.Equal(1, Records().Single().Committed!.Generation);
    }

    [Fact]
    public async Task The_second_publish_continues_the_history()
    {
        await BootstrapAsync();

        var result = await PublishAsync(IndexBytes("1.0.0", "1.1.0"));

        Assert.Equal(PublishStatus.Published, result.Status);
        Assert.Equal(2, result.Generation);
        Assert.False(result.StartedHistory);
    }

    // ---- the journal-discard rule ----

    [Fact]
    public async Task A_failure_before_the_switch_drops_the_journal()
    {
        _transport.FailUpload = new IOException("the connection went away mid-upload");

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Refused, result.Status);
        Assert.Null(Pending());
        Assert.DoesNotContain("rename", _events);
        Assert.Null(_transport.Live);
    }

    [Fact]
    public async Task A_failure_that_may_have_switched_keeps_the_journal()
    {
        _transport.FailRename = new IOException("the connection went away mid-rename");

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Interrupted, result.Status);
        Assert.Equal(1, Pending()!.Generation);
    }

    [Fact]
    public async Task An_unexplained_transport_failure_keeps_the_journal()
    {
        // Not the typed exception, so nothing here knows whether the switch ran. The safe reading is
        // the one that costs a recovery pass rather than the one that loses the evidence.
        _transport.FailUploadUntyped = new InvalidOperationException("something nobody modelled");

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Interrupted, result.Status);
        Assert.Equal(1, Pending()!.Generation);
    }

    [Fact]
    public async Task A_failed_read_back_keeps_the_journal()
    {
        _transport.FailReadAfter = 2; // the readback, not the pre-publish read

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Interrupted, result.Status);
        Assert.Equal(1, Pending()!.Generation);
    }

    [Fact]
    public async Task An_index_that_comes_back_changed_keeps_the_journal()
    {
        _transport.RewriteAfterRename = sent => Tamper(sent, root => root["generatedAt"] = "2020-01-01T00:00:00Z");

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Interrupted, result.Status);
        Assert.Equal(1, Pending()!.Generation);
    }

    // ---- the registry moving under the publish ----

    [Fact]
    public async Task A_registry_that_changes_during_the_upload_never_reaches_the_switch()
    {
        // Counted from this attempt's own first read, not from the process start: the third read of
        // THIS publish is the one made between staging and the rename. By then the registry names a
        // different address, so the claims already signed are bound to something retired.
        _registry.ChangeFrom(3, Registry(version: 2, indexUrl: IndexUrl + "?v=2"));

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Refused, result.Status);
        Assert.Contains("upload", _events);           // it got as far as staging the file...
        Assert.DoesNotContain("rename", _events);     // ...and no further
        Assert.Null(_transport.Live);
        Assert.Null(Pending());
    }

    [Fact]
    public async Task A_registry_rolled_back_during_the_upload_never_reaches_the_switch()
    {
        await BootstrapAsync();
        _registry.Json = Registry(version: 3);
        await PublishAsync(IndexBytes("1.0.0", "1.1.0"));
        _events.Clear();

        // Version 3 has been acted on. A perfectly signed version 2 arriving now is a replay.
        _registry.ChangeFrom(3, Registry(version: 2));

        var result = await PublishAsync(IndexBytes("1.0.0", "1.1.0", "1.2.0"));

        Assert.Equal(PublishStatus.Refused, result.Status);
        Assert.Contains("upload", _events);
        Assert.DoesNotContain("rename", _events);
        Assert.Null(Pending());
    }

    // ---- a failed read is never an absence ----

    [Fact]
    public async Task A_failed_live_read_never_becomes_a_first_publish()
    {
        _transport.FailReadAfter = 1;

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Refused, result.Status);
        Assert.DoesNotContain("confirm:StartSignedHistory", _events);
        Assert.DoesNotContain("upload", _events);
    }

    [Fact]
    public async Task A_signed_catalog_that_has_lost_its_proof_is_never_republished_over()
    {
        await BootstrapAsync();
        _transport.Live = IndexBytes("1.0.0"); // the proof stripped off by whoever holds the file

        var result = await PublishAsync(IndexBytes("1.0.0", "1.1.0"));

        Assert.Equal(PublishStatus.RecoveryRequired, result.Status);
        Assert.DoesNotContain("upload", _events);
    }

    [Fact]
    public async Task A_signed_catalog_this_machine_did_not_publish_is_never_extended()
    {
        await BootstrapAsync();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, "publisher"), "*.json"))
            if (Path.GetFileNameWithoutExtension(file).Length == 64) File.Delete(file);

        var result = await PublishAsync(IndexBytes("1.0.0", "1.1.0"));

        Assert.Equal(PublishStatus.RecoveryRequired, result.Status);
        Assert.DoesNotContain("upload", _events);
    }

    // ---- recovering an interrupted publish ----

    [Fact]
    public async Task An_interrupted_publish_that_did_land_is_recorded_rather_than_repeated()
    {
        _transport.FailRename = new IOException("lost the reply");
        _transport.RenameLandsAnyway = true;
        await PublishAsync(IndexBytes("1.0.0"));
        _events.Clear();

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Recovered, result.Status);
        Assert.Null(Pending());
        Assert.Equal(1, Records().Single().Committed!.Generation);
        Assert.DoesNotContain("upload", _events);

        // Finding out that the first publish landed starts the history, and the backup taken before
        // it can no longer recover what it was taken for.
        Assert.True(result.StartedHistory);
    }

    [Fact]
    public async Task Recovering_a_later_publish_does_not_claim_to_have_started_the_history()
    {
        await BootstrapAsync();
        _transport.FailRename = new IOException("lost the reply");
        _transport.RenameLandsAnyway = true;
        await PublishAsync(IndexBytes("1.0.0", "1.1.0"));
        _events.Clear();

        var result = await PublishAsync(IndexBytes("1.0.0", "1.1.0"));

        Assert.Equal(PublishStatus.Recovered, result.Status);
        Assert.Equal(2, result.Generation);
        Assert.False(result.StartedHistory);
    }

    [Fact]
    public async Task An_interrupted_publish_that_never_landed_is_resent_byte_for_byte()
    {
        _transport.FailRename = new IOException("lost the connection");
        await PublishAsync(IndexBytes("1.0.0"));
        var prepared = _transport.LastSent!;
        _events.Clear();
        _transport.FailRename = null;

        // Deliberately a DIFFERENT local index: a resume sends what was signed, never what is on
        // disk now. Rebuilding at the same generation would sign different bytes under a number that
        // may already be published.
        var result = await PublishAsync(IndexBytes("1.0.0", "9.9.9"));

        Assert.Equal(PublishStatus.Published, result.Status);
        Assert.Equal(prepared, _transport.LastSent);
        Assert.Contains("confirm:ResumeInterrupted", _events);
    }

    // ---- which publishes may be recorded as putting THIS folder live ----
    //
    // A publish succeeding and the local file being published are different facts, and the gap
    // between them is where work gets destroyed. The caller records the local bytes as published,
    // and a later project-open reads that mark to tell "this folder is stale" (replaceable) apart
    // from "this folder holds work nobody has published" (ask first). Claiming it wrongly turns the
    // second into the first.

    [Fact]
    public async Task A_publish_of_the_local_file_reports_that_it_is_what_went_live()
    {
        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Published, result.Status);
        Assert.True(result.LocalSourceIsLive);
    }

    [Fact]
    public async Task A_resend_never_reports_the_local_file_as_the_one_that_went_live()
    {
        _transport.FailRename = new IOException("lost the connection");
        await PublishAsync(IndexBytes("1.0.0"));
        _events.Clear();
        _transport.FailRename = null;

        // 9.9.9 exists only in this folder. The resend sends generation 1's signed bytes, which
        // never mentioned it — so the publish succeeds and 9.9.9 is still unpublished work. A caller
        // that recorded this candidate as published would be arming the next project-open to
        // overwrite it without asking.
        var result = await PublishAsync(IndexBytes("1.0.0", "9.9.9"));

        Assert.Equal(PublishStatus.Published, result.Status);
        Assert.False(result.LocalSourceIsLive);
    }

    [Fact]
    public async Task Discovering_an_interrupted_publish_landed_says_nothing_about_the_local_file()
    {
        _transport.FailRename = new IOException("lost the reply");
        _transport.RenameLandsAnyway = true;
        await PublishAsync(IndexBytes("1.0.0"));
        _events.Clear();

        var result = await PublishAsync(IndexBytes("1.0.0", "9.9.9"));

        Assert.Equal(PublishStatus.Recovered, result.Status);
        Assert.False(result.LocalSourceIsLive);
    }

    [Fact]
    public async Task A_catalog_that_already_says_what_the_folder_says_reports_exactly_that()
    {
        await BootstrapAsync();

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.AlreadyUpToDate, result.Status);
        Assert.True(result.LocalSourceIsLive);
    }

    [Fact]
    public async Task The_unsigned_path_is_handed_the_registry_that_sent_it_there()
    {
        // The unsigned path has its own address check to run, and used to run it against whatever
        // a fresh fetch returned — treating a signature that did not verify as nothing to compare.
        // Carrying the verified document across means it can only ever check against bytes the
        // registry key vouched for.
        _registry.Json = UnsignedRegistry();

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.NotSigned, result.Status);
        Assert.Equal(UnsignedRegistry(), result.VerifiedRegistryJson);
    }

    [Fact]
    public async Task Declining_the_resume_blocks_every_new_publish()
    {
        _transport.FailRename = new IOException("lost the connection");
        await PublishAsync(IndexBytes("1.0.0"));
        _events.Clear();
        _transport.FailRename = null;
        _answers[PublishQuestion.ResumeInterrupted] = false;

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.RecoveryRequired, result.Status);
        Assert.DoesNotContain("upload", _events);
        Assert.Equal(1, Pending()!.Generation);
    }

    [Fact]
    public async Task A_resend_that_fails_before_the_switch_still_keeps_the_journal()
    {
        _transport.FailRename = new IOException("lost the connection");
        await PublishAsync(IndexBytes("1.0.0"));
        _transport.FailRename = null;
        _transport.FailUpload = new IOException("and again");
        _events.Clear();

        var result = await PublishAsync(IndexBytes("1.0.0"));

        // Dropping it would be defensible — the switch provably did not run — but it would throw
        // away the only copy of bytes that can never be rebuilt at this generation.
        Assert.Equal(PublishStatus.Interrupted, result.Status);
        Assert.Equal(1, Pending()!.Generation);
    }

    [Fact]
    public async Task A_history_that_is_neither_the_attempt_nor_its_parent_stops_recovery()
    {
        _transport.FailRename = new IOException("lost the connection");
        await PublishAsync(IndexBytes("1.0.0"));
        _transport.FailRename = null;

        // The proof still verifies and names the same publish, but the plaintext beside it — which
        // the manifest does not cover — is not what went out.
        _transport.Live = Tamper(_transport.LastSent!, root => root["generatedAt"] = "1999-01-01T00:00:00Z");
        _events.Clear();

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.RecoveryRequired, result.Status);
        Assert.DoesNotContain("upload", _events);
        Assert.Equal(1, Pending()!.Generation);
    }

    // ---- unsettled attempts are scanned across every trust context ----

    [Fact]
    public async Task An_unfinished_publish_under_a_retired_registry_blocks_publishing()
    {
        // The same plugin, published under an address the registry has since moved away from. Asking
        // only about the current context would step straight over it.
        var retired = ClaimTrustContext.Compute(Anchor(IndexUrl + "?old=1"));
        _heads.WritePending(retired, PluginId, null,
            new PendingPublish { Generation = 4, ManifestHash = new string('a', 64), IndexSha256 = new string('c', 64) },
            [1, 2, 3]);

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.RecoveryRequired, result.Status);
        Assert.DoesNotContain("upload", _events);
    }

    [Fact]
    public async Task More_than_one_unfinished_publish_blocks_publishing()
    {
        foreach (var suffix in new[] { "?a=1", "?b=2" })
        {
            _heads.WritePending(ClaimTrustContext.Compute(Anchor(IndexUrl + suffix)), PluginId, null,
                new PendingPublish { Generation = 4, ManifestHash = new string('a', 64), IndexSha256 = new string('c', 64) },
                [1, 2, 3]);
        }

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.RecoveryRequired, result.Status);
        Assert.DoesNotContain("upload", _events);
    }

    // ---- the anchor decides, not the key ----

    [Theory]
    [InlineData("null")]
    [InlineData("\"signed-claims-v1\"")]
    [InlineData("[]")]
    [InlineData("""{ "scheme": "signed-claims-v1" }""")]
    [InlineData("""{ "scheme": "signed-claims-v99", "keyId": "k", "algorithm": "rsa-pss-sha256", "publicKeyPem": "x" }""")]
    public async Task An_entry_that_names_an_unusable_key_is_refused_rather_than_published_unsigned(string trust)
    {
        // The defect this replaced. A malformed indexTrust used to read as "no anchor", and the ONLY
        // thing between that and publishing plaintext over a signed catalog was this machine holding
        // publishing records — so on a replacement machine, or a first publish, it went through.
        // Nothing is written down here on purpose: no records, exactly the empty state the old check
        // was consulting.
        _registry.Json = $$"""
            { "registryVersion": "1",
              "plugins": [ { "id": "amethyst", "repoIndexUrl": "{{IndexUrl}}", "indexTrust": {{trust}} } ] }
            """;

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Refused, result.Status);
        Assert.NotEqual(PublishStatus.NotSigned, result.Status);
        Assert.DoesNotContain("upload", _events);
    }

    [Fact]
    public async Task An_unanchored_entry_cannot_shadow_an_anchored_one_of_the_same_id()
    {
        // The registry lists 'amethyst' twice: the first entry names no key, the second names the
        // real one. A reader that stopped at the first match would answer "no anchor" — permission
        // to publish plaintext over a signed catalog — letting whoever writes the registry choose
        // the answer by ordering. Again with NO publishing records, since that is the only thing
        // that used to stand in the way.
        _registry.Json = $$"""
            {
              "registryVersion": "1",
              "plugins": [
                { "id": "amethyst", "repoIndexUrl": "{{IndexUrl}}" },
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

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Refused, result.Status);
        Assert.NotEqual(PublishStatus.NotSigned, result.Status);
        Assert.DoesNotContain("upload", _events);
    }

    [Fact]
    public async Task A_plugin_the_registry_anchors_no_key_for_is_left_to_the_unsigned_path()
    {
        _registry.Json = UnsignedRegistry();

        var result = await PublishAsync(IndexBytes("1.0.0"));

        // A key exists on this machine and it changes nothing. Creating one must never be the act
        // that breaks publishing.
        Assert.Equal(PublishStatus.NotSigned, result.Status);
        Assert.DoesNotContain("lock", _events);
    }

    [Fact]
    public async Task An_anchor_that_disappeared_after_a_signed_publish_stops_everything()
    {
        await BootstrapAsync();
        _registry.Json = UnsignedRegistry();

        var result = await PublishAsync(IndexBytes("1.0.0", "1.1.0"));

        Assert.Equal(PublishStatus.RecoveryRequired, result.Status);
        Assert.DoesNotContain("lock", _events);
    }

    [Fact]
    public async Task A_registry_that_will_not_verify_stops_publishing_whatever_else_is_true()
    {
        _registry.Fail = new RegistryUnusableException("the signature didn't verify");

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Refused, result.Status);
        Assert.DoesNotContain("lock", _events);
    }

    [Fact]
    public async Task An_unreachable_registry_stops_a_catalog_that_has_signing_state_here()
    {
        _registry.Fail = new RegistryUnusableException("no route to host");

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Refused, result.Status);
        Assert.DoesNotContain("lock", _events);
    }

    [Fact]
    public async Task An_unreachable_registry_stops_a_machine_that_holds_no_key_either()
    {
        // The machine that has the project and the server credentials but has not imported the key
        // yet — a fresh install, a restored profile, the laptop. With the registry readable it is
        // told it cannot sign for this catalog and stops. It must reach the same conclusion when
        // someone drops the request instead, or blocking one fetch is enough to make it publish
        // plaintext over a signed catalog. Local absence of a key proves nothing about the registry.
        var elsewhere = Path.Combine(_root, "other");
        var otherConfig = new AuthorConfigService(TestLogger.Create(), elsewhere);
        var clean = new IndexPublishCoordinator(
            new ClaimSigningKeyStore(otherConfig, _heads, TestLogger.Create()),
            new PublisherHeadStore(otherConfig, TestLogger.Create()),
            _proofs, TestLogger.Create());
        _registry.Fail = new RegistryUnusableException("no route to host");

        var result = await clean.PublishAsync(_transport, _registry,
            new PublishRequest(PluginId, IndexBytes("1.0.0")), Answer, CancellationToken.None);

        Assert.Equal(PublishStatus.Refused, result.Status);
        Assert.DoesNotContain("lock", _events);
    }

    [Fact]
    public async Task Only_a_verified_registry_that_names_no_key_selects_the_unsigned_path()
    {
        // The single route to the unsigned path: the registry was read, its signature verified, and
        // it carries no indexTrust for this plugin.
        _registry.Json = UnsignedRegistry();

        Assert.Equal(PublishStatus.NotSigned, (await PublishAsync(IndexBytes("1.0.0"))).Status);

        _registry.Fail = new RegistryUnusableException("no route to host");

        Assert.Equal(PublishStatus.Refused, (await PublishAsync(IndexBytes("1.0.0"))).Status);
    }

    [Fact]
    public async Task An_anchor_naming_a_key_this_machine_lacks_is_a_hard_stop()
    {
        _registry.Json = Registry(publicKeyPem: ClaimTestKeys.Secondary.ExportSubjectPublicKeyInfoPem());

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.SigningKeyMissing, result.Status);
        Assert.DoesNotContain("lock", _events);
    }

    [Fact]
    public async Task Publishing_to_an_address_the_registry_does_not_name_is_refused()
    {
        _registry.Json = Registry(indexUrl: "https://accessibilitymods.com/registry/plugins/Amethyst/index.json");

        var result = await PublishAsync(IndexBytes("1.0.0"));

        // The path case differs, and the catalog is served off a Linux filesystem.
        Assert.Equal(PublishStatus.Refused, result.Status);
        Assert.DoesNotContain("lock", _events);
    }

    // ---- idempotence, without swallowing the publish that starts signing ----

    [Fact]
    public async Task An_unsigned_live_index_identical_to_the_local_one_still_publishes()
    {
        // The blocker this whole rewrite exists for: the old byte-equality early return would have
        // reported success and never created a proof at all.
        var candidate = IndexBytes("1.0.0");
        _transport.Live = candidate;

        var result = await PublishAsync(candidate);

        Assert.Equal(PublishStatus.Published, result.Status);
        Assert.True(result.StartedHistory);
    }

    [Fact]
    public async Task Publishing_an_unchanged_signed_catalog_sends_nothing()
    {
        var candidate = IndexBytes("1.0.0");
        await BootstrapAsync(candidate);

        var result = await PublishAsync(candidate);

        Assert.Equal(PublishStatus.AlreadyUpToDate, result.Status);
        Assert.DoesNotContain("upload", _events);
        Assert.Equal(1, result.Generation);
    }

    // ---- what a publish costs is agreed before it is signed ----

    [Fact]
    public async Task A_withdrawal_is_confirmed_before_anything_is_signed_and_no_stops_it()
    {
        await BootstrapAsync(IndexBytes("1.0.0", "1.1.0"));
        _answers[PublishQuestion.PermanentDeletion] = false;

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Cancelled, result.Status);
        Assert.Equal("confirm:PermanentDeletion", _events.Last(e => e.StartsWith("confirm:", StringComparison.Ordinal)));
        Assert.DoesNotContain("upload", _events);
        Assert.Null(Pending());
    }

    [Fact]
    public async Task Saying_no_to_starting_the_history_uploads_nothing()
    {
        _answers[PublishQuestion.StartSignedHistory] = false;

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.Cancelled, result.Status);
        Assert.DoesNotContain("upload", _events);
        Assert.Empty(Records());
    }

    [Fact]
    public async Task The_ordinary_confirmation_can_be_left_to_the_caller_but_a_withdrawal_cannot()
    {
        await BootstrapAsync(IndexBytes("1.0.0", "1.1.0"));

        await PublishAsync(IndexBytes("1.0.0"), confirmOrdinary: false);

        Assert.Contains("confirm:PermanentDeletion", _events);
        Assert.DoesNotContain("confirm:Ordinary", _events);
    }

    // ---- a restore is only settled by a publish that worked ----

    [Fact]
    public async Task An_abandoned_attempt_does_not_settle_where_this_machine_thinks_it_is()
    {
        RestoreOverOwnHistory();
        _transport.FailUpload = new IOException("the connection went away mid-upload");
        Assert.Equal(PublishStatus.Refused, (await PublishAsync(IndexBytes("1.0.0", "1.1.0"))).Status);
        Assert.Contains("confirm:RestoredState", _events);
        _events.Clear();
        _transport.FailUpload = null;

        await PublishAsync(IndexBytes("1.0.0", "1.1.0"));

        // The attempt was journalled and then abandoned; nothing about it proved the restored head
        // was the latest one, so the question is still open and is asked again.
        Assert.Contains("confirm:RestoredState", _events);
    }

    [Fact]
    public async Task A_publish_that_worked_settles_it_for_good()
    {
        RestoreOverOwnHistory();
        Assert.Equal(PublishStatus.Published, (await PublishAsync(IndexBytes("1.0.0", "1.1.0"))).Status);
        _events.Clear();

        await PublishAsync(IndexBytes("1.0.0", "1.1.0", "1.2.0"));

        Assert.DoesNotContain("confirm:RestoredState", _events);
    }

    [Fact]
    public async Task A_retired_address_does_not_keep_asking_once_the_live_one_is_settled()
    {
        RestoreOverOwnHistory();

        // A backup carries every context's record and a restore marks them all, so a plugin that
        // has ever been re-pointed or re-keyed arrives carrying doubt about an address nothing will
        // ever publish to again. That doubt is unanswerable by construction, and gating the live
        // address on it would put a question nobody can settle in front of every publish — which is
        // how a security prompt becomes something the author clicks through without reading.
        var retired = ClaimTrustContext.Compute(Anchor(IndexUrl + "?retired=1"));
        _heads.RestoreFromBackup(PluginId,
            [new PublisherRecord
            {
                TrustContext = retired,
                PluginId = PluginId,
                Committed = new PublisherHead { Generation = 9, ManifestHash = new string('e', 64) }
            }], null);

        Assert.Equal(PublishStatus.Published, (await PublishAsync(IndexBytes("1.0.0", "1.1.0"))).Status);
        _events.Clear();

        await PublishAsync(IndexBytes("1.0.0", "1.1.0", "1.2.0"));

        Assert.DoesNotContain("confirm:RestoredState", _events);
    }

    [Fact]
    public void A_restore_is_still_questioned_at_an_address_this_machine_has_no_history_for()
    {
        // The hole the plugin-wide check was introduced to close, and it stays closed: restore a
        // backup taken while publishing at one address, let an authentic re-point move the catalog
        // to a second, and the doubt follows the key rather than staying behind with the address.
        RestoreOverOwnHistory();
        var elsewhere = ClaimTrustContext.Compute(Anchor(IndexUrl + "?moved=1"));

        Assert.True(_heads.HasUnconfirmedRestoredState(PluginId, elsewhere));
    }

    /// <summary>
    /// Puts this machine in the state a key backup leaves it in: a head that is authentic, and that
    /// nothing local can prove is current.
    /// </summary>
    private void RestoreOverOwnHistory()
    {
        BootstrapAsync().GetAwaiter().GetResult();
        var mine = Records().Single();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, "publisher"), "*.json"))
            if (Path.GetFileNameWithoutExtension(file).Length == 64) File.Delete(file);

        _heads.RestoreFromBackup(PluginId, [mine], null);
        Assert.True(_heads.HasUnconfirmedRestoredState(PluginId, ClaimTrustContext.Compute(Anchor())));
    }

    // ---- the lock ----

    [Fact]
    public async Task A_lock_somebody_else_holds_stops_the_publish()
    {
        _transport.LockHeldBy = "Ola on COMPUTER";

        var result = await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal(PublishStatus.LockHeld, result.Status);
        Assert.Contains("Ola on COMPUTER", result.Message);
        Assert.DoesNotContain("upload", _events);
    }

    [Fact]
    public async Task The_lock_is_given_back_even_when_the_publish_fails()
    {
        _transport.FailRename = new IOException("lost the connection");

        await PublishAsync(IndexBytes("1.0.0"));

        Assert.Equal("unlock", _events.Last());
    }

    // ---- helpers ----

    private static byte[] Tamper(byte[] indexJson, Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(indexJson)!.AsObject();
        edit(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// The server, with a failure switch at every boundary the real one has.
    ///
    /// <para>It mirrors <see cref="ServerUploadService.PublishIndexAsync"/>'s contract rather than
    /// its transport: upload, then the pre-switch callback, then the rename — and every failure
    /// reported as <see cref="IndexPublishFailedException"/> carrying whether the rename was
    /// reached. No SFTP call in this codebase has ever run against a real server, which is disclosed
    /// rather than papered over: what is verified here is the state machine, not SSH.NET.</para>
    /// </summary>
    private sealed class FakeTransport : IPublishTransport
    {
        public List<string> Events = [];
        public byte[]? Live;
        public byte[]? LastSent;
        public string? LockHeldBy;
        public Exception? FailUpload;
        public Exception? FailUploadUntyped;
        public Exception? FailRename;

        /// <summary>Makes the Nth read throw, counting from one.</summary>
        public int? FailReadAfter;

        /// <summary>True when a failing rename still changed the file, as a lost reply would.</summary>
        public bool RenameLandsAnyway;

        /// <summary>Rewrites what the server serves after a successful rename.</summary>
        public Func<byte[], byte[]>? RewriteAfterRename;

        private int _reads;

        public Task<ServerUploadService.PublishLockHandle> AcquireLockAsync(string pluginId, CancellationToken ct)
        {
            Events.Add("lock");
            if (LockHeldBy is not null)
                throw new PublishLockHeldException($"Another copy is publishing ({LockHeldBy}).", null);

            return Task.FromResult(new ServerUploadService.PublishLockHandle(
                "/locks/" + pluginId, PublishLock.NewBody(pluginId)));
        }

        public Task<PublishLockRelease> ReleaseLockAsync(
            ServerUploadService.PublishLockHandle handle, CancellationToken ct)
        {
            Events.Add("unlock");
            return Task.FromResult(PublishLockRelease.Released);
        }

        /// <summary>Whether each read was handed a token that could be cancelled, in order.</summary>
        public List<bool> ReadTokensCancellable { get; } = [];

        public bool? PublishTokenCancellable { get; private set; }

        public Task<ServerUploadService.RemoteIndex> ReadIndexAsync(string pluginId, CancellationToken ct)
        {
            Events.Add("read");
            ReadTokensCancellable.Add(ct.CanBeCanceled);
            if (++_reads == FailReadAfter) throw new IOException("the read failed");

            return Task.FromResult(new ServerUploadService.RemoteIndex(Live is not null, Live));
        }

        public async Task PublishIndexAsync(
            string pluginId, byte[] indexJson, Func<Task> beforeSwitchAsync, CancellationToken ct)
        {
            Events.Add("upload");
            PublishTokenCancellable = ct.CanBeCanceled;
            LastSent = indexJson;

            if (FailUploadUntyped is not null) throw FailUploadUntyped;
            if (FailUpload is not null)
                throw new IndexPublishFailedException(FailUpload.Message, renameAttempted: false, FailUpload);

            try
            {
                await beforeSwitchAsync();
            }
            catch (Exception ex)
            {
                throw new IndexPublishFailedException(ex.Message, renameAttempted: false, ex);
            }

            Events.Add("rename");
            if (FailRename is not null)
            {
                if (RenameLandsAnyway) Live = indexJson;
                throw new IndexPublishFailedException(FailRename.Message, renameAttempted: true, FailRename);
            }

            Live = RewriteAfterRename is null ? indexJson : RewriteAfterRename(indexJson);
        }
    }

    private sealed class FakeRegistry : IVerifiedRegistrySource
    {
        public List<string> Events = [];
        public string Json = "";
        public RegistryUnusableException? Fail;

        private int _calls;
        private int _changeAt = int.MaxValue;
        private string? _changed;

        /// <summary>
        /// Serves a different registry from the Nth read of the NEXT publish onwards, counting from
        /// one. Rebased on each call rather than on the process, because a test that has already
        /// published has burned reads, and an offset counted from zero would move the change to
        /// before the publish it is meant to interrupt — which still refuses, for the wrong reason.
        /// </summary>
        public void ChangeFrom(int nthReadOfThisPublish, string registry)
        {
            _changeAt = _calls + nthReadOfThisPublish;
            _changed = registry;
        }

        public Task<string> ReadVerifiedAsync(string pluginId, CancellationToken ct)
        {
            Events.Add("registry");
            _calls++;
            if (Fail is not null) throw Fail;

            return Task.FromResult(_calls >= _changeAt ? _changed! : Json);
        }
    }
}
