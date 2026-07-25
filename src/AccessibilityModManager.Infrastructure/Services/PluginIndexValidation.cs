using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// What one pass over an index found: trust violations (the manager refuses the whole index),
/// and releases that are merely unobtainable (the manager drops them one by one; an author must
/// fix them). <see cref="Index"/> is the CLEANED index — unobtainable releases already removed.
/// </summary>
public sealed record IndexValidationReport(
    PluginRepoIndex Index,
    IReadOnlyList<string> TrustErrors,
    IReadOnlyList<string> UnobtainableReleases);

/// <summary>
/// THE index validation — one implementation shared by the manager's fetch path and the
/// AuthorTool's checks, so what the author's green tick approves is exactly what every user's
/// manager will accept (audit finding 38: the old author-side check validated almost nothing).
/// Two severities by design: identity spoofing and non-https URLs are TRUST errors (nothing in
/// the index can be believed); an unobtainable release (a Patreon gate with no server and no
/// numeric post, or no package source at all) is an authoring mistake that costs that release
/// only.
/// </summary>
public static class PluginIndexValidation
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Deserializes and validates <paramref name="json"/> as the index for the plugin the signed
    /// registry names <paramref name="pluginId"/>. Throws only on JSON that doesn't parse at all
    /// (<see cref="JsonException"/>) or deserializes to null (<see cref="InvalidOperationException"/>);
    /// every other problem lands in the report.
    /// </summary>
    public static IndexValidationReport Validate(string pluginId, string json)
    {
        var index = JsonSerializer.Deserialize<PluginRepoIndex>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Repo index for plugin '{pluginId}' deserialized to null");

        var trustErrors = new List<string>();
        var unobtainable = new List<string>();

        // Identity binding: the unsigned index must declare exactly the identity the SIGNED
        // registry entry promised — including case. Case-insensitive acceptance would let two
        // spellings of one id flow into receipts and refcounts, whose comparisons are exact.
        if (!string.Equals(index.PluginId, pluginId, StringComparison.Ordinal))
        {
            trustErrors.Add(
                $"Plugin index identity mismatch: the registry entry '{pluginId}' served an index claiming " +
                $"to be '{index.PluginId}' (ids must match exactly, including case). Refusing it.");
        }

        // Ids become folder names — they must be safe single segments (no separators, no '..').
        foreach (var game in index.Games)
        {
            CollectIdError(trustErrors, game.GameId, $"plugin '{pluginId}' game id");
            foreach (var dep in game.Dependencies)
                CollectIdError(trustErrors, dep.Id, $"plugin '{pluginId}' dependency id");
        }

        foreach (var (gameId, releases) in index.ReleasesByGameId)
        {
            CollectIdError(trustErrors, gameId, $"plugin '{pluginId}' release game id");
            var dropped = new List<ModRelease>();
            foreach (var release in releases)
            {
                if (!string.Equals(release.PluginId, pluginId, StringComparison.Ordinal))
                {
                    trustErrors.Add(
                        $"Release {gameId}/{release.Version} in plugin '{pluginId}' claims plugin id " +
                        $"'{release.PluginId}' (ids must match exactly, including case). Refusing the index.");
                }
                if (!string.Equals(release.GameId, gameId, StringComparison.Ordinal))
                {
                    trustErrors.Add(
                        $"Release {release.Version} filed under game '{gameId}' in plugin '{pluginId}' claims " +
                        $"game id '{release.GameId}' (ids must match exactly, including case). Refusing the index.");
                }

                // A Patreon gate is validated whenever it is PRESENT — the install flow treats
                // any non-null gate as gated, even alongside a packageUrl, so a gate must be
                // usable: an https author server, or a numeric post id for the manual browser
                // path. A gate with neither would send users to a picker with nothing to pick.
                if (release.Patreon is not null)
                {
                    var hasServer = !string.IsNullOrWhiteSpace(release.Patreon.ServerUrl);
                    if (hasServer)
                        CollectHttpsError(trustErrors, release.Patreon.ServerUrl!,
                            $"plugin '{pluginId}' game '{gameId}' Patreon server URL");
                    var hasPost = !string.IsNullOrWhiteSpace(release.Patreon.PostId) &&
                                  release.Patreon.PostId!.All(char.IsAsciiDigit);
                    if (!hasServer && !hasPost)
                    {
                        unobtainable.Add(
                            $"Release {pluginId}/{gameId}/{release.Version} is Patreon-gated but has neither a " +
                            "server URL nor a numeric post id — no user could obtain the file.");
                        dropped.Add(release);
                        continue;
                    }
                }

                // The SHA256 is a hard gate at install time: the manager computes a 64-character
                // lowercase hex digest and compares it exactly. A release whose recorded hash
                // isn't even that SHAPE can never install — the user downloads the whole package
                // only to be turned away — so it's unobtainable in the same sense as a missing
                // URL, and it goes the same way: dropped per release here, fatal for an author.
                if (!IsSha256Shaped(release.Sha256))
                {
                    unobtainable.Add(
                        $"Release {pluginId}/{gameId}/{release.Version} has sha256 '{release.Sha256}', which " +
                        "isn't a 64-character hex fingerprint — no download could ever match it.");
                    dropped.Add(release);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(release.Version) || string.IsNullOrWhiteSpace(release.Channel))
                {
                    unobtainable.Add(
                        $"A release under {pluginId}/{gameId} has no version or no channel — the manager " +
                        "can't offer or record it.");
                    dropped.Add(release);
                    continue;
                }

                if (release.PackageUrl is null)
                {
                    if (release.Patreon is null)
                    {
                        unobtainable.Add(
                            $"Release {pluginId}/{gameId}/{release.Version} has neither a public packageUrl " +
                            "nor a Patreon gate — no user could obtain the file.");
                        dropped.Add(release);
                    }
                    continue;
                }
                CollectHttpsError(trustErrors, release.PackageUrl,
                    $"plugin '{pluginId}' game '{gameId}' package URL");
            }

            foreach (var release in dropped)
                releases.Remove(release);
        }

        return new IndexValidationReport(index, trustErrors, unobtainable);
    }

    /// <summary>
    /// Exactly what <c>Convert.ToHexStringLower(SHA256…)</c> produces, which is what every hash
    /// comparison in the engine is against. Case is tolerated here — the comparisons are
    /// case-insensitive — but length and alphabet are not.
    /// </summary>
    private static bool IsSha256Shaped(string? sha256) =>
        sha256 is { Length: 64 } && sha256.All(char.IsAsciiHexDigit);

    private static void CollectIdError(List<string> errors, string? id, string description)
    {
        try { PathSafety.EnsureSafeId(id, description); }
        catch (InvalidOperationException ex) { errors.Add(ex.Message); }
    }

    private static void CollectHttpsError(List<string> errors, string url, string description)
    {
        try { UrlValidator.RequireHttps(url, description); }
        catch (Exception ex) { errors.Add(ex.Message); }
    }

    private static void CollectHttpsError(List<string> errors, Uri url, string description)
    {
        try { UrlValidator.RequireHttps(url, description); }
        catch (Exception ex) { errors.Add(ex.Message); }
    }
}
