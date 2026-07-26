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
            var newest = history?.OrderByDescending(c => c.Payload.Seq).FirstOrDefault();
            if (newest?.Payload.Kind == ClaimKind.Revocation && identity.Kind == ClaimKind.Release)
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

        // ---- header ----
        // repoVersion is in here because the manager's model requires it to read an index at all,
        // and the published catalog is projected from these claims — an unsigned copy of a required
        // field would be one a server could set freely. generatedAt deliberately is NOT: a
        // timestamp inside the signed set would either re-sign the header on every publish or
        // announce that something hidden had changed.
        Emit(ClaimKind.Header,
            new ClaimIdentity { Kind = ClaimKind.Header },
            ClaimAudience.Everyone,
            Canonicalise(new
            {
                pluginId = index.PluginId,
                repoVersion = index.RepoVersion,
                author = index.Author
            }));

        // ---- games and their releases ----
        foreach (var game in index.Games.OrderBy(g => g.GameId, StringComparer.Ordinal))
        {
            Emit(ClaimKind.Game,
                new ClaimIdentity { Kind = ClaimKind.Game, GameId = game.GameId },
                // Game metadata is public; whether it is DISCLOSED depends on whether any release
                // under it is visible, which is a serving decision, not a signing one. A game whose
                // only builds are patron-only must not appear for someone who cannot see them.
                ClaimAudience.Everyone,
                PublishedGameBody(game));

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
    /// Author-only members of a game, which never reach a published claim.
    ///
    /// The three <c>default*</c> scripts are templates the AuthorTool pre-fills a release form
    /// from; the manager only ever reads a release's own manifest. A dependency's
    /// <c>versionDiscovery</c> is documented as having no runtime effect at all. Signing them would
    /// publish authoring state to every user and re-sign the game claim whenever the author
    /// adjusted a template — churn that, on a public claim, is exactly the signal this design keeps
    /// out of the anonymous view.
    /// </summary>
    private static readonly string[] AuthorOnlyGameMembers =
        ["defaultPreInstall", "defaultPostInstall", "defaultPostUninstall"];

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

        foreach (var member in AuthorOnlyGameMembers) node.Remove(member);

        if (node["dependencies"] is JsonArray dependencies)
        {
            foreach (var dependency in dependencies.OfType<JsonObject>())
                dependency.Remove("versionDiscovery");
        }

        return node.ToJsonString(BodyOptions);
    }
}
