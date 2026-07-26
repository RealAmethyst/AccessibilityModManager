using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

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
    public PreparedPublish PreparePublish(
        byte[] localIndexJson, string verifiedRegistryJson, string pluginId,
        byte[]? liveIndexJson, bool allowBootstrap, bool acknowledgeRestoredState = false)
    {
        headStore.RequireRegistryNotOlder(verifiedRegistryJson);

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
        if (headStore.IsRestoredAndUnconfirmed(trustContext) && !acknowledgeRestoredState)
        {
            throw new InvalidOperationException(
                $"This machine's publishing history was restored from a backup and hasn't been used " +
                $"to publish since. It believes the catalog is at publish " +
                $"{record?.Committed?.Generation.ToString() ?? "unknown"}. If you published again after " +
                "taking that backup, continuing from here would sign a second version of a publish " +
                "that already exists. Confirm this is where you left off before going on.");
        }

        var live = ReadLiveProof(liveIndexJson, anchor);
        var previous = RequireExpectedHead(live, record, allowBootstrap);

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
    private static IReadOnlyList<SignedClaim> RequireExpectedHead(
        VerifiedProof? live, PublisherRecord? record, bool allowBootstrap)
    {
        var committed = record?.Committed;

        if (live is null)
        {
            if (committed is not null)
            {
                throw new InvalidOperationException(
                    $"This machine published generation {committed.Generation} of this catalog, but " +
                    "the server is now offering nothing at all. That is either tampering or data " +
                    "loss, and publishing over the top of it would restart every version counter. " +
                    "Nothing has been changed.");
            }

            if (record is not null)
            {
                throw new InvalidOperationException(
                    "This machine has a publishing record for this catalog but the server has no " +
                    "proof at all. Publishing now would restart the history.");
            }

            if (!allowBootstrap)
            {
                throw new InvalidOperationException(
                    "There is no signed history for this catalog yet. Starting one is a deliberate " +
                    "step, because 'there is nothing published' and 'the server deleted what was " +
                    "published' look exactly alike from here.");
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
