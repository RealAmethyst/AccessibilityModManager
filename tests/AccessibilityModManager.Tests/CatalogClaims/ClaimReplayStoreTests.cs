using System.Text;
using System.Text.Json.Nodes;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// The store on its own: the ratchet's arithmetic, and what it does when its own file is not to be
/// trusted.
///
/// <para>The end-to-end behaviour — a withdrawn release staying withdrawn through a real fetch —
/// lives in <c>VerifiedIndexReadTests</c>. What is here is everything that only shows up when the
/// record itself is missing, damaged, or someone else's.</para>
/// </summary>
public sealed class ClaimReplayStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly SignedCatalog _catalog = new();

    public ClaimReplayStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ammtest_replay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _catalog.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private ClaimReplayStore Store() => new(_directory, TestLogger.Create());

    private Task AcceptAsync(IReadOnlyList<SignedClaim> claims, bool checkOnly = false) =>
        Store().AcceptAsync(_catalog.Anchor, claims, checkOnly);

    private string RecordFile() => Assert.Single(Directory.EnumerateFiles(_directory, "*.json"));

    private static ClaimIdentity Release(string version = "1.0.0") => new()
    {
        Kind = ClaimKind.Release, GameId = "game-1", Channel = "stable", Version = version
    };

    // ------------------------------------------------------------------ the ratchet

    [Fact]
    public async Task FirstAcceptance_RecordsAndProceeds()
    {
        await AcceptAsync(_catalog.Minimal());

        Assert.True(File.Exists(RecordFile()));
    }

    [Fact]
    public async Task LowerSequence_IsRefusedAsAReplay()
    {
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 4)]));

        Assert.Contains("older than version", ex.Message);
    }

    [Fact]
    public async Task SameSequence_DifferentPayload_IsRefusedAsEquivocation()
    {
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);

        // Same object, same sequence, different content. One version of one thing can only say one
        // thing — and the sequence comparison alone would wave this through.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5, url: "https://elsewhere.invalid/p.zip")]));

        Assert.Contains("same version", ex.Message);
    }

    [Fact]
    public async Task SameSequence_SamePayload_IsAccepted()
    {
        // Refreshing an unchanged catalog is the ordinary case and must cost nothing.
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
    }

    [Fact]
    public async Task AbsentClaim_IsNotAnError_AndItsRecordSurvives()
    {
        // Responses may legitimately be incomplete. But forgetting the position because the claim
        // was withheld would hand a server the way to erase the evidence that refuses its replay:
        // withhold once to clear the record, then serve the old claim.
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);

        await AcceptAsync([_catalog.Header()]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 4)]));

        Assert.Contains("older than version", ex.Message);
    }

    [Fact]
    public async Task ARevocationBelowTheCeiling_IsStillAccepted()
    {
        // Every outstanding revocation rides along on every publish, forever, so a legitimate set
        // carries revocations far below its newest sequence. Holding them to the ceiling would refuse
        // ordinary honest catalogs on the second fetch — the narrowing publish below is exactly what
        // the publisher emits, and it must survive being fetched twice.
        var patrons = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t3"] };

        var narrowing = new List<SignedClaim>
        {
            _catalog.Header(),
            _catalog.Signer.Sign(ClaimKind.Revocation, Release(), 7, ClaimAudience.Everyone, "{}"),
            _catalog.Signer.Sign(ClaimKind.Release, Release(), 8, patrons, "{}")
        };

        await AcceptAsync(narrowing);
        await AcceptAsync(narrowing);
    }

    [Fact]
    public async Task AFilteredViewShowingOnlyTheRevocation_IsNotARollback()
    {
        // A public reader legitimately cannot see the tier-only replacement. Their view carries the
        // revocation and nothing above it — an incomplete response, not an older one.
        var patrons = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t3"] };

        await AcceptAsync([
            _catalog.Header(),
            _catalog.Signer.Sign(ClaimKind.Revocation, Release(), 7, ClaimAudience.Everyone, "{}"),
            _catalog.Signer.Sign(ClaimKind.Release, Release(), 8, patrons, "{}")
        ]);

        await AcceptAsync([
            _catalog.Header(),
            _catalog.Signer.Sign(ClaimKind.Revocation, Release(), 7, ClaimAudience.Everyone, "{}")
        ]);
    }

    [Fact]
    public async Task ALiveClaimUnderAnOlderAudience_CannotUndoAWithdrawal()
    {
        // The failure that made the ceiling per-object. The original claim, under its original
        // audience, satisfies its own per-audience record exactly — and the revocation above it is
        // simply omitted, which is not an error. Per object it is below the withdrawal and refuses.
        var tier1 = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t1"] };
        var both = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t1", "t2"] };
        var tier2 = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t2"] };

        var original = _catalog.Signer.Sign(ClaimKind.Release, Release(), 1, tier1, "{}");

        await AcceptAsync([_catalog.Header(), original]);
        await AcceptAsync([
            _catalog.Header(),
            _catalog.Signer.Sign(ClaimKind.Revocation, Release(), 3, both, "{}"),
            _catalog.Signer.Sign(ClaimKind.Release, Release(), 4, tier2, "{}")
        ]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), original]));

        Assert.Contains("older than version", ex.Message);
    }

    [Fact]
    public async Task TheSameSequenceUnderTwoAudiences_IsEquivocation()
    {
        // Sequences are allocated per object — one past every sequence ever used for it, revocations
        // included — so a repeat under a different audience is not a second numbering. It is two
        // truths under one number, and it must not reach two independent records.
        var tier1 = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t1"] };
        var tier2 = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t2"] };

        await AcceptAsync([
            _catalog.Header(),
            _catalog.Signer.Sign(ClaimKind.Release, Release(), 5, tier1, """{"a":1}""")
        ]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([
                _catalog.Header(),
                _catalog.Signer.Sign(ClaimKind.Release, Release(), 5, tier2, """{"a":2}""")
            ]));

        Assert.Contains("same version", ex.Message);
    }

    [Fact]
    public async Task AudienceKey_IgnoresTierOrder()
    {
        // The serving order of a tier list is the server's choice. If it reached the key, one
        // audience would ratchet under two records and neither would ever refuse the other.
        var a = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t1", "t2"] };
        var b = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t2", "t1"] };

        Assert.Equal(a.ToStorageKey(), b.ToStorageKey());

        await AcceptAsync([_catalog.Signer.Sign(ClaimKind.Release, Release(), 5, a, "{}")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Signer.Sign(ClaimKind.Release, Release(), 4, b, "{}")]));
    }

    [Fact]
    public void AudienceKey_SeparatesAudiencesThatOnlyLookAlike()
    {
        // Length-prefixed parts, so no tier id can be crafted to run into the next field.
        var one = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t1|t2"] };
        var two = new ClaimAudience { Public = false, CampaignId = "c1", TierIds = ["t1", "t2"] };

        Assert.NotEqual(one.ToStorageKey(), two.ToStorageKey());
        Assert.NotEqual(ClaimAudience.Everyone.ToStorageKey(),
            new ClaimAudience { Public = false, CampaignId = "" }.ToStorageKey());
    }

    // ------------------------------------------------------------------ when the file is suspect

    [Fact]
    public async Task EmptyRecordFile_IsRefused_AndNotOverwritten()
    {
        // A read that WORKS and returns nothing is a third route to "no records", and only a missing
        // file may mean it. Overwriting here would record the position being replayed and make the
        // loss permanent.
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
        var path = RecordFile();
        File.WriteAllBytes(path, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 4)]));

        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public async Task UnreadableRecordFile_IsRefused()
    {
        // Present and unreadable is this machine's ratchet in an unknown position, which is not the
        // same as never having had one. A directory where the file belongs makes the read fail the
        // way a permission problem would.
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
        var path = RecordFile();
        File.Delete(path);
        Directory.CreateDirectory(path);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]));
    }

    [Fact]
    public async Task RecordFileStampedWithAnotherTrustContext_IsRefused()
    {
        // The file is named after its context and also carries it, so a copy dropped into another
        // context's place cannot lend one plugin's history to another's catalog.
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
        var path = RecordFile();
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["trustContext"] = new string('b', 64);
        File.WriteAllText(path, document.ToJsonString());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]));
    }

    [Fact]
    public async Task RecordFileWithTwoRecordsForOneObject_IsRefused()
    {
        // An ambiguity about a recorded position is damage, not something to resolve by picking one.
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
        var path = RecordFile();
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        var records = document["records"]!.AsArray();
        records.Add(JsonNode.Parse(records[0]!.ToJsonString()));
        File.WriteAllText(path, document.ToJsonString());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]));
    }

    [Theory]
    [InlineData("seq", "-1")]                        // a sequence nothing can be below
    [InlineData("seq", "0")]                         // what a MISSING seq member deserializes to
    [InlineData("seq", "1000000000001")]             // above the maximum a signed claim may carry
    [InlineData("object", "\"\"")]                   // no object to be about
    public async Task RecordWithAnUnusableField_IsRefused(string field, string rawValue)
    {
        // A record that is present but says nothing is not a weaker record — it is an unknown
        // position, and an unknown position must never read as permission. Every field the
        // comparison depends on is checked before any of it is compared against.
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
        var path = RecordFile();
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["records"]![0]![field] = JsonNode.Parse(rawValue);
        File.WriteAllText(path, document.ToJsonString());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]));
    }

    [Theory]
    [InlineData("\"deadbeef\"")]                      // truncated
    [InlineData("\"\"")]                              // absent
    [InlineData("\"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz\"")]  // not hex
    public async Task RecordWithADamagedHash_SaysDamaged_NotThatTheAuthorEquivocated(string rawHash)
    {
        // Refusing either way is not the same as refusing rightly. Without the shape check the
        // comparison still fails — a real 64-character hash never equals rubbish — but it fails as
        // "this catalog offers a different X under the same version", which accuses the author of
        // publishing two truths under one number when what actually happened is that a file on THIS
        // machine got damaged. The remedy the user is sent to would be the wrong one.
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
        var path = RecordFile();
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["records"]![0]!["payload"] = JsonNode.Parse(rawHash);
        File.WriteAllText(path, document.ToJsonString());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]));

        Assert.Contains("is damaged", ex.Message);
    }

    [Fact]
    public async Task ARecordWhoseSequenceMemberIsMissing_IsRefused_NotReadAsZero()
    {
        // The scenario that makes the range check load-bearing rather than tidy. A record whose
        // `seq` is gone deserializes to ZERO — below every real claim — so this object's ceiling
        // silently drops to nothing, and the next authentic-but-withdrawn claim sails over it.
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
        var path = RecordFile();
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["records"]![0]!.AsObject().Remove("seq");
        File.WriteAllText(path, document.ToJsonString());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 1)]));
    }

    [Fact]
    public async Task RecordFileFromAFutureVersion_IsRefused()
    {
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
        var path = RecordFile();
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["v"] = 99;
        File.WriteAllText(path, document.ToJsonString());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]));
    }

    // ------------------------------------------------------------------ the cache path

    [Fact]
    public async Task CheckOnly_WithNoRecordAtAll_IsRefused()
    {
        // A cached copy may only replay an acceptance this machine provably made. Deleting the
        // record must not be a way to let an old cached catalog back in through the first-fetch door.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync(_catalog.Minimal(), checkOnly: true));

        Assert.Contains("no record", ex.Message);
    }

    [Fact]
    public async Task CheckOnly_RefusesAClaimItHasNoRecordOf()
    {
        // A cached copy may only replay an acceptance this machine provably made. Every claim in one
        // was recorded when it was accepted online, so a claim with no record did not come from an
        // acceptance — and "the file exists" is evidence about other claims, not this one.
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
        var before = File.ReadAllText(RecordFile());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 9)], checkOnly: true));

        Assert.Contains("no record of ever accepting", ex.Message);
        Assert.Equal(before, File.ReadAllText(RecordFile()));
    }

    [Fact]
    public async Task CheckOnly_AcceptsExactlyWhatWasRecorded_AndWritesNothing()
    {
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
        var before = File.ReadAllText(RecordFile());

        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)], checkOnly: true);

        Assert.Equal(before, File.ReadAllText(RecordFile()));
    }

    [Fact]
    public async Task CheckOnly_StillRefusesAReplay()
    {
        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(seq: 4)], checkOnly: true));
    }

    // ------------------------------------------------------------------ separation

    [Fact]
    public async Task ADifferentTrustContext_KeepsItsOwnHistory()
    {
        // An authorised key rotation or index re-point starts a fresh history rather than colliding
        // with the old one.
        using var other = new SignedCatalog(ClaimTestKeys.Secondary);

        await AcceptAsync([_catalog.Header(), _catalog.Release(seq: 5)]);
        await Store().AcceptAsync(other.Anchor, [other.Header(), other.Release(seq: 1)], checkOnly: false);

        Assert.Equal(2, Directory.EnumerateFiles(_directory, "*.json").Count());
    }

    [Fact]
    public async Task RecordFileIsNamedForItsTrustContext()
    {
        await AcceptAsync(_catalog.Minimal());

        var expected = ClaimTrustContext.Compute(_catalog.Anchor) + ".json";
        Assert.Equal(expected, Path.GetFileName(RecordFile()));
    }

    [Fact]
    public async Task RefusalNamesWhatItRefused()
    {
        // The message reaches a user as the reason their catalog did not refresh, so it has to say
        // which thing went backwards rather than that something did.
        await AcceptAsync([_catalog.Header(), _catalog.Release(version: "2.0.0", seq: 5)]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptAsync([_catalog.Header(), _catalog.Release(version: "2.0.0", seq: 4)]));

        Assert.Contains("2.0.0", ex.Message);
        Assert.Contains("game-1", ex.Message);
    }

    [Fact]
    public async Task RecordFileIsUtf8Json()
    {
        await AcceptAsync(_catalog.Minimal());

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(RecordFile()));
        Assert.Contains("\"trustContext\"", text);
        Assert.Contains("\"records\"", text);
    }
}
