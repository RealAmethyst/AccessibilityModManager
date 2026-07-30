using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// What the signed registry has to say about where managers read a plugin's index.
/// </summary>
/// <param name="Listed">Whether the registry carries an entry for the plugin at all.</param>
/// <param name="Url">
/// The address, when there is a usable one. Null with <paramref name="Listed"/> true means the entry
/// exists but cannot say where managers read — which must stop a publish, not wave it through.
/// </param>
public readonly record struct RegisteredIndexAddress(bool Listed, string? Url)
{
    /// <summary>
    /// True when the only entry found differs from the requested id by capitalisation alone.
    ///
    /// <para>Its own state because it must stop a publish, and the reason is not obvious. Trust is
    /// matched exactly, so an entry cased differently anchors no key for THIS id — meaning signing
    /// could never switch on, and, worse, if that entry does carry an <c>indexTrust</c> and points
    /// at the same place, publishing here would put an unsigned index over a signed catalog. The
    /// disagreement is the problem, and it is fixed in the registry rather than worked around.</para>
    /// </summary>
    public bool IdCaseDiffers { get; init; }
}

/// <summary>
/// Attaches the signed <c>proof</c> block to an index about to be published, and owns the rule
/// that makes it safe to do so: publishing extends exactly the history this machine last confirmed,
/// or it does not happen.
///
/// Deliberately does no network I/O. The publish flow already fetches the signed registry (to check
/// it still points at this address) and the live index, so both inputs are handed in. Keeping this
/// pure makes the sequencing and revocation logic — the part where a mistake would be expensive and
/// invisible — directly testable.
/// </summary>
public sealed class IndexProofService(
    ClaimSigningKeyStore keyStore, PublisherHeadStore headStore, ILogger logger)
{
    private static readonly JsonSerializerOptions IndexOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Reads the trust anchor for a plugin out of the registry.
    ///
    /// The caller must have verified the registry's signature first: everything here — the key, the
    /// index address, the scheme — is only meaningful because the registry vouches for it. Returns
    /// null when the entry carries no <c>indexTrust</c>, which is the normal state before an author
    /// has published their signing key.
    /// </summary>
    public static ClaimTrustAnchor? TryReadAnchor(string verifiedRegistryJson, string pluginId)
    {
        using var document = JsonDocument.Parse(verifiedRegistryJson);
        if (!document.RootElement.TryGetProperty("plugins", out var plugins) ||
            plugins.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var plugin in plugins.EnumerateArray())
        {
            if (!plugin.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            if (!string.Equals(id.GetString(), pluginId, StringComparison.Ordinal)) continue;

            if (!plugin.TryGetProperty("indexTrust", out var trust) || trust.ValueKind != JsonValueKind.Object)
                return null;
            if (!plugin.TryGetProperty("repoIndexUrl", out var url) || url.ValueKind != JsonValueKind.String)
                return null;

            return new ClaimTrustAnchor
            {
                PluginId = pluginId,
                RepoIndexUrl = url.GetString()!,
                Scheme = RequiredString(trust, "scheme"),
                KeyId = RequiredString(trust, "keyId"),
                Algorithm = RequiredString(trust, "algorithm"),
                PublicKeyPem = RequiredString(trust, "publicKeyPem")
            };
        }

        return null;
    }

    /// <summary>
    /// Reads just the address the registry sends managers to for a plugin, independently of whether
    /// it anchors a signing key.
    ///
    /// <para>Separate from <see cref="TryReadAnchor"/> because an unsigned catalog needs this exact
    /// answer and gets null from that one: no <c>indexTrust</c> is precisely the state it reports
    /// nothing for. Publishing to an address the registry does not name is the quietest failure this
    /// tool has — everything reports success while every manager reads somewhere else — and it is
    /// worth catching whether or not the catalog is signed.</para>
    ///
    /// <para>The id is matched exactly first and then, failing that, ignoring case — unlike
    /// <see cref="TryReadAnchor"/>, which only ever matches exactly. That difference is deliberate
    /// and the two must not be made to agree: a trust anchor decides which key may sign for a
    /// plugin, and matching THAT loosely would let 'amethyst' and 'Amethyst' be one identity. The
    /// loose match here is not a fallback that lets publishing continue — it exists so the
    /// disagreement can be SEEN and refused. Reporting it as plain "not listed" would hide it, and
    /// what it hides is a catalog that can never be signed and might already be.</para>
    ///
    /// <para>The four answers are kept apart deliberately. Not listed is a normal state — a plugin
    /// nobody has added to the registry yet publishes perfectly well. Listed but with no usable
    /// address is not: it is an entry that exists and cannot say where managers read, and folding it
    /// in with "not listed" would publish on the strength of a check that could not be made. Listed
    /// under a different capitalisation is its own refusal, described on
    /// <see cref="RegisteredIndexAddress.IdCaseDiffers"/>. Only an exact entry with a usable address
    /// leads to an address comparison.</para>
    ///
    /// <para>The distinction is defence in depth rather than a live hole: the registry is
    /// deserialized into a model whose <c>RepoIndexUrl</c> is a required <c>Uri</c>, so today a
    /// malformed entry throws before any of this is reachable. That is a property of a different
    /// file, though, and this one should not quietly become permissive if it is ever relaxed.</para>
    ///
    /// <para>The caller must have verified the registry's signature first.</para>
    /// </summary>
    public static RegisteredIndexAddress TryReadIndexUrl(string verifiedRegistryJson, string pluginId)
    {
        using var document = JsonDocument.Parse(verifiedRegistryJson);
        if (!document.RootElement.TryGetProperty("plugins", out var plugins) ||
            plugins.ValueKind != JsonValueKind.Array)
        {
            return new RegisteredIndexAddress(Listed: false, null);
        }

        RegisteredIndexAddress? looseMatch = null;

        foreach (var plugin in plugins.EnumerateArray())
        {
            if (!plugin.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;

            var listedId = id.GetString();
            var exact = string.Equals(listedId, pluginId, StringComparison.Ordinal);
            if (!exact && !string.Equals(listedId, pluginId, StringComparison.OrdinalIgnoreCase)) continue;

            var url = plugin.TryGetProperty("repoIndexUrl", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

            if (exact) return new RegisteredIndexAddress(Listed: true, url);
            looseMatch ??= new RegisteredIndexAddress(Listed: true, url) { IdCaseDiffers = true };
        }

        return looseMatch ?? new RegisteredIndexAddress(Listed: false, null);
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ClaimFormatException($"the registry's indexTrust block has no '{name}'");
        return value.GetString()!;
    }

    /// <summary>
    /// A prepared publish: the exact bytes to send, what changed, and what this machine has already
    /// written down about the attempt.
    /// </summary>
    public sealed record PreparedPublish(
        byte[] IndexJson,
        ClaimSetBuilder.BuildResult Build,
        PendingPublish Pending);

    /// <summary>
    /// What an interrupted publish turned out to be, once the live index could be looked at again.
    /// </summary>
    public enum PendingOutcome
    {
        /// <summary>It landed. Commit the head and carry on.</summary>
        Landed,

        /// <summary>It never landed; the server still holds the head it was extending.</summary>
        NotSent,

        /// <summary>Neither — something else is published under this key.</summary>
        Diverged
    }

    /// <summary>
    /// Builds the index bytes with a fresh proof attached, and journals the attempt before
    /// returning them. Nothing else may upload a proof: the journal has to exist first, or a crash
    /// between the remote rename and the local commit erases the evidence that this machine may
    /// already have published — which is precisely what lets a rolled-back server walk the signer
    /// into issuing a second, different publish under one generation.
    /// </summary>
    /// <param name="localIndexJson">The index about to be published.</param>
    /// <param name="verifiedRegistryJson">
    /// The registry, with its signature ALREADY VERIFIED by the caller. The anchor is derived from
    /// it here rather than passed in beside it, so the two can never describe different things —
    /// and the registry's own freshness is checked first, because a replayed old registry is
    /// cryptographically perfect and names whatever address and key were current before a
    /// re-point or a rotation retired them.
    /// </param>
    /// <param name="pluginId">Which entry in that registry this index belongs to.</param>
    /// <param name="liveIndexJson">
    /// What is currently published, fetched over SFTP so the manifest is present — after the raw
    /// route closes, an HTTPS fetch returns a filtered response with the manifest stripped, which
    /// is indistinguishable from a server that deleted it. Null means the file genuinely is not
    /// there, PROVEN, not "the fetch failed": those two must never collapse into one value, because
    /// one of them leads to starting a history over.
    /// </param>
    /// <param name="allowBootstrap">
    /// The author has confirmed, deliberately and once, that this is the beginning of this
    /// catalog's signed history. Never inferred from the absence of a proof, which is also exactly
    /// what a server that deleted one looks like.
    /// </param>
    /// <param name="acknowledgeRestoredState">
    /// The author has been shown which publish this machine believes it is on, and has confirmed it
    /// is the latest one. Only consulted when the state came out of a backup and has not been
    /// confirmed by a publish since — see <see cref="PublisherHeadStore.IsRestoredAndUnconfirmed"/>
    /// for why a restore cannot authorise itself.
    /// </param>
    /// <param name="confirmedDeletions">
    /// The <see cref="ClaimSetBuilder.PublishPreview.DeletionsToken"/> the author was actually shown
    /// and agreed to. A withdrawn release version can never be published again, so this is not
    /// something to discover afterwards from a count — and it is a token rather than a yes, because
    /// between the preview and here the set can grow, and a bare yes would cover the growth too.
    /// </param>
    public PreparedPublish PreparePublish(
        byte[] localIndexJson, string verifiedRegistryJson, string pluginId,
        byte[]? liveIndexJson, bool allowBootstrap, bool acknowledgeRestoredState = false,
        string? confirmedDeletions = null)
    {
        headStore.RequireRegistryNotOlder(verifiedRegistryJson);

        var (anchor, index, indexText) = ReadInputs(verifiedRegistryJson, pluginId, localIndexJson);
        var trustContext = ClaimTrustContext.Compute(anchor);
        var record = headStore.TryLoad(trustContext);

        if (record?.Pending is not null)
        {
            throw new InvalidOperationException(
                "A previous publish was interrupted before this machine could confirm whether it " +
                "landed. That has to be settled first — publishing on top of it could sign a second " +
                "version of the same publish.");
        }

        // Restored state is authentic but not necessarily current, and nothing local can tell the
        // difference. If two publishes happened after the backup was taken, this machine believes
        // the earlier one — and a server replaying that same publish matches it exactly, so every
        // check below passes and the key signs a second version of a generation that already
        // exists. The author is the only one who knows whether the number they are looking at is
        // the latest, so they are asked once, and a confirmed publish clears it.
        // Plugin-wide, not context-wide. The doubt belongs to the key: a stale backup plus an
        // authentic later re-point puts this machine into a context with no record of its own, where
        // a per-context question would never be asked at all.
        if (headStore.HasUnconfirmedRestoredState(anchor.PluginId, trustContext) && !acknowledgeRestoredState)
        {
            throw new InvalidOperationException(
                $"This machine's publishing history was restored from a backup and hasn't been used " +
                $"to publish since. It believes the catalog is at publish " +
                $"{record?.Committed?.Generation.ToString() ?? "unknown"}. If you published again after " +
                "taking that backup, continuing from here would sign a second version of a publish " +
                "that already exists. Confirm this is where you left off before going on.");
        }

        var live = ReadLiveProof(liveIndexJson, anchor);
        if (live is null && record is null && allowBootstrap) RequireBootstrapPermitted(anchor, trustContext);
        var previous = RequireExpectedHead(live, record, allowBootstrap, liveIndexJson is not null);

        // The authoritative deletion check, made against the claim set this publish will really
        // extend. PreviewPublish shows the author the same answer beforehand, but it is advisory:
        // it reads whatever is live without applying the head rules, so only this one is allowed to
        // decide. If the two ever disagree, the publish stops here rather than going out.
        var preview = ClaimSetBuilder.Preview(index, previous);
        if (preview.HasPermanentRemovals &&
            !string.Equals(DeletionsToken(anchor, live?.Manifest?.PayloadHash, preview), confirmedDeletions,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "This publish would withdraw " +
                string.Join(", ", preview.RemovedReleases.Select(r => r.Describe())) +
                " from the published catalog. A withdrawn version can never be published again under " +
                "that number — after the withdrawal there is nothing left to check a re-publication " +
                "against. Confirm the removal before publishing, or put the version back." +
                (string.IsNullOrEmpty(confirmedDeletions)
                    ? ""
                    : " (What was agreed to is not what this publish would remove, so the earlier " +
                      "confirmation does not cover it.)"));
        }

        using var signer = keyStore.OpenSigner(anchor);
        var built = ClaimSetBuilder.Build(index, previous, signer);

        var generation = (live?.Manifest?.Manifest.Generation ?? 0) + 1;
        var manifest = signer.SignManifest(generation, live?.Manifest?.PayloadHash, ClaimDigest.Compute(built.Claims));

        var root = JsonNode.Parse(indexText)?.AsObject()
            ?? throw new InvalidOperationException("The index is not a JSON object.");
        root["proof"] = JsonSerializer.SerializeToNode(
            ClaimProof.Write(anchor, manifest, built.Claims), IndexOptions);

        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString(IndexOptions));

        // Read our own output back the way a consumer would, rather than through a lighter check
        // that can drift away from it. If what we just built is something a verifier would refuse,
        // that has to surface here, while the author is standing in front of it.
        SelfCheck(bytes, anchor);

        var pending = new PendingPublish
        {
            BaseManifestHash = record?.Committed?.ManifestHash,
            Generation = generation,
            ManifestHash = manifest.PayloadHash,
            IndexSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
        };

        headStore.WritePending(trustContext, anchor.PluginId, record?.Committed, pending, bytes);

        logger.Information(
            "Prepared publish {Generation} for {PluginId}: {Unchanged} unchanged, {Added} added, " +
            "{Updated} updated, {Revoked} revoked",
            generation, index.PluginId, built.Unchanged, built.Added, built.Updated, built.Revoked);

        return new PreparedPublish(bytes, built, pending);
    }

    /// <summary>
    /// What publishing this index would change, worked out without signing anything, journalling
    /// anything, or moving any high-water mark — so the author can be shown what a publish costs
    /// before the tool commits to it.
    ///
    /// <para>Advisory by design. It reads whatever proof is currently live rather than applying the
    /// head rules, because a preview that refused to answer until every publishing precondition was
    /// already satisfied could not be shown before the decision it exists to inform. The binding
    /// check is inside <see cref="PreparePublish"/>, made against the claim set the publish actually
    /// extends.</para>
    /// </summary>
    public PublishOutlook PreviewPublish(
        byte[] localIndexJson, string verifiedRegistryJson, string pluginId, byte[]? liveIndexJson)
    {
        headStore.CheckRegistryNotOlder(verifiedRegistryJson);

        var (anchor, index, _) = ReadInputs(verifiedRegistryJson, pluginId, localIndexJson);
        var live = ReadLiveProof(liveIndexJson, anchor);
        var changes = ClaimSetBuilder.Preview(index, live?.Claims ?? []);

        return new PublishOutlook(changes,
            DeletionsToken(anchor, live?.Manifest?.PayloadHash, changes));
    }

    /// <summary>What a publish would change, and the token that agreeing to it produces.</summary>
    public sealed record PublishOutlook(ClaimSetBuilder.PublishPreview Changes, string DeletionsToken);

    /// <summary>What is published under one anchor right now, as far as its proof can be believed.</summary>
    /// <param name="Present">Whether an index was served at all. False is proven absence, never a failed read.</param>
    /// <param name="Generation">Which publish the live proof says it is; null when there is no proof.</param>
    /// <param name="ManifestHash">
    /// The live head, to compare against this machine's committed one. Null when unsigned.
    /// </param>
    public sealed record LiveCatalogState(
        bool Present, long? Generation, string? ManifestHash, IReadOnlyList<SignedClaim> Claims)
    {
        /// <summary>True when a proof is there and it verified against the registry's key.</summary>
        public bool Signed => ManifestHash is not null;
    }

    /// <summary>
    /// The published catalog, rebuilt from its signed claims and nothing else.
    ///
    /// <para>Everything it reports is taken from the <see cref="VerifiedProof"/> it was built
    /// around, and that proof can only be produced by the verification code in another assembly. So
    /// this type cannot be assembled here at all — not from the live plaintext, not from a
    /// hand-written string, not by a later refactor that finds it convenient. The guarantee is the
    /// type's, not a comment's.</para>
    /// </summary>
    public sealed class VerifiedCatalog
    {
        private readonly SignedManifest _manifest;

        internal VerifiedCatalog(VerifiedProof proof)
        {
            _manifest = proof.Manifest
                ?? throw new InvalidOperationException(
                    "A published catalog is only meaningful with the manifest it was published " +
                    "under — without one there is no way to tell whether part of it was removed.");
            CatalogJson = proof.CatalogJson;
        }

        /// <summary>Which publish this is.</summary>
        public long Generation => _manifest.Manifest.Generation;

        /// <summary>The head to compare against this machine's own record.</summary>
        public string ManifestHash => _manifest.PayloadHash;

        /// <summary>
        /// Every field here came out of a signed claim. Nothing came from the document the proof
        /// travelled inside, and there is no proof in it.
        /// </summary>
        public string CatalogJson { get; }
    }

    /// <summary>
    /// What is published, as the claims describe it — for a caller about to write it into the
    /// author's project folder.
    ///
    /// <para>Null when there is no proof to read, and that null means "leave the local copy alone",
    /// never "adopt what was served". An unsigned published index is the ordinary state before an
    /// author switches signing on, and it is also exactly what a stripped proof looks like; neither
    /// is something to take a catalog from. A proof that exists and does not verify throws, because
    /// that is not a state at all.</para>
    /// </summary>
    public VerifiedCatalog? ReadPublishedCatalog(ClaimTrustAnchor anchor, byte[]? liveIndexJson)
    {
        var live = ReadLiveProof(liveIndexJson, anchor);
        return live?.Manifest is null ? null : new VerifiedCatalog(live);
    }

    /// <summary>
    /// The document to write into the author's folder: the verified catalog, plus the two things
    /// that are the author's own and must survive the trip.
    ///
    /// <para><c>generatedAt</c> is deliberately unsigned — a timestamp inside the signed set would
    /// either re-sign everything on every publish or announce that something hidden had changed — so
    /// the projection stands a fixed value in for it. Taking the server's would be adopting a field
    /// nothing vouches for; keeping the local one costs nothing and churns nothing.</para>
    ///
    /// <para>The author-only fields are kept for a sharper reason. No claim will ever cover a
    /// dependency preset, a default lifecycle script or a version-discovery rule, so nothing
    /// downstream protects them — and each one feeds something the author later signs. A server that
    /// edited a preset would be choosing a download URL and hash for the author to put their key
    /// behind, one plausible click later.</para>
    /// </summary>
    /// <param name="verified">
    /// Taken as the verified object rather than as its JSON, so a caller cannot hand this the live
    /// plaintext or anything else it happens to have — the argument itself has to have come from a
    /// checked proof.
    /// </param>
    public static byte[] BuildLocalDocument(VerifiedCatalog verified, byte[] localIndexJson)
    {
        var adopted = JsonNode.Parse(verified.CatalogJson)?.AsObject()
            ?? throw new InvalidOperationException("The verified catalog is not a JSON object.");
        var mine = JsonNode.Parse(localIndexJson)?.AsObject()
            ?? throw new InvalidOperationException("The local index is not a JSON object.");

        if (mine["generatedAt"] is { } generatedAt) adopted["generatedAt"] = generatedAt.DeepClone();

        AuthoringOnlyFields.RestoreFromLocal(adopted, mine);

        return Encoding.UTF8.GetBytes(adopted.ToJsonString(IndexOptions));
    }

    /// <summary>
    /// Whether the published document and the local one say the same thing, once the proof — the one
    /// part that is only ever added on the way out — is set aside.
    ///
    /// <para>This is what lets "there is nothing to publish" survive signing. The two files can no
    /// longer be compared byte for byte, because the published one carries a proof the local one
    /// never does; without this, every publish would look like a change, and with a careless version
    /// of it the FIRST signed publish would look like a no-op and be skipped.</para>
    ///
    /// <para>Both sides are re-serialized through the same writer before comparing, so indentation
    /// and escaping differences between what this tool wrote and what the author's editor saved do
    /// not read as a change. Anything that cannot be parsed answers "not the same", which only ever
    /// costs a publish that was going to happen anyway.</para>
    /// </summary>
    public static bool SameCatalogIgnoringProof(byte[] liveIndexJson, byte[] localIndexJson)
    {
        try
        {
            var live = JsonNode.Parse(liveIndexJson)?.AsObject();
            var local = JsonNode.Parse(localIndexJson)?.AsObject();
            if (live is null || local is null) return false;

            live.Remove("proof");

            return string.Equals(
                live.ToJsonString(IndexOptions), local.ToJsonString(IndexOptions), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Looks at what is live without preparing to replace it — so the author can be asked the right
    /// question, with the right numbers in it, before anything is signed or journalled.
    ///
    /// <para>Read-only in every sense that matters: no key is opened, no counter moves, no high-water
    /// mark is touched, and nothing is written down. It refuses on exactly the same terms
    /// <see cref="PreparePublish"/> does, because a proof that does not verify is not a state to
    /// report — it is a stop.</para>
    /// </summary>
    public LiveCatalogState InspectLive(ClaimTrustAnchor anchor, byte[]? liveIndexJson)
    {
        var live = ReadLiveProof(liveIndexJson, anchor);

        return new LiveCatalogState(
            Present: liveIndexJson is not null,
            Generation: live?.Manifest?.Manifest.Generation,
            ManifestHash: live?.Manifest?.PayloadHash,
            Claims: live?.Claims ?? []);
    }

    /// <summary>
    /// Ties a deletion confirmation to the exact thing that was agreed to: these versions, of this
    /// plugin, under this key and address, extending this catalog.
    ///
    /// A digest over the version numbers alone is not enough, and the ways it fails are ordinary
    /// rather than exotic. Two plugins withdrawing "1.1.0 stable of game1" produce the same list, so
    /// an agreement shown for one would be accepted for the other. A registry change that re-points
    /// or rotates retires the signing context entirely, and an agreement made before it must not
    /// survive into the context that replaces it. And if the live catalog moves between the question
    /// and the answer, the answer was about a different catalog.
    /// </summary>
    private static string DeletionsToken(
        ClaimTrustAnchor anchor, string? baseManifestHash, ClaimSetBuilder.PublishPreview changes)
    {
        if (!changes.HasPermanentRemovals) return "";

        var parts = new List<string>
        {
            anchor.PluginId,
            ClaimTrustContext.Compute(anchor),
            baseManifestHash ?? ""
        };
        parts.AddRange(changes.RemovedReleaseKeys);

        // Length-prefixed, so no arrangement of one field's contents can imitate another's.
        var preimage = new StringBuilder("amm-deletion-consent-v1\n");
        foreach (var part in parts) preimage.Append(Encoding.UTF8.GetByteCount(part)).Append(':').Append(part).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preimage.ToString())));
    }

    /// <summary>
    /// The anchor and the index, checked against each other. Shared so a preview and the publish it
    /// previews can never be reading two different things.
    /// </summary>
    private static (ClaimTrustAnchor Anchor, PluginRepoIndex Index, string IndexText) ReadInputs(
        string verifiedRegistryJson, string pluginId, byte[] localIndexJson)
    {
        var anchor = TryReadAnchor(verifiedRegistryJson, pluginId)
            ?? throw new InvalidOperationException(
                $"The registry has no signing key recorded for plugin '{pluginId}', so there is nothing " +
                "to sign this index against yet.");

        var indexText = Encoding.UTF8.GetString(localIndexJson);
        var index = JsonSerializer.Deserialize<PluginRepoIndex>(indexText, IndexOptions)
            ?? throw new InvalidOperationException("The index could not be read.");

        if (!string.Equals(index.PluginId, anchor.PluginId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This index is for plugin '{index.PluginId}' but the registry entry being signed against " +
                $"is '{anchor.PluginId}'. Publishing it would produce claims no manager would accept.");
        }

        return (anchor, index, indexText);
    }

    /// <summary>
    /// Confirms that what is now live is exactly what was sent, and only then records it as this
    /// machine's head. Comparing the whole document rather than the manifest alone: the manifest
    /// commits to the claims, and the rest of the index — the compatibility plaintext every current
    /// manager actually reads — is not covered by it.
    /// </summary>
    public void ConfirmPublished(ClaimTrustAnchor anchor, byte[] liveIndexJson)
    {
        var trustContext = ClaimTrustContext.Compute(anchor);
        var record = headStore.TryLoad(trustContext)
            ?? throw new InvalidOperationException("There is no record of a publish to confirm.");
        var pending = record.Pending
            ?? throw new InvalidOperationException("There is no pending publish to confirm.");

        var actual = Convert.ToHexStringLower(SHA256.HashData(liveIndexJson));
        if (!string.Equals(actual, pending.IndexSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The index now on the server is not the one that was just published. Something else " +
                "wrote to it, or the upload did not complete. Nothing has been recorded on this " +
                "machine, so the publish can be repeated once that is understood.");
        }

        headStore.Commit(trustContext, anchor.PluginId,
            new PublisherHead { Generation = pending.Generation, ManifestHash = pending.ManifestHash });
    }

    /// <summary>
    /// Works out what an interrupted publish actually did, by comparing what is live against the
    /// journal. The caller acts on the answer: a landed publish is committed, an unsent one is
    /// retried by re-sending the journaled bytes VERBATIM — never by rebuilding, because RSA-PSS is
    /// randomised and a rebuild produces different bytes under the same generation, which is the
    /// fork this all exists to prevent arriving through the recovery path.
    /// </summary>
    public PendingOutcome ResolvePending(ClaimTrustAnchor anchor, byte[]? liveIndexJson)
    {
        var trustContext = ClaimTrustContext.Compute(anchor);
        var record = headStore.TryLoad(trustContext)
            ?? throw new InvalidOperationException("There is no record of a publish to resolve.");
        var pending = record.Pending
            ?? throw new InvalidOperationException("There is no pending publish to resolve.");

        var live = ReadLiveProof(liveIndexJson, anchor);
        var liveHead = live?.Manifest?.PayloadHash;

        // "Landed" has to mean the whole document, not just the proof inside it. The manifest
        // commits to the claims; the compatibility plaintext beside them — which is what every
        // current manager actually reads — is not covered by it, so a server could keep the proof
        // intact, rewrite the plaintext, and have an interrupted publish committed as though it had
        // gone out unaltered.
        if (string.Equals(liveHead, pending.ManifestHash, StringComparison.Ordinal))
        {
            var actual = liveIndexJson is null ? null : Convert.ToHexStringLower(SHA256.HashData(liveIndexJson));
            return string.Equals(actual, pending.IndexSha256, StringComparison.Ordinal)
                ? PendingOutcome.Landed
                : PendingOutcome.Diverged;
        }

        if (string.Equals(liveHead, pending.BaseManifestHash, StringComparison.Ordinal)) return PendingOutcome.NotSent;
        return PendingOutcome.Diverged;
    }

    /// <summary>Commits a pending publish that <see cref="ResolvePending"/> found had landed.</summary>
    public void CommitPending(ClaimTrustAnchor anchor)
    {
        var trustContext = ClaimTrustContext.Compute(anchor);
        var record = headStore.TryLoad(trustContext)
            ?? throw new InvalidOperationException("There is no record of a publish to commit.");
        var pending = record.Pending
            ?? throw new InvalidOperationException("There is no pending publish to commit.");

        headStore.Commit(trustContext, anchor.PluginId,
            new PublisherHead { Generation = pending.Generation, ManifestHash = pending.ManifestHash });
    }

    /// <summary>
    /// The exact bytes an interrupted publish had prepared, for re-sending unchanged — checked
    /// against the hash journalled beside them, so bytes that were damaged on disk are refused
    /// rather than re-uploaded as though they were the ones that were signed.
    /// </summary>
    public byte[] ReadPendingIndex(ClaimTrustAnchor anchor)
    {
        var trustContext = ClaimTrustContext.Compute(anchor);
        var record = headStore.TryLoad(trustContext)
            ?? throw new InvalidOperationException("There is no record of a publish to resume.");
        var pending = record.Pending
            ?? throw new InvalidOperationException("There is no pending publish to resume.");

        var bytes = headStore.TryReadPendingIndex(trustContext)
            ?? throw new InvalidOperationException(
                "The prepared index for the interrupted publish is missing, so it can't be re-sent. " +
                "It must not be rebuilt — a rebuild signs different bytes under a number that may " +
                "already be published.");

        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), pending.IndexSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The prepared index for the interrupted publish is not the one that was prepared. " +
                "It can't be re-sent, and rebuilding it is not safe.");
        }

        return bytes;
    }

    /// <summary>
    /// The head this publish is allowed to extend — which is the one this machine confirmed, and
    /// nothing else.
    ///
    /// "At or above the local generation" reads as the tolerant, sensible rule and is unsafe. A
    /// server that hides the newest publish and serves a complete older one puts the tool back on a
    /// head it has already moved past, and the next publish signs a second, different version of a
    /// generation that already exists. Every claim in it verifies, the digest is whole, and the
    /// catalog has forked under one key. So: equal, or stop and let the author decide what they are
    /// looking at.
    /// </summary>
    /// <summary>
    /// Whether starting a fresh signed history here is something this machine is entitled to do.
    ///
    /// The author confirming it is necessary but not sufficient. They are being asked "is this the
    /// beginning?" in a situation that is indistinguishable, from the outside, from a server having
    /// deleted what was published — so the question is only fair to ask when this machine can
    /// actually account for the key's past. Two cases where it cannot:
    ///
    /// <para><b>A history already published to this exact address.</b> Whatever the trust context,
    /// the address is the thing a consumer resolves. A record for it means something was published
    /// there and the proof is now gone: data loss or tampering, never a first publish. Re-pointing
    /// to a NEW address is the one case where restarting is right, and it is the reason this is
    /// judged on the address rather than on "has this key ever published".</para>
    ///
    /// <para><b>A key that came from a backup carrying no publishing records.</b> A backup taken
    /// before the first publish restores nothing, so the machine ends up looking exactly like one
    /// holding a brand-new key while the real catalog may be many publishes along. Nothing local can
    /// tell those apart, and the permissive reading is the one that reuses every counter.</para>
    /// </summary>
    private void RequireBootstrapPermitted(ClaimTrustAnchor anchor, string trustContext)
    {
        // Mostly subsumed by the imported-key rule below, since restored state only ever arrives
        // through an import — but not entirely, and the gap it covers is the real one: a config
        // written before the key's origin was recorded reads as "made here", and this check is what
        // still catches it. It also says something more specific when it does fire.
        //
        // Bootstrapping means there is no record for this context, so the check below is asking its
        // widest question: is ANY of this key's history only believed? That is the right one here —
        // starting a history at an address this machine cannot account for is exactly the act a
        // stale backup makes look reasonable.
        if (headStore.HasUnconfirmedRestoredState(anchor.PluginId, trustContext))
        {
            throw new InvalidOperationException(
                "This machine's publishing history was restored from a backup and hasn't been used " +
                "to publish since, so nothing here can say whether that backup was the latest one. " +
                "Starting a new signed history from that position would reissue numbers that may " +
                "already be in use. Publish once from the machine that has been publishing — or take " +
                "a fresh backup there and import that — before starting anything new here.");
        }

        // An imported key may never begin a history, whatever else this machine holds.
        //
        // The tempting narrower rule — refuse only when the backup carried NO records — leaves the
        // hole open, because the records a backup carries say nothing about the ones it does not. A
        // backup taken while publishing at one address knows nothing about a second address
        // published to afterwards; restore it, publish once at the first address (which CLEARS the
        // restored mark, because that publish really was confirmed), and the machine now looks
        // entirely healthy: a genuine record, nothing unconfirmed, and no memory of the second
        // address at all. Re-point there, let the server withhold the index, and the key signs a
        // second generation 1 over a history it cannot see.
        //
        // Only the machine that MADE the key can know it has never published anywhere. So a new
        // address is begun there, and travels to other machines the way everything else does — in a
        // backup taken afterwards.
        if (keyStore.WasImported(anchor.PluginId))
        {
            throw new InvalidOperationException(
                "This key came from a backup, so this machine cannot know whether anything has " +
                "already been published at this address. A backup carries the history it was taken " +
                "with, and says nothing about what happened afterwards or anywhere else — so " +
                "starting a new signed history from here could reissue numbers that are already in " +
                "use. Publish this address for the first time on the machine that created the key, " +
                "then bring a fresh backup across.");
        }
    }

    /// <param name="liveIndexPresent">
    /// Whether an index was actually served, as opposed to none being there. Only consulted when
    /// there is no usable proof, and it is the difference between two situations that used to arrive
    /// at this method looking identical: nothing has ever been published here, versus something is
    /// published and its proof is missing. The second is what a server that stripped a proof looks
    /// like, and it is also what an index from before claims existed looks like, so it cannot be
    /// resolved from here — but it can at least be said out loud instead of being folded into "there
    /// is nothing published".
    /// </param>
    private static IReadOnlyList<SignedClaim> RequireExpectedHead(
        VerifiedProof? live, PublisherRecord? record, bool allowBootstrap, bool liveIndexPresent)
    {
        var committed = record?.Committed;

        if (live is null)
        {
            var whatIsThere = liveIndexPresent
                ? "the index on the server carries no proof at all"
                : "the server is offering no index at all";

            if (committed is not null)
            {
                throw new InvalidOperationException(
                    $"This machine published generation {committed.Generation} of this catalog, but " +
                    $"{whatIsThere}. That is either tampering or data loss, and publishing over the " +
                    "top of it would restart every version counter. Nothing has been changed.");
            }

            if (record is not null)
            {
                throw new InvalidOperationException(
                    $"This machine has a publishing record for this catalog but {whatIsThere}. " +
                    "Publishing now would restart the history.");
            }

            if (!allowBootstrap)
            {
                throw new InvalidOperationException(
                    liveIndexPresent
                        ? "The index on the server carries no signed history yet. Starting one is a " +
                          "deliberate step, because an index published before signing existed and an " +
                          "index whose proof has been stripped look exactly alike from here."
                        : "There is no signed history for this catalog yet. Starting one is a " +
                          "deliberate step, because 'there is nothing published' and 'the server " +
                          "deleted what was published' look exactly alike from here.");
            }

            return [];
        }

        if (committed is null)
        {
            throw new InvalidOperationException(
                "There is a signed catalog on the server, but this machine has no record of " +
                "publishing it. That happens on a new computer, after restoring a profile, or if " +
                "the record was lost — and it is also what a rolled-back server looks like, so it " +
                "cannot be adopted automatically. Bring this machine's publishing state across with " +
                "the key, or adopt what is published as a deliberate recovery.");
        }

        var liveHead = live.Manifest?.PayloadHash;
        if (!string.Equals(liveHead, committed.ManifestHash, StringComparison.Ordinal))
        {
            var liveGeneration = live.Manifest?.Manifest.Generation;
            throw new InvalidOperationException(
                $"The catalog on the server is not the one this machine last published. It says " +
                $"publish {liveGeneration?.ToString() ?? "unknown"}; this machine confirmed " +
                $"{committed.Generation}. Publishing on top of it would sign version numbers that " +
                "may already be in use elsewhere. Nothing has been changed.");
        }

        return live.Claims;
    }

    /// <summary>
    /// Verified claims from the live index.
    ///
    /// A proof that exists but does NOT verify stops everything. It means one of two things: the
    /// registry now names a different key, or the live file was altered. Neither is safe to paper
    /// over — and treating it as "no previous claims" would restart sequences from one, which is
    /// exactly the equivocation the whole mechanism exists to prevent.
    /// </summary>
    private VerifiedProof? ReadLiveProof(byte[]? liveIndexJson, ClaimTrustAnchor anchor)
    {
        // Null means the file is proven absent. A file that exists and is empty is not an absence —
        // it is a damaged or truncated index, and letting it reach the same branch would let a
        // zero-byte write become "there is no history here".
        if (liveIndexJson is null) return null;

        ClaimProofDocument? document;
        try
        {
            document = ClaimProof.TryExtract(liveIndexJson);
        }
        catch (Exception ex) when (ex is JsonException or ClaimFormatException)
        {
            throw new InvalidOperationException(
                "The index currently on the server could not be read, so its claim history can't be " +
                "continued. Publishing now could reuse version numbers already in use.", ex);
        }

        if (document is null)
        {
            logger.Information("The live index carries no proof block");
            return null;
        }

        try
        {
            return ClaimProof.ReadVerified(document, anchor, requireManifest: true);
        }
        catch (ClaimFormatException ex)
        {
            throw new InvalidOperationException(
                "The proof on the server's index doesn't verify against the key the registry currently " +
                "names. Either the registry was re-pointed at a different key, or that file has been " +
                "altered. Publishing would reissue version numbers and break every manager that has " +
                $"already seen the current ones.\n\nDetail: {ex.Message}", ex);
        }
    }

    private static void SelfCheck(byte[] indexBytes, ClaimTrustAnchor anchor)
    {
        var document = ClaimProof.TryExtract(indexBytes)
            ?? throw new InvalidOperationException("The proof just written could not be read back.");

        ClaimProof.ReadVerified(document, anchor, requireManifest: true);
    }
}
