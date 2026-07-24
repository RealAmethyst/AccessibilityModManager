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
    private readonly string _cacheDirectory;

    public PluginRepoClient(HttpClient httpClient, ILogger logger, string? cacheDirectoryOverride = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _cacheDirectory = cacheDirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibilityModManager", "cache", "indexes");
    }

    public async Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(PluginEntry plugin, CancellationToken ct = default)
    {
        UrlValidator.RequireHttps(plugin.RepoIndexUrl, $"plugin '{plugin.Id}' repo index");

        _logger.Information("Fetching repo index for plugin {PluginId} from {Url}", plugin.Id, plugin.RepoIndexUrl);

        string json;
        try
        {
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
            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (PluginRegistryClient.IsNetworkFailure(ex, ct))
        {
            // Offline: fall back to the last index this machine accepted for THIS plugin. The
            // cached copy is re-validated in full against the CURRENT (signed) registry entry —
            // identity binding included — so the cache can't smuggle anything a live fetch
            // would refuse.
            return await LoadCachedIndexAsync(plugin, ex);
        }

        var index = ValidateIndex(plugin, json);
        await SaveIndexCacheAsync(plugin, json);

        _logger.Information("Fetched index for {PluginId}: {GameCount} games, {ReleaseCount} total releases",
            plugin.Id, index.Games.Count,
            index.ReleasesByGameId.Values.Sum(r => r.Count));

        return new Fetched<PluginRepoIndex> { Value = index };
    }

    /// <summary>
    /// The single acceptance gate for an index, shared by the network and cache paths: identity
    /// binding to the signed registry entry, safe-id checks, and per-release validation.
    /// </summary>
    private PluginRepoIndex ValidateIndex(PluginEntry plugin, string json)
    {
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
        //
        // Two enforcement levels, deliberately different: TRUST violations (identity spoofing,
        // non-https URLs) refuse the whole index — nothing in it can be believed. AUTHORING
        // mistakes that merely make one release unobtainable (a gate with no server and no post,
        // a release with no package source at all) drop THAT release with a warning: one stale
        // entry must never blank the entire catalog for every user (it did, live, 2026-07-24 —
        // an early beta authored before the download server existed emptied the Mods tab).
        // The AuthorTool blocks publishing these; this is the manager-side safety net.
        foreach (var (gameId, releases) in index.ReleasesByGameId)
        {
            PathSafety.EnsureSafeId(gameId, $"plugin '{plugin.Id}' release game id");
            var unobtainable = new List<ModRelease>();
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
                // any non-null gate as gated, even alongside a packageUrl, so a gate must be
                // usable: an https author server, or a numeric post id for the manual browser
                // path. A gate with neither would send users to a picker with nothing to pick.
                if (release.Patreon is not null)
                {
                    var hasServer = !string.IsNullOrWhiteSpace(release.Patreon.ServerUrl);
                    if (hasServer)
                        UrlValidator.RequireHttps(release.Patreon.ServerUrl!, $"plugin '{plugin.Id}' game '{gameId}' Patreon server URL");
                    var hasPost = !string.IsNullOrWhiteSpace(release.Patreon.PostId) &&
                                  release.Patreon.PostId!.All(char.IsAsciiDigit);
                    if (!hasServer && !hasPost)
                    {
                        _logger.Warning(
                            "Release {PluginId}/{GameId}/{Version} is Patreon-gated but has neither a server URL " +
                            "nor a numeric post id — hiding it from the catalog. Fix the release in the AuthorTool " +
                            "and republish the index.",
                            plugin.Id, gameId, release.Version);
                        unobtainable.Add(release);
                        continue;
                    }
                }

                if (release.PackageUrl is null)
                {
                    if (release.Patreon is null)
                    {
                        _logger.Warning(
                            "Release {PluginId}/{GameId}/{Version} has neither a public packageUrl nor a Patreon " +
                            "gate — hiding it from the catalog. Fix the release in the AuthorTool and republish the index.",
                            plugin.Id, gameId, release.Version);
                        unobtainable.Add(release);
                    }
                    continue;
                }
                UrlValidator.RequireHttps(release.PackageUrl, $"plugin '{plugin.Id}' game '{gameId}' package URL");
            }

            foreach (var release in unobtainable)
                releases.Remove(release);
        }

        return index;
    }

    // ---- offline cache (audit finding 33) ----

    private sealed class IndexCacheEnvelope
    {
        public DateTimeOffset FetchedAtUtc { get; set; }

        /// <summary>The signed registry entry's RepoIndexUrl the copy was fetched from. When the
        /// registry re-points a plugin's index, the old cache is refused, not served.</summary>
        public string SourceUrl { get; set; } = "";

        public string IndexJson { get; set; } = "";
    }

    /// <summary>
    /// Cache file for one plugin's index. The file name is a hash of the id, not the id itself:
    /// ids are ordinal-distinct, but Windows file names aren't (case-insensitive, and reserved
    /// device names like <c>CON</c> exist), so raw ids could collide or misbehave as file names.
    /// The id is still re-validated as a safe segment — defense in depth, not a path ingredient.
    /// </summary>
    private string IndexCachePath(string pluginId)
    {
        PathSafety.EnsureSafeId(pluginId, "plugin id");
        var nameHash = Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pluginId)))[..32];
        return Path.Combine(_cacheDirectory, nameHash + ".json");
    }

    private async Task SaveIndexCacheAsync(PluginEntry plugin, string json)
    {
        try
        {
            var envelope = new IndexCacheEnvelope
            {
                FetchedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = plugin.RepoIndexUrl.AbsoluteUri,
                IndexJson = json
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            await AtomicJson.WriteAtomicAsync(IndexCachePath(plugin.Id), bytes);
        }
        catch (Exception ex)
        {
            // Best-effort — a cache write problem must never fail a good fetch.
            _logger.Warning(ex, "Couldn't save the offline cache for plugin index {PluginId}", plugin.Id);
        }
    }

    private async Task<Fetched<PluginRepoIndex>> LoadCachedIndexAsync(PluginEntry plugin, Exception networkError)
    {
        IndexCacheEnvelope? envelope = null;
        try
        {
            var path = IndexCachePath(plugin.Id);
            if (File.Exists(path))
            {
                envelope = JsonSerializer.Deserialize<IndexCacheEnvelope>(
                    await File.ReadAllBytesAsync(path), JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Offline cache for plugin index {PluginId} is unreadable", plugin.Id);
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.IndexJson))
        {
            throw new InvalidOperationException(
                $"Couldn't reach the index for plugin '{plugin.Id}' ({networkError.Message}) and no offline " +
                "copy is saved yet.", networkError);
        }

        PluginRepoIndex index;
        try
        {
            // Exact textual identity: URI paths can be case-sensitive, so a re-point that only
            // changes case must still invalidate the cache.
            if (!string.Equals(envelope.SourceUrl, plugin.RepoIndexUrl.AbsoluteUri, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "the saved copy came from a different index address than the signed registry now points at");
            }
            index = ValidateIndex(plugin, envelope.IndexJson);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Offline cache for plugin index {PluginId} failed validation; refusing it", plugin.Id);
            throw new InvalidOperationException(
                $"Couldn't reach the index for plugin '{plugin.Id}' ({networkError.Message}), and the saved " +
                $"offline copy was rejected ({ex.Message}).", networkError);
        }

        _logger.Information("Offline: serving cached index for {PluginId} fetched {At}",
            plugin.Id, envelope.FetchedAtUtc);

        return new Fetched<PluginRepoIndex>
        {
            Value = index,
            FromCache = true,
            CachedAtUtc = envelope.FetchedAtUtc
        };
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
