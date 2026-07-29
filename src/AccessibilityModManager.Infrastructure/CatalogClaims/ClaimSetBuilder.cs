using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// Turns an index into the set of signed claims published alongside it.
///
/// Two properties drive the whole design:
///
/// <b>Unchanged objects keep their existing claim.</b> Bytes, signature and sequence are reused
/// untouched. That is not an optimisation — if public claims were re-signed on every publish, the
/// anonymous response would change every time a patron-only release was added, and the timing of
/// those changes would disclose exactly the hidden activity this design exists to conceal.
///
/// <b>Sequences come from what is already published.</b> The previous claim set is the authority,
/// not any local counter, so a restored project backup, a second machine, or an abandoned publish
/// cannot hand out a sequence that has already been used.
/// </summary>
public static class ClaimSetBuilder
{
    /// <summary>What changed in a build, for reporting back to the author.</summary>
    public sealed record BuildResult(
        IReadOnlyList<SignedClaim> Claims,
        int Unchanged,
        int Added,
        int Updated,
        int Revoked);

    /// <summary>
    /// One object this index asserts, as it will be signed. Produced by <see cref="Plan"/> from the
    /// index alone — no history, no key, nothing that could differ between asking what a publish
    /// would do and doing it.
    /// </summary>
    public sealed record PlannedClaim(
        ClaimKind Kind, ClaimIdentity Identity, ClaimAudience Audience, string BodyJson);

    /// <summary>
    /// What a publish would do, worked out without signing anything or writing anything down.
    ///
    /// This exists because <see cref="Build"/> cannot answer the question. By the time it has
    /// returned a <see cref="BuildResult"/> with a revocation count, the claims are signed and the
    /// caller has journalled the attempt — so a warning built from that number would be shown after
    /// the decision it is warning about.
    /// </summary>
    /// <param name="RemovedReleases">
    /// Versions this publish withdraws. Permanent: a withdrawn release version can never be
    /// published again, because after the deletion the proof keeps the revocation but not the body
    /// or its package hash, so nothing remains to check a re-publication against. This is the list
    /// the author has to see and agree to.
    /// </param>
    /// <param name="RemovedGames">
    /// Games this publish withdraws. Reversible — a removed game may be re-added — so it is
    /// reported but does not need the same consent.
    /// </param>
    /// <param name="Narrowed">
    /// Objects somebody could see before and cannot now. Nobody is losing the object, but somebody
    /// is losing access to it.
    /// </param>
    /// <param name="BlockedReleases">
    /// Versions the index re-asserts that were already withdrawn. <see cref="Build"/> refuses these,
    /// so surfacing them here turns a failure part-way through publishing into something the author
    /// is told before they start.
    /// </param>
    public sealed record PublishPreview(
        IReadOnlyList<ClaimIdentity> RemovedReleases,
        IReadOnlyList<ClaimIdentity> RemovedGames,
        IReadOnlyList<ClaimIdentity> Narrowed,
        IReadOnlyList<ClaimIdentity> BlockedReleases,
        int Unchanged,
        int Added,
        int Updated)
    {
        /// <summary>True when this publish retires something that can never come back.</summary>
        public bool HasPermanentRemovals => RemovedReleases.Count > 0;

        /// <summary>
        /// The withdrawn identities in a fixed order, for whoever is binding a confirmation to them.
        ///
        /// Deliberately not hashed here. An identity is a game, a channel and a version — it does
        /// not name the plugin, the key, the address or the catalog state, so a digest computed at
        /// this level would be equal for two different plugins withdrawing the same version number,
        /// and equal across a registry change that retired the signing context. The binding belongs
        /// where those are known.
        /// </summary>
        public IReadOnlyList<string> RemovedReleaseKeys =>
            [.. RemovedReleases.Select(r => r.ToStorageKey()).OrderBy(k => k, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The objects this index asserts, in the order they are claimed.
    ///
    /// Split out so the preview and the signed set are the same list read twice, rather than two
    /// enumerations that agree today. A release the preview forgot to mention would be a release the
    /// author was never warned about losing.
    /// </summary>
    public static IReadOnlyList<PlannedClaim> Plan(PluginRepoIndex index)
    {
        var plan = new List<PlannedClaim>
        {
            // repoVersion is in here because the manager's model requires it to read an index at
            // all, and the published catalog is projected from these claims — an unsigned copy of a
            // required field would be one a server could set freely. generatedAt deliberately is
            // NOT: a timestamp inside the signed set would either re-sign the header on every
            // publish or announce that something hidden had changed.
            new(ClaimKind.Header,
                new ClaimIdentity { Kind = ClaimKind.Header },
                ClaimAudience.Everyone,
                Canonicalise(new
                {
                    pluginId = index.PluginId,
                    repoVersion = index.RepoVersion,
                    author = index.Author
                }))
        };

        foreach (var game in index.Games.OrderBy(g => g.GameId, StringComparer.Ordinal))
        {
            plan.Add(new PlannedClaim(
                ClaimKind.Game,
                new ClaimIdentity { Kind = ClaimKind.Game, GameId = game.GameId },
                // Game metadata is public; whether it is DISCLOSED depends on whether any release
                // under it is visible, which is a serving decision, not a signing one. A game whose
                // only builds are patron-only must not appear for someone who cannot see them.
                ClaimAudience.Everyone,
                PublishedGameBody(game)));

            if (!index.ReleasesByGameId.TryGetValue(game.GameId, out var releases)) continue;

            foreach (var release in releases.OrderBy(r => r.Version, StringComparer.Ordinal)
                         .ThenBy(r => r.Channel, StringComparer.Ordinal))
            {
                plan.Add(new PlannedClaim(
                    ClaimKind.Release,
                    new ClaimIdentity
                    {
                        Kind = ClaimKind.Release,
                        GameId = game.GameId,
                        Channel = release.Channel,
                        Version = release.Version
                    },
                    AudienceFor(release),
                    Canonicalise(release)));
            }
        }

        return plan;
    }

    /// <summary>
    /// What publishing this index over that history would do — the same comparisons
    /// <see cref="Build"/> makes, with nothing signed and nothing recorded.
    /// </summary>
    public static PublishPreview Preview(PluginRepoIndex index, IReadOnlyList<SignedClaim> previousClaims)
    {
        var previousByIdentity = GroupByIdentity(previousClaims);
        var plan = Plan(index);
        var planned = new HashSet<string>(plan.Select(p => p.Identity.ToStorageKey()), StringComparer.Ordinal);

        List<ClaimIdentity> narrowed = [], blocked = [], removedReleases = [], removedGames = [];
        int unchanged = 0, added = 0, updated = 0;

        foreach (var item in plan)
        {
            previousByIdentity.TryGetValue(item.Identity.ToStorageKey(), out var history);
            var previous = LiveClaim(history);

            if (Newest(history)?.Payload.Kind == ClaimKind.Revocation &&
                item.Identity.Kind == ClaimKind.Release)
            {
                blocked.Add(item.Identity);
                continue;
            }

            if (previous is null) { added++; continue; }

            if (string.Equals(previous.Payload.BodyJson, item.BodyJson, StringComparison.Ordinal) &&
                previous.Payload.Audience.SameAs(item.Audience))
            {
                unchanged++;
                continue;
            }

            updated++;
            if (IsNarrowing(previous.Payload.Audience, item.Audience)) narrowed.Add(item.Identity);
        }

        foreach (var (key, history) in previousByIdentity)
        {
            if (planned.Contains(key)) continue;

            // Already withdrawn: nothing is being retired by this publish that was not retired
            // before it, so there is nothing to warn about.
            var last = Newest(history)!;
            if (last.Payload.Kind == ClaimKind.Revocation) continue;

            if (last.Payload.Identity.Kind == ClaimKind.Release) removedReleases.Add(last.Payload.Identity);
            else removedGames.Add(last.Payload.Identity);
        }

        return new PublishPreview(removedReleases, removedGames, narrowed, blocked, unchanged, added, updated);
    }

    private static Dictionary<string, List<SignedClaim>> GroupByIdentity(
        IReadOnlyList<SignedClaim> claims) =>
        claims
            .GroupBy(c => c.Payload.Identity.ToStorageKey(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    /// <summary>
    /// The claim that was live for this identity last time, if there was one. A revocation in the
    /// history does not count as a live claim — it is what withdrew one.
    /// </summary>
    private static SignedClaim? LiveClaim(List<SignedClaim>? history) =>
        history?
            .Where(c => c.Payload.Kind != ClaimKind.Revocation)
            .OrderByDescending(c => c.Payload.Seq)
            .FirstOrDefault();

    private static SignedClaim? Newest(List<SignedClaim>? history) =>
        history?.OrderByDescending(c => c.Payload.Seq).FirstOrDefault();

    public static BuildResult Build(
        PluginRepoIndex index,
        IReadOnlyList<SignedClaim> previousClaims,
        ClaimSigner signer)
    {
        var previousByIdentity = GroupByIdentity(previousClaims);

        var result = new List<SignedClaim>();
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        int unchanged = 0, added = 0, updated = 0, revoked = 0;

        void Emit(ClaimKind kind, ClaimIdentity identity, ClaimAudience audience, string bodyJson)
        {
            var key = identity.ToStorageKey();
            seenIdentities.Add(key);
            previousByIdentity.TryGetValue(key, out var history);

            var previous = LiveClaim(history);
            var nextSeq = NextSequence(history);

            // A withdrawn RELEASE identity is never re-asserted.
            //
            // After a deletion the proof keeps the revocation but not the withdrawn body or its
            // package hash, and a server holding the only copy of the published ZIP can delete that
            // too — so "this version is already published with these bytes" finds nothing to compare
            // against, and the same version could be re-signed over different bytes with a perfectly
            // valid higher sequence. That is the one invariant a release has.
            //
            // Games have no such invariant, so re-adding a removed game is allowed. And this can
            // only ever be a producer-side rule: within one publish, narrowing legitimately emits a
            // revocation followed by a higher-sequence live claim for the same identity, which is
            // indistinguishable to a reader from a resurrection spread across two publishes. The
            // builder can tell the difference because it holds the previous set.
            if (Newest(history)?.Payload.Kind == ClaimKind.Revocation && identity.Kind == ClaimKind.Release)
            {
                throw new ClaimFormatException(
                    $"Version {identity.Version} ({identity.Channel}) of {identity.GameId} was " +
                    "withdrawn from the published catalog, and a withdrawn version can't be " +
                    "published again — the bytes it stood for are gone. Publish it under a new " +
                    "version number instead.");
            }

            // Outstanding revocations ride along on every publish, forever.
            //
            // They used to be dropped the moment anything else was republished, which broke the
            // narrowing case during ordinary honest publishing: a patron who was offline for the one
            // publish that carried their revocation would never see it again, and would go on
            // trusting a release they are no longer entitled to. A revocation is only useful once its
            // audience has actually received it, and we cannot know when that has happened.
            foreach (var carried in history?.Where(c => c.Payload.Kind == ClaimKind.Revocation) ?? [])
                result.Add(carried);

            if (previous is not null &&
                string.Equals(previous.Payload.BodyJson, bodyJson, StringComparison.Ordinal) &&
                previous.Payload.Audience.SameAs(audience))
            {
                result.Add(previous);
                unchanged++;
                return;
            }

            // Narrowing: someone who could see this before no longer can. They are holding a claim
            // they must stop trusting, and they cannot be told by the replacement — they will never
            // see it. So a revocation goes to the audience they were in, at a LOWER sequence than
            // the replacement, and "newest visible wins" does the rest.
            if (previous is not null && IsNarrowing(previous.Payload.Audience, audience))
            {
                result.Add(signer.Sign(ClaimKind.Revocation, identity, nextSeq,
                    previous.Payload.Audience, RevocationBody));
                nextSeq++;
                revoked++;
            }

            result.Add(signer.Sign(kind, identity, nextSeq, audience, bodyJson));
            if (previous is null) added++; else updated++;
        }

        // ---- everything this index asserts ----
        // The same list Preview reads. Signing what was previewed, rather than re-deriving it, is
        // what makes "this publish will withdraw these versions" a promise instead of a guess.
        foreach (var item in Plan(index))
            Emit(item.Kind, item.Identity, item.Audience, item.BodyJson);

        // ---- things that are gone ----
        foreach (var (key, history) in previousByIdentity)
        {
            if (seenIdentities.Contains(key)) continue;

            // Every revocation this identity has ever accumulated rides along, exactly as it does
            // for an identity that is still present. Keeping only the newest one loses the earlier
            // audiences: publish a release publicly, narrow it to tier 3, then delete it, and the
            // public revocation from the narrowing disappears — so a public manager that was offline
            // for that one publish never sees a revocation it can read, and keeps trusting a release
            // it lost access to two publishes ago. No attacker involved; ordinary publishing does it.
            foreach (var carried in history.Where(c => c.Payload.Kind == ClaimKind.Revocation))
                result.Add(carried);

            var last = Newest(history)!;
            if (last.Payload.Kind == ClaimKind.Revocation) continue;

            // Revoke at the audience that could see it, so nobody else learns it ever existed.
            result.Add(signer.Sign(ClaimKind.Revocation, last.Payload.Identity, NextSequence(history),
                last.Payload.Audience, RevocationBody));
            revoked++;
        }

        return new BuildResult(result, unchanged, added, updated, revoked);
    }

    /// <summary>
    /// One past every sequence ever used for this object, revocations included. Reusing a sequence
    /// is equivocation, and the set rules reject it.
    /// </summary>
    private static long NextSequence(List<SignedClaim>? history) =>
        history is null or [] ? 1 : history.Max(c => c.Payload.Seq) + 1;

    /// <summary>
    /// True when the new audience does not admit everyone the old one did — the case where somebody
    /// is losing access and has to be told.
    ///
    /// Widening needs no revocation: nobody loses anything, and the newer claim simply reaches more
    /// people.
    /// </summary>
    private static bool IsNarrowing(ClaimAudience previous, ClaimAudience next)
    {
        if (previous.Public) return !next.Public;
        if (next.Public) return false; // strictly wider

        // Different campaign entirely: everyone in the old one loses access.
        if (!string.Equals(previous.CampaignId, next.CampaignId, StringComparison.Ordinal)) return true;

        return previous.TierIds.Any(t => !next.TierIds.Contains(t, StringComparer.Ordinal));
    }

    private static ClaimAudience AudienceFor(ModRelease release)
    {
        if (release.Patreon is null) return ClaimAudience.Everyone;

        return new ClaimAudience
        {
            Public = false,
            CampaignId = release.Patreon.CampaignId,
            TierIds = release.Patreon.TierIds.ToList()
        };
    }

    /// <summary>
    /// A revocation says nothing at all. Its content is deliberately absent: a revocation is
    /// disclosed to an audience that is losing access, and restating what they are losing would
    /// hand them the very metadata the withdrawal is taking away.
    ///
    /// An earlier version carried the sequence it superseded. That went, because no verifier can
    /// check it — once the withdrawn assertion has left the current proof there is nothing to
    /// compare against — and resolution is by identity and highest visible sequence, which never
    /// consults it. A field that cannot be checked and is not used is a promise nobody keeps.
    /// </summary>
    private const string RevocationBody = "{}";

    private static readonly JsonSerializerOptions BodyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Serializes a body the same way every time, so an object that has not actually changed
    /// produces identical bytes and keeps its existing claim.
    /// </summary>
    private static string Canonicalise<T>(T value) => JsonSerializer.Serialize(value, BodyOptions);

    /// <summary>
    /// The published projection of a game: everything the model carries, minus the author-only
    /// members. Built by stripping rather than by copying into a hand-written subset, so a field
    /// added to <see cref="GameDefinition"/> tomorrow is published rather than silently dropped
    /// from every catalog until someone notices.
    /// </summary>
    private static string PublishedGameBody(GameDefinition game)
    {
        var node = JsonSerializer.SerializeToNode(game, BodyOptions)?.AsObject()
            ?? throw new InvalidOperationException($"Game '{game.GameId}' could not be serialized.");

        AuthoringOnlyFields.StripFromGame(node);
        return node.ToJsonString(BodyOptions);
    }
}
