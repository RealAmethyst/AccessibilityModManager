using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.CatalogClaims;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// Phase C, increment 2: the manager reads a signed catalog by verifying it, or it does not read it
/// at all.
///
/// <para>Every test here goes through <see cref="PluginRepoClient.FetchPluginIndexAsync"/> — the
/// door a user's manager actually comes in by — rather than calling the verification directly. The
/// difference matters: the point of this increment is that the fetch path CONSULTS the verification,
/// and a test that called the verifier itself would pass just as happily if nothing were wired
/// up.</para>
/// </summary>
public sealed class VerifiedIndexReadTests : IDisposable
{
    private readonly string _root;
    private readonly SignedCatalog _catalog = new();

    public VerifiedIndexReadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ammtest_verify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _catalog.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private PluginRepoClient Client(byte[] served, string? root = null) =>
        new(new HttpClient(new ByteRouteHandler(_ => served)), TestLogger.Create(), root ?? _root);

    private PluginEntry Anchored() => TestPluginEntry.Anchored(_catalog.Anchor);

    // ------------------------------------------------------------------ the trust gate

    [Fact]
    public async Task Unresolved_IsRefused_NotTreatedAsUnsigned()
    {
        // The state an entry is in when no registry acceptance ever spoke for it. Reading it as
        // "unsigned" would be reading the absence of an answer as a permission.
        var client = Client(Encoding.UTF8.GetBytes(_catalog.Plaintext()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchPluginIndexAsync(TestPluginEntry.Unresolved()));

        Assert.Contains("never looked up", ex.Message);
    }

    [Fact]
    public async Task UnusableAnchor_RefusesThePlugin_AndSaysWhy()
    {
        var client = Client(Encoding.UTF8.GetBytes(_catalog.Plaintext()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchPluginIndexAsync(TestPluginEntry.Unusable("the key is written in crayon")));

        // The registry reader's own words reach the user, so the notice can say what is wrong
        // rather than that something is.
        Assert.Contains("the key is written in crayon", ex.Message);
    }

    [Fact]
    public async Task Unanchored_ReadsThePlaintext_Unchanged()
    {
        var client = Client(Encoding.UTF8.GetBytes(_catalog.Plaintext()));

        var index = (await client.FetchPluginIndexAsync(TestPluginEntry.Unanchored())).Value;

        Assert.Equal("plug-a", index.PluginId);
        Assert.Single(index.Games);
    }

    // ------------------------------------------------------------------ verification

    [Fact]
    public async Task Anchored_VerifiesAndProjects()
    {
        var client = Client(_catalog.Build(_catalog.Minimal()));

        var index = (await client.FetchPluginIndexAsync(Anchored())).Value;

        Assert.Equal("plug-a", index.PluginId);
        Assert.Equal("game-1", Assert.Single(index.Games).GameId);
        Assert.Equal("1.0.0", Assert.Single(index.ReleasesByGameId["game-1"]).Version);
    }

    [Fact]
    public async Task Anchored_TakesTheClaims_NotTheServedPlaintext()
    {
        // The catalog beside a proof is not covered by it, so a server can rewrite it freely. Here
        // the plaintext advertises a second game and a different version; neither is signed, so
        // neither may appear.
        var plaintext = JsonNode.Parse(_catalog.Plaintext())!.AsObject();
        plaintext["games"]!.AsArray().Add(JsonNode.Parse("""{"gameId":"smuggled","displayName":"Smuggled"}"""));
        plaintext["releasesByGameId"]!["game-1"]![0]!["version"] = "9.9.9";

        var client = Client(_catalog.Build(_catalog.Minimal(), plaintext.ToJsonString()));

        var index = (await client.FetchPluginIndexAsync(Anchored())).Value;

        Assert.Equal("game-1", Assert.Single(index.Games).GameId);
        Assert.Equal("1.0.0", Assert.Single(index.ReleasesByGameId["game-1"]).Version);
    }

    [Fact]
    public async Task Anchored_NoProofAtAll_IsRefused()
    {
        // "This catalog is unsigned" is not something a signed plugin's server gets to say.
        var client = Client(Encoding.UTF8.GetBytes(_catalog.Plaintext()));

        var ex = await Assert.ThrowsAsync<CatalogRefusedException>(() =>
            client.FetchPluginIndexAsync(Anchored()));

        Assert.Contains("carries no signature", ex.Reason);
    }

    [Fact]
    public async Task Anchored_ProofFromAnotherKey_IsRefused()
    {
        using var impostor = new SignedCatalog(ClaimTestKeys.Secondary);
        var client = Client(impostor.Build(impostor.Minimal()));

        // Same plugin id and index address, a different key. The registry names ours.
        await Assert.ThrowsAnyAsync<Exception>(() => client.FetchPluginIndexAsync(Anchored()));
    }

    [Fact]
    public async Task Anchored_TamperedClaimPayload_IsRefused()
    {
        var served = _catalog.Build(_catalog.Minimal());
        var document = JsonNode.Parse(Encoding.UTF8.GetString(served))!.AsObject();

        // Flip one byte inside the first claim's signed payload. The signature no longer covers it.
        var claims = document["proof"]!["claims"]!.AsArray();
        var payload = Convert.FromBase64String((string)claims[0]!["payload"]!);
        payload[^1] ^= 0x01;
        claims[0]!["payload"] = Convert.ToBase64String(payload);

        var client = Client(Encoding.UTF8.GetBytes(document.ToJsonString()));

        await Assert.ThrowsAnyAsync<Exception>(() => client.FetchPluginIndexAsync(Anchored()));
    }

    [Fact]
    public async Task Anchored_ProofWithoutManifest_IsAccepted()
    {
        // The catalog API strips the manifest from every filtered reply, so a consumer that demanded
        // one would refuse every response the server is designed to send.
        var client = Client(_catalog.Build(_catalog.Minimal(), withManifest: false));

        var index = (await client.FetchPluginIndexAsync(Anchored())).Value;

        Assert.Single(index.Games);
    }

    // ------------------------------------------------------------------ replay

    [Fact]
    public async Task Anchored_OlderClaimAfterANewerOne_IsRefusedAsReplay()
    {
        // Accept sequence 2 for the release, then let a stale mirror serve sequence 1 again. Both
        // are perfectly signed; only one is current. With no saved copy to fall back to, the refusal
        // is the whole answer.
        var current = Client(_catalog.Build([_catalog.Header(), _catalog.Game(), _catalog.Release(seq: 2)]));
        await current.FetchPluginIndexAsync(Anchored());

        var noSnapshot = Path.Combine(_root, "no-snapshot");
        Directory.CreateDirectory(noSnapshot);
        Directory.Move(Path.Combine(_root, "cache"), Path.Combine(noSnapshot, "cache"));

        var stale = Client(_catalog.Build(_catalog.Minimal(releaseSeq: 1)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stale.FetchPluginIndexAsync(Anchored()));

        Assert.Contains("older than version", ex.Message);
    }

    [Fact]
    public async Task Anchored_WithdrawnReleaseCannotBeRestoredByReplay()
    {
        // The whole point of the increment. The release is published, then withdrawn by a
        // higher-sequence revocation. A server that goes back to serving the original claim is
        // serving something this machine has already been told is gone.
        //
        // The withdrawn set carries the revocation and NOT the live claim, which is what the
        // publisher actually emits: outstanding revocations ride along on every publish forever, but
        // a deleted object's live claim is simply not re-emitted (ClaimSetBuilder.Build).
        var released = Client(_catalog.Build(_catalog.Minimal()));
        Assert.Single((await released.FetchPluginIndexAsync(Anchored())).Value.ReleasesByGameId["game-1"]);

        var identity = new ClaimIdentity
        {
            Kind = ClaimKind.Release, GameId = "game-1", Channel = "stable", Version = "1.0.0"
        };
        var withdrawn = Client(_catalog.Build(
            [_catalog.Header(), _catalog.Game(), _catalog.Revocation(identity, seq: 2)]));

        var afterWithdrawal = (await withdrawn.FetchPluginIndexAsync(Anchored())).Value;
        Assert.False(afterWithdrawal.ReleasesByGameId.TryGetValue("game-1", out var still) && still.Count > 0);

        // Now the replay: the original set, still validly signed, with the revocation dropped. The
        // live catalog is refused; the last copy this machine accepted stands in, and that copy is
        // the one WITHOUT the release. The withdrawal holds either way.
        var replayed = Client(_catalog.Build(_catalog.Minimal()));
        var served = await replayed.FetchPluginIndexAsync(Anchored());

        Assert.NotNull(served.LiveRejectionReason);
        Assert.Contains("older than version", served.LiveRejectionReason);
        Assert.False(served.Value.ReleasesByGameId.TryGetValue("game-1", out var back) && back.Count > 0);
    }

    [Fact]
    public async Task Anchored_RefusedLiveCatalog_FallsBackToTheLastVerifiedCopy_AndSaysSo()
    {
        // Settled: a failed verification rejects that RESPONSE, not the last good document. A hostile
        // server can withhold a reply entirely, so blanking the catalog buys nothing — but the user
        // has to be told, because this is not the same as being offline.
        var good = Client(_catalog.Build([_catalog.Header(), _catalog.Game(), _catalog.Release(seq: 2)]));
        await good.FetchPluginIndexAsync(Anchored());

        // A live reply with no proof at all — reached, and refused.
        var hostile = Client(Encoding.UTF8.GetBytes(_catalog.Plaintext()));
        var served = await hostile.FetchPluginIndexAsync(Anchored());

        Assert.True(served.FromCache);
        Assert.NotNull(served.LiveRejectionReason);
        Assert.Contains("carries no signature", served.LiveRejectionReason);
        Assert.Single(served.Value.ReleasesByGameId["game-1"]);
    }

    [Fact]
    public async Task Anchored_ResurrectionUnderAnOlderAudience_IsRefused()
    {
        // The hole the per-(object, audience) key left open, and the reason the ceiling is per
        // object. R is live for one tier, widens, then narrows — leaving a revocation at the wider
        // audience and a live claim at the narrower one. The server then serves R's ORIGINAL claim,
        // under its ORIGINAL audience, and omits the revocation. Its own per-audience high-water is
        // satisfied exactly, and omission is not an error.
        var tier1 = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t1"] };
        var both = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t1", "t2"] };
        var tier2 = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t2"] };

        var identity = new ClaimIdentity
        {
            Kind = ClaimKind.Release, GameId = "game-1", Channel = "stable", Version = "1.0.0"
        };

        SignedClaim Gated(long seq, ClaimAudience audience) =>
            _catalog.Signer.Sign(ClaimKind.Release, identity, seq, audience, "{}");

        var original = Gated(1, tier1);

        // What the machine last saw: the narrowing publish. It carries the revocation aimed at the
        // audience that lost access, and the replacement above it.
        var store = new ClaimReplayStore(Path.Combine(_root, "claim-highwater"), TestLogger.Create());
        await store.AcceptAsync(_catalog.Anchor,
            [_catalog.Header(), _catalog.Revocation(identity, 3, both), Gated(4, tier2)], checkOnly: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AcceptAsync(_catalog.Anchor, [_catalog.Header(), original], checkOnly: false));

        Assert.Contains("older than version", ex.Message);
    }

    [Fact]
    public async Task Anchored_MissingClaimIsNotAnError()
    {
        // Responses are allowed to be incomplete — that is what audience filtering is. A claim that
        // simply is not there must not read as a rollback of the one recorded for it.
        var full = Client(_catalog.Build(
            [_catalog.Header(), _catalog.Game(), _catalog.Release(), _catalog.Release(gameId: "game-1", version: "2.0.0")],
            JsonNode.Parse(_catalog.Plaintext())!.ToJsonString()));
        await full.FetchPluginIndexAsync(Anchored());

        var narrowed = Client(_catalog.Build(_catalog.Minimal()));
        var index = (await narrowed.FetchPluginIndexAsync(Anchored())).Value;

        Assert.Equal("1.0.0", Assert.Single(index.ReleasesByGameId["game-1"]).Version);
    }

    [Fact]
    public async Task ReplayRecords_SurviveClearingTheCache()
    {
        // Clearing a cache is a routine recovery step. It must not also erase the evidence that
        // makes a withdrawal stick, or "delete the cache" becomes the way to undo one.
        var current = Client(_catalog.Build([_catalog.Header(), _catalog.Game(), _catalog.Release(seq: 2)]));
        await current.FetchPluginIndexAsync(Anchored());

        Directory.Delete(Path.Combine(_root, "cache"), recursive: true);

        var stale = Client(_catalog.Build(_catalog.Minimal(releaseSeq: 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stale.FetchPluginIndexAsync(Anchored()));
    }

    // ------------------------------------------------------------------ bounds and decoding

    [Fact]
    public async Task OversizedResponse_IsRefusedBeforeItIsParsed()
    {
        var huge = new byte[ClaimProof.MaxIndexBytes + 1];
        huge[0] = (byte)'{';

        var client = Client(huge);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchPluginIndexAsync(TestPluginEntry.Unanchored()));

        Assert.Contains("larger than", ex.Message);
    }

    [Fact]
    public async Task OversizedResponse_StopsReading_RatherThanBufferingItAll()
    {
        // The refusal alone does not prove the protection: a check applied to an already-buffered
        // body refuses just as loudly while the manager has still held all of it in memory. What is
        // pinned here is that the reader STOPS, so a host serving an endless body cannot make the
        // manager swallow it.
        var counting = new CountingContent(ClaimProof.MaxIndexBytes * 4L);
        var client = new PluginRepoClient(
            new HttpClient(new ContentHandler(counting)), TestLogger.Create(), _root);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchPluginIndexAsync(TestPluginEntry.Unanchored()));

        Assert.True(counting.Produced <= ClaimProof.MaxIndexBytes + (1 << 20),
            $"read {counting.Produced} bytes for a {ClaimProof.MaxIndexBytes}-byte ceiling");
    }

    /// <summary>
    /// A body far larger than the ceiling that is produced ONLY as it is read, and reports how much
    /// of it was.
    ///
    /// <para><see cref="CreateContentReadStreamAsync"/> is overridden deliberately. The base
    /// implementation serializes the whole content into a buffer first, which would make this fixture
    /// measure itself: every byte would be produced no matter what the client did.</para>
    /// </summary>
    private sealed class CountingContent(long length) : HttpContent
    {
        private readonly LazyStream _stream = new(length);

        public long Produced => _stream.Produced;

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            _stream.CopyToAsync(stream);

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = 0;
            return false;   // no Content-Length, so the bound cannot come from the server's claim
        }

        private sealed class LazyStream(long length) : Stream
        {
            public long Produced { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Produced >= length) return 0;
                var take = (int)Math.Min(count, length - Produced);
                Array.Fill(buffer, (byte)' ', offset, take);
                if (Produced == 0 && take > 0) buffer[offset] = (byte)'{';
                Produced += take;
                return take;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get => Produced; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class ContentHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }

    [Fact]
    public async Task AHostThatAnswersAndThenStopsSending_IsGivenUpOn_NotWaitedOnForever()
    {
        // ResponseHeadersRead is what makes the size ceiling real, and it also takes the body out of
        // HttpClient.Timeout's reach — that timeout covers the send, and with headers-only completion
        // the send is already over. Without an explicit deadline this read never returns, and the Mods
        // refresh walks plugins in sequence, so ONE such host hangs every plugin after it.
        var good = Client(_catalog.Build(_catalog.Minimal()));
        await good.FetchPluginIndexAsync(Anchored());

        var stalling = new PluginRepoClient(
            new HttpClient(new ContentHandler(new StallingContent())), TestLogger.Create(), _root,
            responseDeadlineOverride: TimeSpan.FromMilliseconds(250));

        // Given up on and classed as the host being unreachable, so the saved copy stands in.
        var served = await stalling.FetchPluginIndexAsync(Anchored());

        Assert.True(served.FromCache);
        Assert.Single(served.Value.Games);
    }

    [Fact]
    public async Task AnOversizedLiveResponse_StillFallsBackToTheSavedCopy()
    {
        // The bounded read used to sit outside the fallback's scope, so a host serving more than the
        // ceiling made the plugin vanish instead of showing the verified copy already on disk.
        var good = Client(_catalog.Build(_catalog.Minimal()));
        await good.FetchPluginIndexAsync(Anchored());

        var huge = new byte[ClaimProof.MaxIndexBytes + 1];
        huge[0] = (byte)'{';
        var oversized = Client(huge);

        var served = await oversized.FetchPluginIndexAsync(Anchored());

        Assert.True(served.FromCache);
        Assert.NotNull(served.LiveRejectionReason);
        Assert.Contains("larger than", served.LiveRejectionReason);
        Assert.Single(served.Value.Games);
    }

    /// <summary>Headers, then silence — the shape a hung host actually has.</summary>
    private sealed class StallingContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new NeverEndingStream());

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            Task.Delay(Timeout.Infinite);

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = 0;
            return false;
        }

        private sealed class NeverEndingStream : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task InvalidUtf8_OnTheUnsignedPath_IsRefused()
    {
        // ReadAsStringAsync would have replaced the bad bytes with U+FFFD and carried on, so an
        // unsigned field could hold content one implementation reads and another rejects.
        var body = Encoding.UTF8.GetBytes(_catalog.Plaintext()).ToList();
        body.InsertRange(body.Count - 1, new byte[] { 0xC3, 0x28 });

        var client = Client([.. body]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchPluginIndexAsync(TestPluginEntry.Unanchored()));

        Assert.Contains("not valid UTF-8", ex.Message);
    }

    [Fact]
    public async Task ByteOrderMark_OnTheUnsignedPath_IsStillAccepted()
    {
        // Third-party unsigned indexes are hand-edited files and some editors write a BOM. This path
        // has always accepted them, and tightening it would drop working plugins for no gain — the
        // signed path refuses one, which is where the strictness belongs.
        var body = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes(_catalog.Plaintext())).ToArray();

        var client = Client(body);

        var index = (await client.FetchPluginIndexAsync(TestPluginEntry.Unanchored())).Value;

        Assert.Equal("plug-a", index.PluginId);
    }

    // ------------------------------------------------------------------ the offline copy

    [Fact]
    public async Task CachedSignedCatalog_IsVerifiedAgainOnTheWayOut()
    {
        var served = _catalog.Build(_catalog.Minimal());
        await Client(served).FetchPluginIndexAsync(Anchored());

        var offline = new PluginRepoClient(
            new HttpClient(new FailingHandler()), TestLogger.Create(), _root);
        var cached = await offline.FetchPluginIndexAsync(Anchored());

        Assert.True(cached.FromCache);
        Assert.Single(cached.Value.Games);
    }

    [Fact]
    public async Task CachedSignedCatalog_WithItsProofStripped_IsRefused()
    {
        var served = _catalog.Build(_catalog.Minimal());
        await Client(served).FetchPluginIndexAsync(Anchored());

        // Strip the proof from the SAVED copy. Before the cache carried the exact accepted bytes it
        // stored the plaintext catalog, which is precisely a copy with no proof — and it was served
        // offline without anything ever checking a signature.
        var cachePath = Assert.Single(
            Directory.EnumerateFiles(Path.Combine(_root, "cache", "indexes"), "*.json"));
        var envelope = JsonNode.Parse(File.ReadAllText(cachePath))!;
        var document = JsonNode.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String((string)envelope["indexBase64"]!)))!.AsObject();
        document.Remove("proof");
        envelope["indexBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(document.ToJsonString()));
        File.WriteAllText(cachePath, envelope.ToJsonString());

        var offline = new PluginRepoClient(
            new HttpClient(new FailingHandler()), TestLogger.Create(), _root);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            offline.FetchPluginIndexAsync(Anchored()));

        Assert.Contains("rejected", ex.Message);
    }

    [Fact]
    public async Task CachedCopy_FromAnOlderVersion_IsRefusedForAnAnchoredPlugin()
    {
        // A version-1 envelope holds a plaintext catalog and no proof. For a plugin the registry
        // says is signed, there is no honest way to manufacture the evidence it never carried.
        WriteLegacyCache();

        var offline = new PluginRepoClient(
            new HttpClient(new FailingHandler()), TestLogger.Create(), _root);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            offline.FetchPluginIndexAsync(Anchored()));

        Assert.Contains("before this version checked signatures", ex.Message);
    }

    [Fact]
    public async Task CachedCopy_FromAnOlderVersion_IsStillServedForAnUnanchoredPlugin()
    {
        // Nothing was ever verified about an unanchored catalog and nothing is being skipped.
        // Refusing it would take the offline catalog — and with it every installed mod's row, which
        // is where uninstall lives — away from someone who upgraded while offline, over a check that
        // does not apply to them.
        WriteLegacyCache();

        var offline = new PluginRepoClient(
            new HttpClient(new FailingHandler()), TestLogger.Create(), _root);

        var served = await offline.FetchPluginIndexAsync(TestPluginEntry.Unanchored());

        Assert.True(served.FromCache);
        Assert.Equal("plug-a", served.Value.PluginId);
    }

    /// <summary>The pre-verification envelope, under the name this plugin's cache really has.</summary>
    private void WriteLegacyCache()
    {
        var indexes = Path.Combine(_root, "cache", "indexes");
        Directory.CreateDirectory(indexes);

        var path = Path.Combine(indexes, Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("plug-a")))[..32] + ".json");

        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            fetchedAtUtc = DateTimeOffset.UtcNow,
            sourceUrl = "https://example.invalid/index.json",
            indexJson = _catalog.Plaintext()
        }));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("simulated network failure");
    }
}
