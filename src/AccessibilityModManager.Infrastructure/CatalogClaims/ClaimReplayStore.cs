using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// The consumer's memory of the newest claim it has accepted for each published object — the thing
/// that makes withdrawing a release mean something.
///
/// <para><b>A signature proves authorship, never freshness.</b> Without this store, a manager that
/// verifies every signature perfectly still accepts an older, authentically signed proof replayed at
/// it forever. The author withdraws a release; a stale mirror or a hostile server keeps serving the
/// previous proof; every manager keeps offering the withdrawn release. Verification alone does not
/// close that, and revocation does not work until this does.</para>
///
/// <para><b>Sequences belong to the OBJECT, and so does the ceiling.</b> An earlier version of this
/// kept a high-water per (object, audience), which reads as more precise and is broken: narrow a
/// release and then withdraw it, and a server can serve the ORIGINAL claim under its original, older
/// audience — whose own per-audience record it still satisfies — while simply omitting the
/// revocation. Omission is not an error, so the withdrawn release comes back. Per object, that claim
/// is below the revocation's sequence and refuses. Codex found this; the audience is still recorded,
/// but it is not part of the key.</para>
///
/// <para><b>Live claims and revocations are held to different rules.</b> The publisher carries every
/// outstanding revocation forward on every publish, forever, so a legitimate set contains revocations
/// far below its newest sequence and refusing those would refuse ordinary catalogs. A LIVE claim is
/// different: the builder re-emits at most one per object, at one past every sequence ever used for
/// it. So a live claim below anything already seen for that object cannot be the current one.</para>
///
/// <para><b>A claim that is simply absent is not an error.</b> Responses are allowed to be
/// incomplete — that is what audience filtering is — so absence can never be distinguished from
/// withholding, and treating it as an attack would refuse every filtered response. The protection
/// this store gives is against a claim coming back OLDER than one already accepted, or DIFFERENT
/// under a sequence already accepted.</para>
///
/// <para>Namespaced per trust context, so an authorised key rotation or index re-point starts a
/// fresh history rather than colliding with the old one.</para>
/// </summary>
public sealed class ClaimReplayStore
{
    /// <summary>
    /// Serializes the read-compare-write within this process. The Mods and Developers tabs can fetch
    /// concurrently; a second app COPY is serialized by the file lock.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        // Duplicate members would let two readings of one file disagree about a recorded position,
        // which is the whole thing this file exists to be unambiguous about.
        AllowDuplicateProperties = false
    };

    private readonly string _directory;
    private readonly ILogger _logger;

    /// <param name="stateDirectory">
    /// Where the per-context records live. Deliberately NOT under the offline cache directory:
    /// clearing a cache is a routine recovery step, and it must not silently reset this machine's
    /// protection against replayed catalogs.
    /// </param>
    public ClaimReplayStore(string stateDirectory, ILogger logger)
    {
        _directory = stateDirectory;
        _logger = logger;
    }

    /// <summary>
    /// Checks a freshly verified claim set against everything this machine has already accepted for
    /// the same trust context, and — unless <paramref name="checkOnly"/> — records the result.
    ///
    /// <para>Throws on a replay or an equivocation, and on any failure to read or write the record.
    /// <b>Persistence is mandatory.</b> Accepting a catalog this machine cannot record means the next
    /// fetch judges against a stale position: the ratchet is lost at the very moment it should have
    /// advanced, and no later run can tell that it happened.</para>
    /// </summary>
    /// <param name="checkOnly">
    /// True for the offline cache path. A cached copy may only replay an acceptance this machine
    /// provably made, so it compares but never advances — and with no record at all it is refused,
    /// because deleting the record must not be a way to let an old cached catalog back in.
    /// </param>
    /// <param name="onCommitted">
    /// Runs inside this transaction once the records are durable. Anything derived from an
    /// acceptance — the offline snapshot — belongs here: written outside, two app copies can commit
    /// records in one order and snapshots in the other, leaving the newer records beside an older
    /// snapshot they will correctly refuse.
    /// </param>
    public async Task AcceptAsync(
        ClaimTrustAnchor anchor, IReadOnlyList<SignedClaim> claims, bool checkOnly,
        Func<Task>? onCommitted = null)
    {
        var trustContext = ClaimTrustContext.Compute(anchor);

        await Gate.WaitAsync();
        try
        {
            using var crossProcessLock = await CrossProcessFileLock.AcquireAsync(
                Path.Combine(_directory, "claim-highwater.lock"), "catalog history");
            await AcceptCoreAsync(anchor, trustContext, claims, checkOnly);
            if (onCommitted is not null) await onCommitted();
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task AcceptCoreAsync(
        ClaimTrustAnchor anchor, string trustContext, IReadOnlyList<SignedClaim> claims, bool checkOnly)
    {
        var path = RecordPath(trustContext);
        var (recorded, fileAbsent) = ReadRecords(path, trustContext, anchor.PluginId);

        if (checkOnly && fileAbsent)
        {
            throw new InvalidOperationException(
                $"there is no record of this machine ever accepting a catalog for '{anchor.PluginId}', " +
                "so the saved copy can't be trusted");
        }

        // The highest sequence this machine has ever seen for each OBJECT, computed from the durable
        // state BEFORE this response is merged into it. Per object, not per (object, audience) — see
        // the note on the class.
        var ceiling = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var record in recorded.Values)
        {
            if (!ceiling.TryGetValue(record.ObjectKey, out var seen) || record.Seq > seen)
                ceiling[record.ObjectKey] = record.Seq;
        }

        var changed = false;

        foreach (var incoming in Describe(claims))
        {
            if (recorded.TryGetValue(incoming.Key, out var known))
            {
                // Equivocation, and it is checked across ALL audiences: one sequence of one object
                // may only ever have said one thing. Sequences are allocated per object (one past
                // every sequence ever used for it, revocations included), so a repeat under a
                // different audience is not a second numbering — it is two truths under one number.
                if (!string.Equals(incoming.PayloadSha256, known.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"the catalog for '{anchor.PluginId}' offers a different {incoming.Describe()} " +
                        $"under the same version {incoming.Seq} this machine already accepted. Refusing " +
                        "it — one version of one thing can only say one thing.");
                }
            }
            else if (checkOnly)
            {
                // A cached copy may only REPLAY an acceptance this machine provably made. Every claim
                // in it was recorded when it was accepted online, so one that is not recorded did not
                // come from an acceptance — and "the file exists" is not evidence about THIS claim.
                throw new InvalidOperationException(
                    $"the saved copy of '{anchor.PluginId}' offers {incoming.Describe()} at version " +
                    $"{incoming.Seq}, which this machine has no record of ever accepting");
            }

            // The rule that makes a withdrawal stick.
            //
            // A live claim is always the HIGHEST sequence for its object in a legitimate set: the
            // builder carries outstanding revocations forward forever but re-emits at most one live
            // claim, at one past every sequence ever used. So a live claim below something already
            // seen for that object cannot be current — it is the object as it was before a
            // revocation, offered again.
            //
            // Keying the ceiling per (object, audience) instead — which this store did until Codex
            // found it — lets exactly that through: withdraw a release that had earlier been narrowed,
            // and the server can serve the ORIGINAL claim under its original, older audience, whose
            // own per-audience record it still satisfies. The revocation is simply omitted, and
            // omission is not an error. Per object, that same claim is below the revocation's
            // sequence and refuses.
            //
            // Revocations are exempt, and must be: every one ever issued rides along on every
            // publish, so a set legitimately carries revocations far below its newest sequence, and
            // refusing those would refuse ordinary honest catalogs.
            if (!incoming.IsRevocation &&
                ceiling.TryGetValue(incoming.ObjectKey, out var highest) && incoming.Seq < highest)
            {
                throw new InvalidOperationException(
                    $"the catalog for '{anchor.PluginId}' offers {incoming.Describe()} at version " +
                    $"{incoming.Seq}, older than version {highest} this machine has already accepted for " +
                    "it. Refusing it — this can be a stale mirror, or an old copy replayed to undo a " +
                    "withdrawal. Try again later; if it persists, the catalog itself needs attention.");
            }

            if (known is null)
            {
                if (recorded.Count >= MaxRecords)
                {
                    throw new InvalidOperationException(
                        $"this machine's record of the catalog for '{anchor.PluginId}' has reached its " +
                        $"limit of {MaxRecords} entries. The catalog wasn't refreshed.");
                }

                recorded[incoming.Key] = incoming;
                changed = true;
            }
        }

        // Records with no matching claim in this response are KEPT, never pruned. A response is
        // allowed to be incomplete, and forgetting a position because a claim was withheld would
        // hand a server the ability to erase the very evidence that refuses its replay: withhold
        // once to clear the record, then serve the old claim.

        if (checkOnly || !changed) return;

        try
        {
            var document = new StoredRecords
            {
                Version = FileVersion,
                TrustContext = trustContext,
                Records = [.. recorded.Values.OrderBy(r => r.ObjectKey, StringComparer.Ordinal)
                                             .ThenBy(r => r.Seq)]
            };
            // DURABLE, not merely atomic. AtomicJson writes through the ordinary cache and commits
            // its rename the same way, so a machine that loses power right after this returns can
            // come back holding the PREVIOUS records — while the user was already told the catalog
            // refreshed. This is a ratchet: losing a committed advance is exactly the rollback it
            // exists to refuse, and a server replaying the older claim would then satisfy the
            // restored record exactly.
            DurableFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(document, FileOptions));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Couldn't persist the catalog history for {PluginId}", anchor.PluginId);
            throw new InvalidOperationException(
                $"the catalog for '{anchor.PluginId}' verified, but the record of having accepted it " +
                "couldn't be saved, so an older copy couldn't be refused later. The catalog wasn't " +
                "refreshed.", ex);
        }
    }

    /// <summary>
    /// Every claim in the response as a record, keyed by (object, sequence).
    ///
    /// <para>All of them, not the highest per object: a sequence's hash has to be remembered for as
    /// long as that sequence can come back, and revocations come back on every publish forever. The
    /// set was already checked for two claims sharing one object's sequence
    /// (<see cref="ClaimVerifier.ValidateSet"/>), so within one response these keys are distinct.</para>
    /// </summary>
    private static IEnumerable<ClaimRecord> Describe(IReadOnlyList<SignedClaim> claims) =>
        claims.Select(claim => new ClaimRecord
        {
            ObjectKey = claim.Payload.Identity.ToStorageKey(),
            AudienceKey = claim.Payload.Audience.ToStorageKey(),
            Seq = claim.Payload.Seq,
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(claim.PayloadBytes)),
            IsRevocation = claim.Payload.Kind == ClaimKind.Revocation,
            Describes = claim.Payload.Identity.Describe()
        });

    /// <summary>
    /// Reads this context's records.
    ///
    /// <para><b>Absent and damaged are different answers, and only the first may proceed.</b> A
    /// missing file is the ordinary state of a first fetch and must be allowed, or nothing could ever
    /// start. Anything else — unreadable, unparseable, empty, the wrong version, or stamped with a
    /// different trust context — is this machine's ratchet in an unknown position, and continuing
    /// would silently accept a rollback of everything published since. It refuses, and it does NOT
    /// rewrite the file: the position it would record is the one being replayed, which would make the
    /// loss permanent.</para>
    /// </summary>
    private (Dictionary<string, ClaimRecord> Records, bool FileAbsent) ReadRecords(
        string path, string trustContext, string pluginId)
    {
        byte[] bytes;
        try
        {
            // Bounded: these records are small, and a planted or damaged one must not be
            // buffered whole before anything can object to its size.
            bytes = BoundedFile.ReadAllBytes(path, MaxRecordFileBytes, "catalog history file");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Genuinely absent. Deliberately not File.Exists first: that answers a different question
            // a moment earlier, and a permission error from it would have read as absence.
            return (new Dictionary<string, ClaimRecord>(StringComparer.Ordinal), true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "The catalog history for {PluginId} exists but couldn't be read", pluginId);
            throw new InvalidOperationException(
                $"this machine's record of the catalog versions it has already accepted for " +
                $"'{pluginId}' couldn't be read, so an older catalog can't be told apart from a " +
                "current one. The catalog wasn't refreshed.", ex);
        }

        StoredRecords? document;
        try
        {
            // A zero-byte file lands here as a JsonException rather than as an empty success: a read
            // that WORKS and returns nothing is the third route to "no records", and the two above
            // are the only ones allowed to mean it.
            document = JsonSerializer.Deserialize<StoredRecords>(bytes, FileOptions);
        }
        catch (Exception ex)
        {
            throw Damaged(pluginId, ex);
        }

        if (document is null || document.Version != FileVersion || document.Records is null)
            throw Damaged(pluginId, null);

        // The file is named after the trust context, and it also carries it. A file copied or renamed
        // into another context's place would otherwise apply one plugin's history to another's
        // catalog.
        if (!string.Equals(document.TrustContext, trustContext, StringComparison.Ordinal))
            throw Damaged(pluginId, null);

        if (document.Records.Count > MaxRecords) throw Damaged(pluginId, null);

        var records = new Dictionary<string, ClaimRecord>(StringComparer.Ordinal);
        foreach (var record in document.Records)
        {
            // Every field the comparison depends on, checked before any of it is compared against.
            // A record that is present but says nothing is not a weaker record, it is an unknown
            // position — and an unknown position must not be read as permission.
            //
            // The sequence is held to the SAME range a signed claim is (ClaimCodec: 1..MaxCounter),
            // not merely "not negative". A record whose `seq` member is missing deserializes to
            // ZERO, which is below every real claim — so damage that erases one member silently
            // lowers this object's ceiling to nothing, and the next authentic-but-withdrawn claim
            // sails over it. A value above the maximum is the mirror image: it refuses every honest
            // claim for that object forever.
            if (string.IsNullOrEmpty(record.ObjectKey) ||
                record.AudienceKey is null ||
                record.Seq is < 1 or > ClaimCodec.MaxCounter ||
                record.PayloadSha256.Length != 64 ||
                !record.PayloadSha256.All(char.IsAsciiHexDigit))
            {
                throw Damaged(pluginId, null);
            }

            // Two records for one (object, sequence) is an ambiguity about a recorded position, so it
            // is damage rather than something to resolve by picking one.
            if (!records.TryAdd(record.Key, record)) throw Damaged(pluginId, null);
        }

        return (records, false);
    }

    /// <summary>
    /// A ceiling on how much history one context may accumulate. Records are only ever added, and
    /// what may be added is bounded by what the author signed — but "bounded by a remote document"
    /// is not a bound this machine controls, and a proof may carry
    /// <see cref="ClaimProof.MaxClaims"/> claims.
    /// </summary>
    private const int MaxRecords = ClaimProof.MaxClaims;

    /// <summary>Ten thousand records of roughly a couple of hundred bytes, with room to spare.</summary>
    private const int MaxRecordFileBytes = 8 * 1024 * 1024;

    private InvalidOperationException Damaged(string pluginId, Exception? cause)
    {
        _logger.Error(cause, "The catalog history for {PluginId} is damaged", pluginId);
        return new InvalidOperationException(
            $"this machine's record of the catalog versions it has already accepted for '{pluginId}' " +
            "is damaged, so an older catalog can't be told apart from a current one. The catalog " +
            "wasn't refreshed.", cause);
    }

    /// <summary>
    /// One file per trust context. The context is a hex SHA-256 produced by
    /// <see cref="ClaimTrustContext.Compute"/>, but it is re-checked rather than assumed before being
    /// used as a path: a value that reached this from somewhere else must not be able to name a file
    /// outside this directory.
    /// </summary>
    private string RecordPath(string trustContext)
    {
        if (trustContext.Length != 64 || !trustContext.All(char.IsAsciiHexDigitLower))
            throw new InvalidOperationException("the trust context is not a hex fingerprint");

        return Path.Combine(_directory, trustContext + ".json");
    }

    private const int FileVersion = 1;

    private sealed class StoredRecords
    {
        [JsonPropertyName("v")]
        public int Version { get; init; }

        [JsonPropertyName("trustContext")]
        public string? TrustContext { get; init; }

        [JsonPropertyName("records")]
        public List<ClaimRecord>? Records { get; init; }
    }

    private sealed class ClaimRecord
    {
        [JsonPropertyName("object")]
        public string ObjectKey { get; init; } = "";

        [JsonPropertyName("audience")]
        public string AudienceKey { get; init; } = "";

        [JsonPropertyName("seq")]
        public long Seq { get; init; }

        [JsonPropertyName("payload")]
        public string PayloadSha256 { get; init; } = "";

        /// <summary>
        /// True when this sequence was a withdrawal rather than an assertion. Revocations ride along
        /// on every publish forever, so they legitimately arrive far below an object's newest
        /// sequence; live claims never do.
        /// </summary>
        [JsonPropertyName("revoked")]
        public bool IsRevocation { get; init; }

        /// <summary>
        /// How to name this object when refusing — "version 1.2.0 (stable) of ptcgl". Written into
        /// the file so a refusal can name what it refused even when the offending claim is the one
        /// that is missing.
        /// </summary>
        [JsonPropertyName("describes")]
        public string Describes { get; init; } = "";

        /// <summary>
        /// (object, sequence). The audience is recorded but deliberately NOT part of the key: a
        /// sequence belongs to the object, and keying it by audience lets one object hold several
        /// independent ratchets that a server can play off against each other.
        /// </summary>
        [JsonIgnore]
        public string Key => ObjectKey + " " + Seq.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string Describe() => string.IsNullOrEmpty(Describes) ? "an entry" : Describes;
    }
}
