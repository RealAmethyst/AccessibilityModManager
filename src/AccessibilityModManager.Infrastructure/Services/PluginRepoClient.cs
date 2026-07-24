using System.Security.Cryptography;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed class PluginRepoClient : IPluginRepoClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public PluginRepoClient(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PluginRepoIndex> FetchPluginIndexAsync(PluginEntry plugin, CancellationToken ct = default)
    {
        UrlValidator.RequireHttps(plugin.RepoIndexUrl, $"plugin '{plugin.Id}' repo index");

        _logger.Information("Fetching repo index for plugin {PluginId} from {Url}", plugin.Id, plugin.RepoIndexUrl);

        // Cache-Control: no-cache + unique query param. GitHub's raw-URL CDN (Fastly) ignores
        // header-only revalidation hints for under-a-minute-old objects, so we make each
        // request a unique URL too — that forces the CDN to fetch origin and the
        // freshly-pushed index.json shows up immediately.
        var bustedUrl = PluginRegistryClient.AppendCacheBuster(plugin.RepoIndexUrl);
        var request = new HttpRequestMessage(HttpMethod.Get, bustedUrl);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var index = JsonSerializer.Deserialize<PluginRepoIndex>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Repo index for plugin '{plugin.Id}' deserialized to null");

        // Identity binding: the unsigned index must declare exactly the identity the SIGNED
        // registry entry promised — including case. Case-insensitive acceptance would let two
        // spellings of one id flow into receipts and refcounts, whose comparisons are exact;
        // requiring the registry's spelling keeps a single canonical id everywhere.
        if (!string.Equals(index.PluginId, plugin.Id, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Plugin index identity mismatch: the registry entry '{plugin.Id}' served an index claiming " +
                $"to be '{index.PluginId}' (ids must match exactly, including case). Refusing it.");

        // Ids become folder names — they must be safe single segments (no separators, no '..').
        foreach (var game in index.Games)
        {
            PathSafety.EnsureSafeId(game.GameId, $"plugin '{plugin.Id}' game id");
            foreach (var dep in game.Dependencies)
                PathSafety.EnsureSafeId(dep.Id, $"plugin '{plugin.Id}' dependency id");
        }

        // Validate releases: HTTPS package URLs (Patreon-gated releases have PackageUrl == null
        // and are fetched via the author's server or a manual pick), and identities that match
        // both the signed plugin id and the game they're filed under.
        foreach (var (gameId, releases) in index.ReleasesByGameId)
        {
            PathSafety.EnsureSafeId(gameId, $"plugin '{plugin.Id}' release game id");
            foreach (var release in releases)
            {
                if (!string.Equals(release.PluginId, plugin.Id, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Release {gameId}/{release.Version} in plugin '{plugin.Id}' claims plugin id " +
                        $"'{release.PluginId}' (ids must match exactly, including case). Refusing the index.");
                if (!string.Equals(release.GameId, gameId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Release {release.Version} filed under game '{gameId}' in plugin '{plugin.Id}' claims " +
                        $"game id '{release.GameId}' (ids must match exactly, including case). Refusing the index.");

                // A Patreon gate is validated whenever it is PRESENT — the install flow treats
                // any non-null gate as gated, even alongside a packageUrl, so a gate must always
                // be usable: an https author server, or a numeric post id for the manual browser
                // path. A gate with neither would send users to a picker with nothing to pick.
                if (release.Patreon is not null)
                {
                    var hasServer = !string.IsNullOrWhiteSpace(release.Patreon.ServerUrl);
                    if (hasServer)
                        UrlValidator.RequireHttps(release.Patreon.ServerUrl!, $"plugin '{plugin.Id}' game '{gameId}' Patreon server URL");
                    var hasPost = !string.IsNullOrWhiteSpace(release.Patreon.PostId) &&
                                  release.Patreon.PostId!.All(char.IsAsciiDigit);
                    if (!hasServer && !hasPost)
                        throw new InvalidOperationException(
                            $"Patreon-gated release {plugin.Id}/{gameId}/{release.Version} has neither a server URL " +
                            "nor a valid (numeric) post id — there would be no way to obtain the file.");
                }

                if (release.PackageUrl is null)
                {
                    if (release.Patreon is null)
                        throw new InvalidOperationException(
                            $"Release {plugin.Id}/{gameId}/{release.Version} has neither a public packageUrl nor a Patreon gate.");
                    continue;
                }
                UrlValidator.RequireHttps(release.PackageUrl, $"plugin '{plugin.Id}' game '{gameId}' package URL");
            }
        }

        _logger.Information("Fetched index for {PluginId}: {GameCount} games, {ReleaseCount} total releases",
            plugin.Id, index.Games.Count,
            index.ReleasesByGameId.Values.Sum(r => r.Count));

        return index;
    }

    public async Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        UrlValidator.RequireHttps(packageUrl, "package download URL");

        _logger.Information("Downloading package from {Url} to {Dest}", packageUrl, destFile);

        using var response = await _httpClient.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        var downloadedBytes = 0L;

        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            downloadedBytes += bytesRead;

            if (progress != null && totalBytes.HasValue && totalBytes.Value > 0)
            {
                var pct = (double)downloadedBytes / totalBytes.Value * 100;
                progress.Report(new ProgressInfo
                {
                    Percentage = pct,
                    StatusText = $"Downloading... {downloadedBytes / 1024:N0} / {totalBytes.Value / 1024:N0} KB",
                    StepDescription = "Downloading package"
                });
            }
        }

        _logger.Information("Download complete: {Bytes} bytes", downloadedBytes);
        return destFile;
    }

    public async Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default)
    {
        _logger.Information("Verifying SHA256 for {File}", filePath);

        using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, ct);
        var actualHash = Convert.ToHexStringLower(hashBytes);

        var match = string.Equals(actualHash, expectedHash.ToLowerInvariant(), StringComparison.Ordinal);

        if (match)
        {
            _logger.Information("SHA256 verified: {Hash}", actualHash);
        }
        else
        {
            _logger.Error("SHA256 mismatch! Expected: {Expected}, Got: {Actual}", expectedHash, actualHash);
        }

        return match;
    }
}
