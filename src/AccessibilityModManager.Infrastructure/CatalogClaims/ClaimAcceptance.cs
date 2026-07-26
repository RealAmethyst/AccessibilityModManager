using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// What a verified claim set still has to prove before anyone acts on it.
///
/// A signature says the author produced these bytes. It says nothing about whether the bytes make
/// sense. Without the checks here, all of these verify: a release whose envelope says version 1.0
/// and whose body says 2.0; a claim addressed to tier 3 whose body gates on tier 1; a header naming
/// somebody else's plugin; an http:// package URL; a SHA-256 that is not a SHA-256 at all. Each one
/// is the author's key vouching for something the author's own rules forbid, and the failure mode
/// is worse than an unsigned mistake, because everything downstream has been told to trust it.
///
/// Two halves, in order:
///
/// 1. **Body agreement** — the signed body is parsed into its real model and cross-checked against
///    the envelope carrying it. This is what makes the projection below well defined; without it a
///    body claiming 2.0 would simply be filed under an identity saying 1.0 and nobody would notice.
/// 2. **Manager-grade validation** — the claims are projected back into an index and run through
///    <see cref="PluginIndexValidation"/>: the same implementation, and therefore necessarily the
///    same rules, that every user's manager applies to what it downloads. Writing a second
///    validator here would be writing a second set of rules that agree only until they don't.
///
/// One deliberate difference from the manager's handling of an UNSIGNED index: there, a release
/// that cannot be obtained is dropped and the rest is used, because an authoring mistake in public
/// data should not take a whole catalog down. Here the whole proof is refused. A signed release
/// that fails is the key asserting something impossible, and continuing past it is how a
/// half-trusted catalog gets built.
/// </summary>
public static class ClaimAcceptance
{
    private static readonly JsonSerializerOptions BodyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions IndexOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static void Accept(IReadOnlyList<SignedClaim> claims, ClaimTrustAnchor anchor)
    {
        foreach (var claim in claims) CheckBody(claim, anchor);

        var report = PluginIndexValidation.Validate(anchor.PluginId, Project(claims, anchor));

        if (report.TrustErrors.Count > 0)
            throw new ClaimFormatException(
                "a signed claim carries content no manager would accept: " + report.TrustErrors[0]);

        if (report.UnobtainableReleases.Count > 0)
            throw new ClaimFormatException(
                "a signed release could never be installed: " + report.UnobtainableReleases[0]);
    }

    private static void CheckBody(SignedClaim claim, ClaimTrustAnchor anchor)
    {
        var identity = claim.Payload.Identity;

        switch (claim.Payload.Kind)
        {
            case ClaimKind.Revocation:
                // Nothing at all, and nothing means nothing: a revocation goes to an audience that
                // is losing access, so any content in it hands back the metadata being withdrawn.
                using (var body = JsonDocument.Parse(claim.Payload.BodyJson))
                {
                    if (body.RootElement.EnumerateObject().Any())
                        throw new ClaimFormatException("a revocation body must be empty");
                }
                return;

            case ClaimKind.Header:
                var header = Parse<HeaderBody>(claim, "header");
                if (!string.Equals(header.PluginId, anchor.PluginId, StringComparison.Ordinal))
                    throw new ClaimFormatException(
                        $"the header claim names plugin '{header.PluginId}' but the registry names " +
                        $"'{anchor.PluginId}'");
                if (string.IsNullOrWhiteSpace(header.RepoVersion))
                    throw new ClaimFormatException("the header claim carries no repoVersion");
                return;

            case ClaimKind.Game:
                var game = Parse<GameDefinition>(claim, "game");
                if (!string.Equals(game.GameId, identity.GameId, StringComparison.Ordinal))
                    throw new ClaimFormatException(
                        $"a game claim for '{identity.GameId}' carries a body for '{game.GameId}'");
                if (!claim.Payload.Audience.Public)
                    throw new ClaimFormatException("game claims are public");
                return;

            case ClaimKind.Release:
                var release = Parse<ModRelease>(claim, "release");
                if (!string.Equals(release.Version, identity.Version, StringComparison.Ordinal) ||
                    !string.Equals(release.Channel, identity.Channel, StringComparison.Ordinal) ||
                    !string.Equals(release.GameId, identity.GameId, StringComparison.Ordinal))
                {
                    throw new ClaimFormatException(
                        $"a release claim for {identity.GameId}/{identity.Channel}/{identity.Version} carries a " +
                        $"body for {release.GameId}/{release.Channel}/{release.Version}");
                }

                if (!string.Equals(release.PluginId, anchor.PluginId, StringComparison.Ordinal))
                    throw new ClaimFormatException(
                        $"a release claim names plugin '{release.PluginId}' but the registry names " +
                        $"'{anchor.PluginId}'");

                CheckAudienceMatchesGate(claim.Payload.Audience, release, identity);
                return;
        }
    }

    /// <summary>
    /// The audience and the Patreon gate are two statements of one fact, and a claim that lets them
    /// disagree is worse than either alone: a caller who is shown a release because its audience
    /// admits them still cannot install it if the gate says otherwise, and a release disclosed to a
    /// tier its gate does not cover is a leak with a valid signature on it.
    /// </summary>
    private static void CheckAudienceMatchesGate(ClaimAudience audience, ModRelease release, ClaimIdentity identity)
    {
        var where = $"{identity.GameId}/{identity.Channel}/{identity.Version}";

        if (audience.Public)
        {
            if (release.Patreon is not null)
                throw new ClaimFormatException($"release {where} is signed as public but carries a Patreon gate");
            return;
        }

        if (release.Patreon is null)
            throw new ClaimFormatException($"release {where} is signed to a tier but carries no Patreon gate");

        if (!string.Equals(release.Patreon.CampaignId, audience.CampaignId, StringComparison.Ordinal))
            throw new ClaimFormatException(
                $"release {where} is signed to campaign '{audience.CampaignId}' but gated on " +
                $"'{release.Patreon.CampaignId}'");

        if (!new HashSet<string>(audience.TierIds, StringComparer.Ordinal).SetEquals(release.Patreon.TierIds))
            throw new ClaimFormatException($"release {where} is signed to different tiers than it is gated on");
    }

    /// <summary>
    /// The claims as an index — the projection the compatibility view is built from, and the thing
    /// manager-grade validation runs against.
    ///
    /// Releases are filed under the SIGNED identity's game, never under wherever an unsigned
    /// plaintext happened to put them. Games and releases are sorted explicitly rather than in
    /// arrival order: claim order carries no meaning and the digest is over a sorted set, so a
    /// server reordering the array must not be able to change a single byte of what comes out.
    ///
    /// <c>generatedAt</c> is not signed and not projected — a timestamp in the signed set would
    /// either re-sign everything on every publish or announce that something hidden had changed —
    /// so a fixed value stands in for a field the model requires and nobody reads.
    /// </summary>
    private static string Project(IReadOnlyList<SignedClaim> claims, ClaimTrustAnchor anchor)
    {
        var live = ClaimVerifier.ResolveAll(claims);

        var headerClaim = live.SingleOrDefault(c => c.Payload.Kind == ClaimKind.Header)
            ?? throw new ClaimFormatException("the claim set has no live header");
        var header = Parse<HeaderBody>(headerClaim, "header");

        var games = live
            .Where(c => c.Payload.Kind == ClaimKind.Game)
            .OrderBy(c => c.Payload.Identity.GameId, StringComparer.Ordinal)
            .Select(c => Parse<GameDefinition>(c, "game"))
            .ToList();

        var known = new HashSet<string>(games.Select(g => g.GameId), StringComparer.Ordinal);
        var releases = new Dictionary<string, List<ModRelease>>(StringComparer.Ordinal);

        foreach (var claim in live
                     .Where(c => c.Payload.Kind == ClaimKind.Release)
                     .OrderBy(c => c.Payload.Identity.Version, StringComparer.Ordinal)
                     .ThenBy(c => c.Payload.Identity.Channel, StringComparer.Ordinal))
        {
            var gameId = claim.Payload.Identity.GameId!;

            // A release under a game nobody asserted is a hole in the catalog, not a release: the
            // manager has nothing to install it into, and the projection would have to invent a
            // game to hold it.
            if (!known.Contains(gameId))
                throw new ClaimFormatException($"a release claims game '{gameId}', which no game claim describes");

            if (!releases.TryGetValue(gameId, out var list)) releases[gameId] = list = [];
            list.Add(Parse<ModRelease>(claim, "release"));
        }

        return JsonSerializer.Serialize(new PluginRepoIndex
        {
            PluginId = header.PluginId,
            RepoVersion = header.RepoVersion,
            GeneratedAt = DateTime.UnixEpoch,
            Games = games,
            ReleasesByGameId = releases,
            Author = header.Author
        }, IndexOptions);
    }

    private static T Parse<T>(SignedClaim claim, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(claim.Payload.BodyJson, BodyOptions)
                ?? throw new ClaimFormatException($"a {what} claim has an empty body");
        }
        catch (JsonException ex)
        {
            throw new ClaimFormatException($"a {what} claim's body is not a {what}: {ex.Message}", ex);
        }
    }

    private sealed record HeaderBody(string PluginId, string RepoVersion, PluginAuthorInfo? Author);
}
