using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>One public download the live catalog promises, and what it promises about it.</summary>
public sealed record PublicDownload(string GameId, string Version, Uri Url, string Sha256)
{
    public string Describe() => $"{GameId} {Version}";
}

/// <summary>
/// Which of a catalog's downloads this machine can meaningfully prove, and how to tell the author
/// what was found.
///
/// <para>Pure by design: the ordering around it is the dangerous part (see
/// <c>IndexEditorViewModel</c>), so the choosing is kept somewhere tests can reach without a
/// window.</para>
/// </summary>
public static class PublicDownloadVerification
{
    /// <summary>
    /// The releases in <paramref name="index"/> whose bytes the author's own download server is
    /// expected to hand to anyone.
    ///
    /// <para>Three conditions, all necessary. A release with a Patreon gate is not anonymously
    /// downloadable and a check would rightly be turned away. A release hosted anywhere else —
    /// GitHub, most commonly — is not this server's promise to keep. And a release with no package
    /// address has nothing to check.</para>
    ///
    /// <para>Host alone is not enough to decide the second: one host can serve the catalog, the
    /// site and the downloads from different roots, so the configured base PATH has to match too,
    /// and at a segment boundary — otherwise a base of <c>/releases</c> would claim
    /// <c>/releases-archive/…</c>, which this server does not serve and never uploaded to.</para>
    /// </summary>
    public static IReadOnlyList<PublicDownload> ServerHostedPublicDownloads(
        PluginRepoIndex index, string? publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl) ||
            !Uri.TryCreate(publicBaseUrl.TrimEnd('/'), UriKind.Absolute, out var configuredBase))
        {
            return [];
        }

        var found = new List<PublicDownload>();

        foreach (var (gameId, releases) in index.ReleasesByGameId)
        {
            foreach (var release in releases)
            {
                if (release.Patreon != null) continue;
                if (release.PackageUrl is not { } url) continue;
                if (!IsUnder(configuredBase, url)) continue;

                found.Add(new PublicDownload(gameId, release.Version, url, release.Sha256));
            }
        }

        return found;
    }

    /// <summary>
    /// Whether <paramref name="url"/> is an address this server configuration would have produced:
    /// same origin, and a path inside the configured base rather than merely starting with its
    /// spelling.
    /// </summary>
    internal static bool IsUnder(Uri configuredBase, Uri url)
    {
        if (!string.Equals(configuredBase.Scheme, url.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(configuredBase.Host, url.Host, StringComparison.OrdinalIgnoreCase)) return false;
        if (configuredBase.Port != url.Port) return false;

        var basePath = configuredBase.AbsolutePath.TrimEnd('/');
        var path = url.AbsolutePath;

        // A bare base ("https://host") owns every path under that origin.
        if (basePath.Length == 0) return true;

        return path.StartsWith(basePath, StringComparison.Ordinal) &&
               path.Length > basePath.Length &&
               path[basePath.Length] == '/';
    }
}
