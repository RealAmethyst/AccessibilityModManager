using System.Text.Json;
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

    public static BuildResult Build(
        PluginRepoIndex index,
        IReadOnlyList<SignedClaim> previousClaims,
        ClaimSigner signer)
    {
        var previousByIdentity = previousClaims
            .GroupBy(c => c.Payload.Identity.ToStorageKey(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var result = new List<SignedClaim>();
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        int unchanged = 0, added = 0, updated = 0, revoked = 0;

        void Emit(ClaimKind kind, ClaimIdentity identity, ClaimAudience audience, string bodyJson)
        {
            var key = identity.ToStorageKey();
            seenIdentities.Add(key);
            previousByIdentity.TryGetValue(key, out var history);

            // The live claim from last time, if there was one. A revocation in the history does not
            // count as a live claim — it is what withdrew one.
            var previous = history?
                .Where(c => c.Payload.Kind != ClaimKind.Revocation)
                .OrderByDescending(c => c.Payload.Seq)
                .FirstOrDefault();

            var nextSeq = NextSequence(history);

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
                    previous.Payload.Audience, RevocationBody(previous.Payload.Seq)));
                nextSeq++;
                revoked++;
            }

            result.Add(signer.Sign(kind, identity, nextSeq, audience, bodyJson));
            if (previous is null) added++; else updated++;
        }

        // ---- header ----
        Emit(ClaimKind.Header,
            new ClaimIdentity { Kind = ClaimKind.Header },
            ClaimAudience.Everyone,
            Canonicalise(new { pluginId = index.PluginId, author = index.Author }));

        // ---- games and their releases ----
        foreach (var game in index.Games.OrderBy(g => g.GameId, StringComparer.Ordinal))
        {
            Emit(ClaimKind.Game,
                new ClaimIdentity { Kind = ClaimKind.Game, GameId = game.GameId },
                // Game metadata is public; whether it is DISCLOSED depends on whether any release
                // under it is visible, which is a serving decision, not a signing one. A game whose
                // only builds are patron-only must not appear for someone who cannot see them.
                ClaimAudience.Everyone,
                Canonicalise(game));

            if (!index.ReleasesByGameId.TryGetValue(game.GameId, out var releases)) continue;

            foreach (var release in releases.OrderBy(r => r.Version, StringComparer.Ordinal)
                         .ThenBy(r => r.Channel, StringComparer.Ordinal))
            {
                Emit(ClaimKind.Release,
                    new ClaimIdentity
                    {
                        Kind = ClaimKind.Release,
                        GameId = game.GameId,
                        Channel = release.Channel,
                        Version = release.Version
                    },
                    AudienceFor(release),
                    Canonicalise(release));
            }
        }

        // ---- things that are gone ----
        foreach (var (key, history) in previousByIdentity)
        {
            if (seenIdentities.Contains(key)) continue;

            var last = history.OrderByDescending(c => c.Payload.Seq).First();
            if (last.Payload.Kind == ClaimKind.Revocation)
            {
                // Already withdrawn; carry the revocation forward so a manager that has not seen it
                // yet still learns the object is gone.
                result.Add(last);
                continue;
            }

            // Revoke at the audience that could see it, so nobody else learns it ever existed.
            result.Add(signer.Sign(ClaimKind.Revocation, last.Payload.Identity, NextSequence(history),
                last.Payload.Audience, RevocationBody(last.Payload.Seq)));
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
    /// A revocation says nothing about what it withdrew beyond which sequence it supersedes. The
    /// object's content is deliberately absent: a revocation is disclosed to an audience that is
    /// losing access, and restating what they are losing would hand them the very metadata the
    /// withdrawal is taking away.
    /// </summary>
    private static string RevocationBody(long supersedes) =>
        JsonSerializer.Serialize(new { supersedes });

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
}
