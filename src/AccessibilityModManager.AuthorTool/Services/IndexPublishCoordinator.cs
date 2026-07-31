using AccessibilityModManager.Infrastructure.CatalogClaims;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// The published index, read the only way anyone acting on this machine's own publishing state is
/// allowed to read it.
///
/// <para>Over SFTP, never HTTPS: once the read API serves filtered catalogs an HTTP fetch returns a
/// document with the manifest stripped by design, and that is indistinguishable — from here — from a
/// server that deleted it.</para>
/// </summary>
public interface IPublishedIndexReader
{
    /// <summary>
    /// Absence must be proven, never inferred from a failed read — see
    /// <see cref="ServerUploadService.RemoteIndex"/>.
    /// </summary>
    Task<ServerUploadService.RemoteIndex> ReadIndexAsync(string pluginId, CancellationToken ct);
}

/// <summary>The server, reduced to the four things publishing needs from it.</summary>
public interface IPublishTransport : IPublishedIndexReader
{
    Task<ServerUploadService.PublishLockHandle> AcquireLockAsync(string pluginId, CancellationToken ct);

    Task<PublishLockRelease> ReleaseLockAsync(ServerUploadService.PublishLockHandle handle, CancellationToken ct);

    /// <summary>
    /// Uploads and switches, running <paramref name="beforeSwitchAsync"/> in the gap between the two.
    /// Must throw <see cref="IndexPublishFailedException"/> so the caller can tell a switch that was
    /// never reached from one that may have happened.
    /// </summary>
    Task PublishIndexAsync(
        string pluginId, byte[] indexJson, Func<Task> beforeSwitchAsync, CancellationToken ct);
}

/// <summary>
/// A publish carried out as far as the point of no return, and then abandoned on purpose.
///
/// <para>Separate from <see cref="IPublishTransport"/> because publishing must never be able to
/// reach it and it must never be able to reach publishing. Nothing that implements this has a route
/// to a rename.</para>
/// </summary>
public interface IPublishRehearsal
{
    Task RehearseAsync(string pluginId, Func<Task> beforeSwitchAsync, CancellationToken ct);
}

/// <summary>The signed registry, fetched and its signature checked, or an explained refusal.</summary>
public interface IVerifiedRegistrySource
{
    /// <exception cref="RegistryUnusableException">It could not be fetched, or could not be trusted.</exception>
    Task<string> ReadVerifiedAsync(string pluginId, CancellationToken ct);
}

/// <summary>
/// The registry could not be turned into something safe to act on — it could not be fetched, or it
/// was fetched and could not be trusted. Publishing treats both the same way, and the reason is
/// worth writing down.
///
/// <para>The tempting rule is that an unreachable registry is survivable for a catalog with no local
/// signing key: nothing here can sign, so nothing here can be signing. It is wrong, and the way it
/// fails is ordinary. A second machine — a fresh install, a restored profile, the laptop — has the
/// project and the server credentials but has not imported the key yet. With the registry readable
/// it is told plainly that it cannot sign for this catalog, and stops. With the registry blocked it
/// concludes the catalog is unsigned and publishes plaintext straight over the signed one. Local
/// absence of a key is not evidence of remote absence of a key, and anyone who can drop one HTTPS
/// request gets to decide which of those the tool believes.</para>
/// </summary>
public sealed class RegistryUnusableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Which question the author is being asked, so callers and tests can tell them apart.</summary>
public enum PublishQuestion
{
    /// <summary>This machine's history came from a backup; is the publish it names really the latest?</summary>
    RestoredState,

    /// <summary>Nothing signed is published yet; start the history here?</summary>
    StartSignedHistory,

    /// <summary>These release versions are being withdrawn, and can never be published again.</summary>
    PermanentDeletion,

    /// <summary>An earlier publish never reached the server; send the exact bytes it prepared?</summary>
    ResumeInterrupted,

    /// <summary>The ordinary "this goes live now" check.</summary>
    Ordinary
}

/// <summary>A question for the author, already worded. Titles and messages are read aloud.</summary>
public sealed record PublishConfirmation(PublishQuestion Question, string Title, string Message);

public enum PublishStatus
{
    /// <summary>Switched live, read back, and recorded.</summary>
    Published,

    /// <summary>The live catalog already says exactly this. Nothing was sent.</summary>
    AlreadyUpToDate,

    /// <summary>An interrupted publish turned out to have landed, and has now been recorded.</summary>
    Recovered,

    /// <summary>
    /// This plugin has no signing key anchored in the registry, so the caller's unsigned publish
    /// path applies unchanged. Not a failure — it is today's normal state.
    /// </summary>
    NotSigned,

    /// <summary>The author said no. Nothing was sent.</summary>
    Cancelled,

    /// <summary>Stopped before anything was sent, and the live index is untouched.</summary>
    Refused,

    /// <summary>The registry anchors a key this machine does not have.</summary>
    SigningKeyMissing,

    /// <summary>Somebody else is publishing this plugin.</summary>
    LockHeld,

    /// <summary>
    /// The switch may have happened. Nothing may be assumed about the live index until the next
    /// publish resolves it.
    /// </summary>
    Interrupted,

    /// <summary>Publishing is blocked until an interrupted or divergent history is sorted out.</summary>
    RecoveryRequired
}

/// <summary>What the publish did, worded for the author.</summary>
public sealed record PublishResult(PublishStatus Status, string Title, string Message)
{
    /// <summary>Which publish this now is, when one went out.</summary>
    public long? Generation { get; init; }

    /// <summary>
    /// True when this publish began the signed history. The moment it does, every key backup taken
    /// before it stops being able to recover the thing it was taken for: it holds the key but no
    /// publishing head, and an imported key with no head may never start or continue a history.
    /// </summary>
    public bool StartedHistory { get; init; }

    /// <summary>
    /// Whether the live catalog now describes exactly the candidate this publish was handed —
    /// the only condition under which the caller may record that local file as published.
    ///
    /// <para>Not the same question as "did a publish succeed", and the difference is the whole
    /// reason this exists. A resumed publish sends the bytes an interrupted attempt signed, which
    /// is deliberately NOT the current local file: anything edited since goes out on the publish
    /// after it. So it succeeds, a generation goes live, and the local file is still unpublished
    /// work. Recording it as published there would tell the next project-open that this folder is
    /// merely stale — and project-open replaces stale folders without asking. The same holds for
    /// discovering that an interrupted publish had landed.</para>
    ///
    /// <para>"Describes", not "equals": the published document carries the proof and the local one
    /// never does, so the two are never byte-identical once signing is on.</para>
    /// </summary>
    public bool LocalSourceIsLive { get; init; }

    /// <summary>
    /// The signed registry this decision was made against, carried out on
    /// <see cref="PublishStatus.NotSigned"/> so the unsigned path can finish its own checks against
    /// a document whose signature has been verified, rather than fetching the registry again and
    /// deciding what to do when that fetch fails.
    /// </summary>
    public string? VerifiedRegistryJson { get; init; }
}

/// <summary>What to publish, and how much to ask about it.</summary>
public sealed record PublishRequest(string PluginId, byte[] Candidate)
{
    /// <summary>
    /// Whether to ask the short "this goes live now" question. Off when the caller has already
    /// asked it as part of a larger action. It is never the only thing asked: a deletion or a first
    /// signed publish is confirmed regardless.
    /// </summary>
    public bool ConfirmOrdinary { get; init; } = true;

    /// <summary>What changed, in the author's own words, for the ordinary confirmation.</summary>
    public string? ChangeSummary { get; init; }
}

/// <summary>
/// Owns the publish, from taking the lock to recording the head.
///
/// <para>It lives here rather than in a view model because the ordering IS the security property.
/// Every confirmation happens before the journal is written, and after the journal there is no
/// dialog, no cancellation and no path that quietly gives up — a rule that can be stated in one
/// sentence and, in this shape, tested in one assertion. Spread across command handlers it would be
/// a convention instead, and conventions do not fail builds.</para>
///
/// <para>The other rule worth stating outright: the ANCHOR decides whether a publish is signed, not
/// the presence of a key. A plugin with no <c>indexTrust</c> in the signed registry publishes
/// exactly as it always has, so creating a key locally cannot break publishing, and the switch to
/// signed catalogs is one deliberate registry publication rather than a side effect.</para>
/// </summary>
public sealed class IndexPublishCoordinator(
    ClaimSigningKeyStore keyStore,
    PublisherHeadStore headStore,
    IndexProofService proofs,
    ILogger logger)
{
    /// <summary>
    /// Where managers are told to read this plugin's index — derived from the registry's fixed home,
    /// which is the same anchor the manager itself is built around.
    /// </summary>
    public static Uri CanonicalIndexUrl(string pluginId) => new(
        RegistryMembershipChecker.RegistryUrl, $"plugins/{Uri.EscapeDataString(pluginId)}/index.json");

    /// <summary>
    /// Publishes, or explains why it did not.
    ///
    /// <para>Outcomes are returned, not thrown. An exception escaping this method therefore means
    /// nothing was published: every path from the journal onwards — including one where the switch
    /// may have landed — returns an <see cref="PublishStatus.Interrupted"/> result rather than
    /// throwing. Callers may show an escaped message as a plain failure, and some of them are worth
    /// showing: this machine's own publishing record refuses to be read rather than be mistaken for
    /// "this key has never published", and says so in words meant for the author.</para>
    /// </summary>
    /// <param name="confirm">
    /// Asks the author one question and returns their answer. Called only before the journal is
    /// written — after that there is nothing left to decide.
    /// </param>
    /// <param name="ct">
    /// Cancels the part where stopping is still free. Nothing after the journal takes it.
    /// </param>
    public async Task<PublishResult> PublishAsync(
        IPublishTransport transport, IVerifiedRegistrySource registry, PublishRequest request,
        Func<PublishConfirmation, bool> confirm, CancellationToken ct)
    {
        var pluginId = request.PluginId;

        // ---- preflight: better errors, no authority whatsoever ----

        string registryJson;
        try
        {
            registryJson = await registry.ReadVerifiedAsync(pluginId, ct);
        }
        catch (RegistryUnusableException ex)
        {
            return new PublishResult(PublishStatus.Refused, "Couldn't check the registry",
                $"{ex.Message}\n\nThe registry is what says whether this catalog is signed, which key " +
                "signs it, and where managers read it, so publishing without it is refused. Nothing " +
                "was uploaded.");
        }

        var resolution = IndexProofService.ResolveAnchor(registryJson, pluginId);

        // Present and broken is its own refusal, and it must come before the no-anchor branch below.
        // Reading it as "no anchor" would take the unsigned path — and that path is only guarded by
        // this machine having publishing records, so on a machine that has none (a replacement, or a
        // first publish) a malformed entry would publish plaintext over a catalog the registry says
        // is signed. Same shape as the bugs fixed on 30 July: checked against local state, which is
        // empty in exactly the situation the check exists for.
        if (resolution.Status == IndexTrustStatus.Unusable)
        {
            return new PublishResult(PublishStatus.Refused,
                "The registry's signing key can't be used",
                $"{resolution.Reason}\n\nThe registry is what says which key signs this catalog, so " +
                "publishing without being able to read that is refused. Nothing was uploaded.");
        }

        if (resolution.Status == IndexTrustStatus.None)
        {
            // No anchor and a signed history behind us is not "this catalog is unsigned" — it is the
            // registry having moved backwards, or the entry having been edited. Publishing plaintext
            // over a signed catalog would strand every manager that has already seen a proof, and it
            // cannot be undone by publishing again.
            if (headStore.RecordsFor(pluginId).Count > 0)
            {
                return new PublishResult(PublishStatus.RecoveryRequired,
                    "The registry no longer names a signing key",
                    $"This machine has published '{pluginId}' with a signing key, but the registry " +
                    "now carries no key for it. Publishing an unsigned index over a signed catalog " +
                    "would break every manager that has already read the signed one, and there is no " +
                    "way to undo it. Nothing was uploaded.");
            }

            return new PublishResult(PublishStatus.NotSigned,
                "Not signed", $"The registry anchors no signing key for '{pluginId}'.")
            {
                VerifiedRegistryJson = registryJson
            };
        }

        // Anchored is the only state left that may continue, and it is asked for BY NAME rather than
        // inferred from an anchor being present. Anything else means nobody actually asked the
        // registry, and an unasked question is not an answer — least of all the one that grants the
        // unsigned path.
        if (resolution.Status != IndexTrustStatus.Anchored || resolution.Anchor is not { } anchor)
        {
            return new PublishResult(PublishStatus.Refused, "The registry wasn't checked",
                "The signing key for this catalog was never resolved from the registry, so there is " +
                "nothing to publish against. Nothing was uploaded.");
        }

        if (IndexUrlMismatch(anchor.RepoIndexUrl, pluginId) is { } mismatch)
            return new PublishResult(PublishStatus.Refused, "The registry points somewhere else", mismatch);

        // An early identity check only, for a clearer message than a failure four steps later would
        // give. PreparePublish opens the signer again and is the one that decides.
        try
        {
            keyStore.OpenSigner(anchor).Dispose();
        }
        catch (Exception ex)
        {
            return new PublishResult(PublishStatus.SigningKeyMissing,
                "This machine can't sign for this catalog",
                $"{ex.Message}\n\nImport the key backup for '{pluginId}' before publishing. Nothing was uploaded.");
        }

        // ---- everything from here happens under the publish lock ----

        ServerUploadService.PublishLockHandle handle;
        try
        {
            handle = await transport.AcquireLockAsync(pluginId, ct);
        }
        catch (PublishLockHeldException ex)
        {
            return new PublishResult(PublishStatus.LockHeld, "Publish in progress", ex.Message);
        }
        catch (Exception ex)
        {
            return new PublishResult(PublishStatus.Refused, "Couldn't take the publish lock",
                $"{ex.Message}\n\nNothing was uploaded.");
        }

        try
        {
            return await PublishUnderLockAsync(transport, registry, request, confirm, ct);
        }
        finally
        {
            await ReleaseAsync(transport, handle);
        }
    }

    private async Task<PublishResult> PublishUnderLockAsync(
        IPublishTransport transport, IVerifiedRegistrySource registry, PublishRequest request,
        Func<PublishConfirmation, bool> confirm, CancellationToken ct)
    {
        var pluginId = request.PluginId;

        // Read the registry again now that nothing else can be publishing. The preflight copy was
        // fetched before the lock and grants nothing; this is the one every decision below is made
        // against.
        string registryJson;
        try
        {
            registryJson = await registry.ReadVerifiedAsync(pluginId, ct);
        }
        catch (RegistryUnusableException ex)
        {
            return new PublishResult(PublishStatus.Refused, "Couldn't check the registry",
                $"{ex.Message}\n\nNothing was uploaded.");
        }

        var resolution = IndexProofService.ResolveAnchor(registryJson, pluginId);
        if (resolution.Status == IndexTrustStatus.Unusable)
        {
            return new PublishResult(PublishStatus.Refused, "The registry changed",
                $"The registry's signing key for this plugin stopped being usable between opening " +
                $"the publish and taking the lock: {resolution.Reason}\n\nNothing was uploaded.");
        }

        // Anchored or nothing, asked for by name: None means the key went away mid-publish, and any
        // other state means the question was never asked. Both refuse here, and neither continues.
        if (resolution.Status != IndexTrustStatus.Anchored || resolution.Anchor is not { } anchor)
        {
            return new PublishResult(PublishStatus.Refused, "The registry changed",
                "The registry stopped naming a signing key for this plugin between opening the " +
                "publish and taking the lock. Nothing was uploaded.");
        }

        if (IndexUrlMismatch(anchor.RepoIndexUrl, pluginId) is { } mismatch)
            return new PublishResult(PublishStatus.Refused, "The registry points somewhere else", mismatch);

        try
        {
            keyStore.OpenSigner(anchor).Dispose();
        }
        catch (Exception ex)
        {
            return new PublishResult(PublishStatus.SigningKeyMissing,
                "This machine can't sign for this catalog", $"{ex.Message}\n\nNothing was uploaded.");
        }

        var trustContext = ClaimTrustContext.Compute(anchor);

        // Over SFTP, never HTTPS: once the read API serves filtered catalogs, an HTTP fetch returns
        // a document with the manifest stripped by design, which from here is indistinguishable
        // from a server that deleted it.
        byte[]? liveBytes;
        try
        {
            var live = await transport.ReadIndexAsync(pluginId, ct);
            liveBytes = live.Present ? live.Bytes : null;
        }
        catch (Exception ex)
        {
            // A read that failed is not an index that is absent, and only one of those is permission
            // to start a history over.
            return new PublishResult(PublishStatus.Refused, "Couldn't read the published index",
                $"{ex.Message}\n\nNothing was uploaded.");
        }

        // ---- an unsettled attempt outranks everything else ----

        switch (ScanUnsettled(pluginId, trustContext))
        {
            case { Blocked: { } blocked }:
                return blocked;

            case { Resumable: true }:
                return await ResolveInterruptedAsync(transport, registry, anchor, liveBytes, confirm);
        }

        // ---- what is live, and may this machine extend it ----

        IndexProofService.LiveCatalogState state;
        try
        {
            state = proofs.InspectLive(anchor, liveBytes);
        }
        catch (Exception ex)
        {
            return new PublishResult(PublishStatus.RecoveryRequired,
                "The published proof is not trusted", $"{ex.Message}\n\nNothing was uploaded.");
        }

        var record = headStore.TryLoad(trustContext);
        var committed = record?.Committed;
        if (HeadObjection(state, record, pluginId) is { } objection) return objection;

        var startsHistory = !state.Signed;

        // ---- what publishing would cost ----

        IndexProofService.PublishOutlook outlook;
        try
        {
            outlook = proofs.PreviewPublish(request.Candidate, registryJson, pluginId, liveBytes);
        }
        catch (Exception ex)
        {
            return new PublishResult(PublishStatus.Refused, "Couldn't work out what this would publish",
                $"{ex.Message}\n\nNothing was uploaded.");
        }

        if (outlook.Changes.BlockedReleases.Count > 0)
        {
            return new PublishResult(PublishStatus.Refused, "Some releases can't be published",
                "These versions were withdrawn from the published catalog before, and a withdrawn " +
                "version can never be published again under that number:\n\n" +
                string.Join("\n", outlook.Changes.BlockedReleases.Select(r => r.Describe())) +
                "\n\nGive them new version numbers. Nothing was uploaded.");
        }

        // Idempotence, defined so it can never swallow the first signed publish: the live catalog
        // must already be signed, nothing signed may change, and what is live must say exactly what
        // the local file says once the proof is set aside. An unsigned live index fails the first
        // condition, so switching signing on always publishes.
        if (state.Signed && NothingSignedChanges(outlook.Changes) &&
            liveBytes is not null && IndexProofService.SameCatalogIgnoringProof(liveBytes, request.Candidate))
        {
            return new PublishResult(PublishStatus.AlreadyUpToDate, "Nothing to publish",
                $"The live catalog is already publish {state.Generation} of this index.")
            {
                Generation = state.Generation,
                // Established by the two conditions just checked, not assumed: nothing signed
                // differs, and what is live says exactly what the candidate says once the proof is
                // set aside.
                LocalSourceIsLive = true
            };
        }

        // ---- every question, now, while stopping is still free ----

        var acknowledgedRestored = false;
        if (headStore.HasUnconfirmedRestoredState(pluginId, trustContext))
        {
            if (!confirm(RestoredStateQuestion(pluginId, committed?.Generation))) return Cancelled();
            acknowledgedRestored = true;
        }

        if (startsHistory && !confirm(StartHistoryQuestion(state.Present)))
            return Cancelled();

        if (outlook.Changes.HasPermanentRemovals && !confirm(DeletionQuestion(outlook.Changes)))
            return Cancelled();

        if (request.ConfirmOrdinary && !startsHistory && !outlook.Changes.HasPermanentRemovals &&
            !confirm(OrdinaryQuestion(pluginId, request.ChangeSummary, (state.Generation ?? 0) + 1)))
        {
            return Cancelled();
        }

        // ---- the journal boundary ----

        IndexProofService.PreparedPublish prepared;
        try
        {
            // Journalling is the last thing PreparePublish does, so a throw from it means nothing
            // was written down and nothing has to be resolved later.
            prepared = proofs.PreparePublish(
                request.Candidate, registryJson, pluginId, liveBytes,
                allowBootstrap: startsHistory,
                acknowledgeRestoredState: acknowledgedRestored,
                confirmedDeletions: outlook.DeletionsToken);
        }
        catch (Exception ex)
        {
            return new PublishResult(PublishStatus.Refused, "Publish stopped",
                $"{ex.Message}\n\nNothing was uploaded.");
        }

        // Past this point the author is not asked anything, cannot cancel, and nothing takes a
        // cancellation token. Closing the tool from here is a crash, and the journal is what makes
        // that recoverable.
        return await SendAsync(transport, registry, anchor, trustContext, pluginId,
            prepared.IndexJson, prepared.Pending.Generation, committed, startsHistory,
            newlyPrepared: true);
    }

    /// <summary>
    /// Uploads, switches, reads back and commits — the part with no way out.
    /// </summary>
    /// <param name="newlyPrepared">
    /// Whether the journal describes an attempt made just now, which is the only case where it may
    /// be dropped. A resend is recovering an attempt whose fate was already once in doubt, and
    /// throwing its exact signed bytes away would leave a generation that can only be rebuilt — and
    /// a rebuild signs different bytes under a number that may already be published.
    /// </param>
    private async Task<PublishResult> SendAsync(
        IPublishTransport transport, IVerifiedRegistrySource registry, ClaimTrustAnchor anchor,
        string trustContext, string pluginId, byte[] indexJson, long generation,
        PublisherHead? committed, bool startsHistory, bool newlyPrepared)
    {
        try
        {
            await transport.PublishIndexAsync(pluginId, indexJson,
                () => RequireRegistryStillCurrentAsync(registry, trustContext, pluginId),
                CancellationToken.None);
        }
        catch (IndexPublishFailedException ex) when (!ex.RenameAttempted && newlyPrepared)
        {
            // The one place a journal is ever dropped: the switch was provably never reached, so
            // the live index is the one it always was and this attempt never existed.
            headStore.DiscardPending(trustContext, pluginId, committed);
            logger.Information("Publish {Generation} for {PluginId} stopped before the switch", generation, pluginId);

            return new PublishResult(PublishStatus.Refused, "Publish stopped — nothing was uploaded", ex.Message);
        }
        catch (Exception ex)
        {
            // Everything else, including an exception this method did not expect: assume the switch
            // may have run. Being wrong in this direction costs a recovery pass. Being wrong in the
            // other direction destroys the only evidence that this machine may have published.
            logger.Error(ex, "Publish {Generation} for {PluginId} was interrupted", generation, pluginId);
            return Interrupted(generation, ex.Message);
        }

        byte[]? readBack;
        try
        {
            var live = await transport.ReadIndexAsync(pluginId, CancellationToken.None);
            readBack = live.Present ? live.Bytes : null;
        }
        catch (Exception ex)
        {
            return Interrupted(generation, ex.Message);
        }

        if (readBack is null) return Interrupted(generation, "The server offered no index afterwards.");

        try
        {
            proofs.ConfirmPublished(anchor, readBack);
        }
        catch (Exception ex)
        {
            return Interrupted(generation, ex.Message);
        }

        logger.Information("Published generation {Generation} of {PluginId}", generation, pluginId);

        return new PublishResult(PublishStatus.Published, "Published",
            newlyPrepared
                ? $"Publish {generation} is live, and reading it back off the server confirmed it."
                : $"Publish {generation} is live, and reading it back off the server confirmed it. " +
                  "It sent what the interrupted attempt had prepared, so anything edited since is " +
                  "still unpublished — choose Publish again to send it.")
        {
            Generation = generation,
            StartedHistory = startsHistory,
            // A resend puts the JOURNALLED bytes live, which predate whatever is in the folder now.
            LocalSourceIsLive = newlyPrepared
        };
    }

    /// <summary>
    /// The last look before the switch, run with the upload already staged.
    ///
    /// Checking this before the upload instead would leave the whole transfer between the check and
    /// the switch — long enough for a re-point or a key rotation to become live, after which this
    /// machine would publish into a context the registry has retired.
    /// </summary>
    private async Task RequireRegistryStillCurrentAsync(
        IVerifiedRegistrySource registry, string trustContext, string pluginId)
    {
        var fresh = await registry.ReadVerifiedAsync(pluginId, CancellationToken.None);

        // Refuses a registry older than one this machine has already acted on, and records this
        // one. A replayed old registry is cryptographically perfect and names whatever address
        // and key were current before a re-point retired them.
        headStore.RequireRegistryNotOlder(fresh);

        var freshResolution = IndexProofService.ResolveAnchor(fresh, pluginId);
        var freshAnchor = freshResolution.Status switch
        {
            IndexTrustStatus.Anchored => freshResolution.Anchor!,
            IndexTrustStatus.Unusable => throw new InvalidOperationException(
                "The registry's signing key for this plugin stopped being usable while the index " +
                $"was uploading ({freshResolution.Reason}). Nothing was switched live."),
            _ => throw new InvalidOperationException(
                "The registry stopped naming a signing key for this plugin while the index was " +
                "uploading. Nothing was switched live.")
        };

        if (!string.Equals(ClaimTrustContext.Compute(freshAnchor), trustContext, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The registry changed while the index was uploading — the address or the signing " +
                "key it names is no longer the one this publish was signed for. Nothing was " +
                "switched live.");
        }

        // Implied by the trust context matching, since the address is part of it. Checked anyway:
        // the cost is one comparison, and the failure it guards against is publishing to an
        // address nobody reads.
        if (IndexUrlMismatch(freshAnchor.RepoIndexUrl, pluginId) is { } mismatch)
            throw new InvalidOperationException(mismatch);
    }

    /// <summary>
    /// Whether an earlier attempt is still unsettled, and whether this one may deal with it.
    /// </summary>
    /// <param name="Blocked">Set when publishing must stop until a person sorts it out.</param>
    /// <param name="Resumable">
    /// Set when the single unsettled attempt belongs to the context being published to now, so it
    /// can be checked against the live index and either committed or re-sent.
    /// </param>
    private readonly record struct UnsettledScan(PublishResult? Blocked, bool Resumable);

    /// <summary>
    /// Looks for an attempt this publish is not allowed to step over.
    ///
    /// <para>Scanned across every trust context for the plugin, not only the current one. A pending
    /// entry under a different context means the registry changed before that attempt was settled,
    /// and the current anchor cannot resolve it: those bytes were signed for an address or a key
    /// that is no longer in use, so the live index cannot even be read in the terms they were made
    /// in. Asking only about the current context would let an unsettled publish under a retired
    /// context be ignored entirely — the same scoping mistake, keyed on the context instead of on
    /// the plugin, that has produced the worst findings in this design so far.</para>
    /// </summary>
    private UnsettledScan ScanUnsettled(string pluginId, string trustContext)
    {
        var unsettled = headStore.RecordsFor(pluginId).Where(r => r.Pending is not null).ToList();
        if (unsettled.Count == 0) return new UnsettledScan(null, false);

        if (unsettled.Count > 1)
        {
            return new UnsettledScan(new PublishResult(
                PublishStatus.RecoveryRequired, "More than one publish is unfinished",
                $"This machine has {unsettled.Count} unfinished publishes recorded for '{pluginId}'. " +
                "Each one may or may not have gone out, so none of them can be settled automatically. " +
                "Nothing was uploaded."), false);
        }

        var only = unsettled[0];
        if (string.Equals(only.TrustContext, trustContext, StringComparison.Ordinal))
            return new UnsettledScan(null, true);

        return new UnsettledScan(new PublishResult(
            PublishStatus.RecoveryRequired, "An earlier publish was never finished",
            $"Publish {only.Pending!.Generation} of '{pluginId}' was prepared but never confirmed, and " +
            "the registry has changed since — it now names a different address or signing key. That " +
            "attempt cannot be checked from here, because the index it was signed for is not the one " +
            "this registry points at. Nothing was uploaded."), false);
    }

    /// <summary>
    /// Settles an interrupted publish before any new one is considered.
    /// </summary>
    private async Task<PublishResult> ResolveInterruptedAsync(
        IPublishTransport transport, IVerifiedRegistrySource registry, ClaimTrustAnchor anchor,
        byte[]? liveBytes, Func<PublishConfirmation, bool> confirm)
    {
        var trustContext = ClaimTrustContext.Compute(anchor);
        var record = headStore.TryLoad(trustContext)!;
        var pending = record.Pending!;

        IndexProofService.PendingOutcome outcome;
        try
        {
            outcome = proofs.ResolvePending(anchor, liveBytes);
        }
        catch (Exception ex)
        {
            return new PublishResult(PublishStatus.RecoveryRequired, "Couldn't check the interrupted publish",
                $"{ex.Message}\n\nNothing was uploaded.");
        }

        switch (outcome)
        {
            case IndexProofService.PendingOutcome.Landed:
                proofs.CommitPending(anchor);
                return new PublishResult(PublishStatus.Recovered, "That publish did go out",
                    $"Publish {pending.Generation} is live and verified — it reached the server before " +
                    "the interruption, and this machine has now recorded it. If your local copy has " +
                    "changed since, choose Publish again to send it.")
                {
                    Generation = pending.Generation,
                    // Discovering that the first publish landed starts the history just as surely as
                    // watching it land does, and stales the pre-bootstrap backup exactly the same way.
                    StartedHistory = pending.BaseManifestHash is null
                };

            case IndexProofService.PendingOutcome.NotSent:
                byte[] prepared;
                try
                {
                    prepared = proofs.ReadPendingIndex(anchor);
                }
                catch (Exception ex)
                {
                    return new PublishResult(PublishStatus.RecoveryRequired,
                        "The interrupted publish can't be resumed", $"{ex.Message}\n\nNothing was uploaded.");
                }

                if (!confirm(new PublishConfirmation(PublishQuestion.ResumeInterrupted,
                        "Resume the interrupted publish?",
                        $"Publish {pending.Generation} never reached the server. Resuming sends exactly " +
                        "what it prepared, which does not include anything you have edited since — " +
                        "those changes go out on the publish after it.\n\nResume now?")))
                {
                    return new PublishResult(PublishStatus.RecoveryRequired, "Still unfinished",
                        $"Publish {pending.Generation} is still unfinished, so no new publish can be " +
                        "made until it is resumed. Nothing was uploaded.");
                }

                return await SendAsync(transport, registry, anchor, trustContext, anchor.PluginId,
                    prepared, pending.Generation, record.Committed,
                    // A resumed attempt that had no parent IS the first publish, and finishing it
                    // stales every backup taken before it just as much as a fresh bootstrap would.
                    startsHistory: pending.BaseManifestHash is null,
                    newlyPrepared: false);

            default:
                return new PublishResult(PublishStatus.RecoveryRequired, "Recovery stopped",
                    $"What is on the server is neither publish {pending.Generation} nor the publish it " +
                    "was based on. Publishing over it, or replacing it, could leave two valid " +
                    "histories signed by the same key. Nothing was changed. Import a current " +
                    "publishing backup, or rotate the key and re-anchor it in the registry.");
        }
    }

    /// <summary>
    /// Whether the live catalog is the one this machine is entitled to extend.
    ///
    /// <para>PreparePublish decides this authoritatively; this is the same judgement made early, so
    /// the author gets the numbers rather than a refusal from four steps further in — and so the
    /// right question gets asked before anything is signed.</para>
    /// </summary>
    private static PublishResult? HeadObjection(
        IndexProofService.LiveCatalogState state, PublisherRecord? record, string pluginId)
    {
        var committed = record?.Committed;

        if (state.Signed)
        {
            if (committed is null)
            {
                return new PublishResult(PublishStatus.RecoveryRequired, "This machine didn't publish that",
                    $"There is a signed catalog on the server for '{pluginId}', but this machine has no " +
                    "record of publishing it. That happens on a new computer, after restoring a " +
                    "profile, or if the record was lost — and it is also what a rolled-back server " +
                    "looks like, so it can't be adopted automatically. Import this machine's " +
                    "publishing backup. Nothing was uploaded.");
            }

            if (!string.Equals(state.ManifestHash, committed.ManifestHash, StringComparison.Ordinal))
            {
                return new PublishResult(PublishStatus.RecoveryRequired, "Catalog history differs",
                    $"The server says publish {state.Generation?.ToString() ?? "unknown"}. This machine " +
                    $"last confirmed publish {committed.Generation}. Publishing on top of that could " +
                    "reuse version numbers that are already in use. Nothing was changed. Import a " +
                    "current key backup, or rotate the key and re-anchor it in the registry.");
            }

            return null;
        }

        // Any record at all, not only a committed head. A record whose head is null is still this
        // machine saying it has acted under this context, and "acted, but nothing signed is live"
        // has exactly the two explanations above — neither of which is a first publish.
        if (record is not null)
        {
            var whatIsThere = state.Present
                ? "the index on the server now carries no proof at all"
                : "the server is offering no index at all";

            return new PublishResult(PublishStatus.RecoveryRequired, "The signed catalog is gone",
                committed is not null
                    ? $"This machine published {committed.Generation} of '{pluginId}', but {whatIsThere}. " +
                      "That is either tampering or data loss, and publishing over it would restart " +
                      "every version counter. Nothing was changed."
                    : $"This machine has a publishing record for '{pluginId}', but {whatIsThere}. " +
                      "Publishing now would restart the history. Nothing was changed.");
        }

        return null;
    }

    private static bool NothingSignedChanges(ClaimSetBuilder.PublishPreview changes) =>
        changes.Added == 0 && changes.Updated == 0 &&
        changes.RemovedReleases.Count == 0 && changes.RemovedGames.Count == 0 &&
        changes.Narrowed.Count == 0 && changes.BlockedReleases.Count == 0;

    /// <summary>
    /// Whether the registry sends managers to the address this tool publishes to.
    ///
    /// Scheme and host are case-insensitive by definition; the PATH is not — the catalog is served
    /// off a Linux filesystem, where /plugins/Amethyst/ and /plugins/amethyst/ are two different
    /// places, and comparing them loosely would call a real mismatch a match.
    /// </summary>
    public static string? IndexUrlMismatch(string registeredUrl, string pluginId)
    {
        var target = CanonicalIndexUrl(pluginId);

        if (!Uri.TryCreate(registeredUrl, UriKind.Absolute, out var registered))
        {
            return $"The registry's index address for '{pluginId}' isn't a usable address:\n\n" +
                   $"{registeredUrl}\n\nNothing was uploaded.";
        }

        var sameOrigin = Uri.Compare(registered, target,
            UriComponents.Scheme | UriComponents.HostAndPort,
            UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
        var samePath = Uri.Compare(registered, target,
            UriComponents.PathAndQuery,
            UriFormat.Unescaped, StringComparison.Ordinal) == 0;
        if (sameOrigin && samePath) return null;

        return $"The signed registry tells managers to read '{pluginId}' from:\n\n{registered}\n\n" +
               $"but publishing here would write to:\n\n{target}\n\n" +
               "Publishing now would look like it worked while every manager kept reading the old " +
               "address. Update the plugin's index URL in the registry admin screen (then sign and " +
               "publish the registry) so the two match. Nothing was uploaded.";
    }

    private async Task ReleaseAsync(IPublishTransport transport, ServerUploadService.PublishLockHandle handle)
    {
        try
        {
            var release = await transport.ReleaseLockAsync(handle, CancellationToken.None);
            if (release != PublishLockRelease.Released)
                logger.Warning("The publish lock came back {Release}", release);
        }
        catch (Exception ex)
        {
            // A lock that would not come back does not unpublish anything. Saying the publish failed
            // because of it would be a lie in the direction that matters most.
            logger.Warning(ex, "Couldn't release the publish lock");
        }
    }

    private static PublishResult Cancelled() => new(PublishStatus.Cancelled,
        "Publish cancelled", "Nothing was uploaded, and the published catalog is unchanged.");

    private static PublishResult Interrupted(long generation, string detail) => new(
        PublishStatus.Interrupted, "Publish interrupted",
        $"Publish {generation} may or may not have reached the server, so nothing can be assumed " +
        $"about what is live. Choose Publish again to find out and finish it.\n\nDetail: {detail}")
    {
        Generation = generation
    };

    private static PublishConfirmation RestoredStateQuestion(string pluginId, long? generation) => new(
        PublishQuestion.RestoredState, "Is this where you left off?",
        $"This machine's publishing history for '{pluginId}' came out of a backup and hasn't been " +
        $"used to publish since. It believes the catalog is at publish " +
        $"{generation?.ToString() ?? "the very beginning"}.\n\n" +
        "If you published again after taking that backup, continuing from here would sign a second " +
        "version of a publish that already exists. Nothing on this machine can tell the difference — " +
        "only you can.\n\nIs that really the latest publish?");

    private static PublishConfirmation StartHistoryQuestion(bool livePresent) => new(
        PublishQuestion.StartSignedHistory, "Start the signed history?",
        (livePresent
            ? "The index currently published carries no signature. This replaces it with signed publish 1."
            : "There is nothing published yet. This starts the catalog at signed publish 1.") +
        "\n\nEvery later publish has to continue this key's history, and a lost key means the catalog " +
        "can't be updated until a new one is anchored in the registry.\n\nStart it?");

    private static PublishConfirmation DeletionQuestion(ClaimSetBuilder.PublishPreview changes) => new(
        PublishQuestion.PermanentDeletion, "This permanently withdraws releases",
        "Publishing this withdraws:\n\n" +
        string.Join("\n", changes.RemovedReleases.Select(r => r.Describe())) +
        "\n\nA withdrawn version can never be published again under that number — after the " +
        "withdrawal there is nothing left to check a re-publication against. Anyone who already " +
        "installed it keeps it; nobody else will be offered it again.\n\nWithdraw them?");

    private static PublishConfirmation OrdinaryQuestion(
        string pluginId, string? changeSummary, long generation) => new(
        PublishQuestion.Ordinary, "Publish index",
        $"This publishes '{pluginId}' as signed publish {generation}. Managers see the change on " +
        "their next refresh." +
        (string.IsNullOrWhiteSpace(changeSummary) ? "" : $"\n\nChange: {changeSummary}") +
        "\n\nProceed?");
}

/// <summary>The real server, behind the publish state machine's four operations.</summary>
public sealed class ServerUploadPublishTransport(ServerUploadService uploads, ServerUploadConfig cfg)
    : IPublishTransport, IPublishRehearsal
{
    public Task RehearseAsync(string pluginId, Func<Task> beforeSwitchAsync, CancellationToken ct) =>
        uploads.RehearseIndexPublishAsync(cfg, pluginId, beforeSwitchAsync, ct);

    public Task<ServerUploadService.PublishLockHandle> AcquireLockAsync(string pluginId, CancellationToken ct) =>
        uploads.AcquirePublishLockAsync(cfg, pluginId, ct);

    public Task<PublishLockRelease> ReleaseLockAsync(
        ServerUploadService.PublishLockHandle handle, CancellationToken ct) =>
        uploads.ReleasePublishLockAsync(cfg, handle, ct);

    public Task<ServerUploadService.RemoteIndex> ReadIndexAsync(string pluginId, CancellationToken ct) =>
        uploads.ReadPluginIndexAsync(cfg, pluginId, ct);

    public Task PublishIndexAsync(
        string pluginId, byte[] indexJson, Func<Task> beforeSwitchAsync, CancellationToken ct) =>
        uploads.PublishIndexAsync(cfg, pluginId, indexJson, beforeSwitchAsync, ct);
}

/// <summary>The real registry, fetched and signature-checked before anything may be read out of it.</summary>
public sealed class RegistryVerifiedSource(RegistryMembershipChecker checker) : IVerifiedRegistrySource
{
    public async Task<string> ReadVerifiedAsync(string pluginId, CancellationToken ct)
    {
        var result = await checker.CheckAsync(pluginId, ct);

        if (result.SignatureFailed)
        {
            throw new RegistryUnusableException(
                "The registry's signature didn't verify, so nothing in it can be believed — including " +
                "which key signs this catalog and where managers read it.");
        }

        if (!result.RegistryReachable)
            throw new RegistryUnusableException($"The registry couldn't be read: {result.Error}");

        return result.VerifiedJson ?? throw new RegistryUnusableException(
            "The registry was read but never verified.");
    }
}
