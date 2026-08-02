using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed class PluginRepoClient : IPluginRepoClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Decodes exactly what arrived, or refuses. <c>ReadAsStringAsync</c> does neither: it applies
    /// the REPLACEMENT fallback, so invalid bytes in an unsigned field become U+FFFD and travel on
    /// as content, and it honours a server-supplied charset, so a <c>charset=utf-16</c> header would
    /// change how the whole document is read. Both are the server choosing how its own file is
    /// interpreted.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly string _cacheDirectory;
    private readonly ClaimReplayStore _replayStore;

    /// <param name="stateRootOverride">
    /// Root for everything this client persists. One root rather than a cache path, so a caller
    /// cannot redirect the cache while leaving the replay records pointed at the real machine state —
    /// which is how a test would silently write to a user's app data, or a fixture would ratchet the
    /// real one.
    /// </param>
    /// <param name="responseDeadlineOverride">
    /// How long a response body may take once its headers have arrived. A test seam, and a safe one:
    /// it is a patience setting, not a security floor — shortening it can only produce a refusal that
    /// falls back to the saved copy, never an acceptance. Without it the stalled-host case could only
    /// be asserted, not demonstrated.
    /// </param>
    public PluginRepoClient(
        HttpClient httpClient, ILogger logger, string? stateRootOverride = null,
        TimeSpan? responseDeadlineOverride = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _responseDeadline = responseDeadlineOverride ?? DefaultResponseDeadline;

        var stateRoot = stateRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibilityModManager");

        _cacheDirectory = Path.Combine(stateRoot, "cache", "indexes");

        // NOT under the cache directory. Clearing a cache is a routine recovery step; it must not
        // also erase this machine's evidence of which catalog versions it has already accepted,
        // because that evidence is the whole of what makes a withdrawal stick.
        _replayStore = new ClaimReplayStore(Path.Combine(stateRoot, "claim-highwater"), logger);
    }

    public async Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(PluginEntry plugin, CancellationToken ct = default)
    {
        UrlValidator.RequireHttps(plugin.RepoIndexUrl, $"plugin '{plugin.Id}' repo index");

        // Before a byte is fetched. A plugin whose trust state is unknown or unusable has no catalog
        // this manager may read, and finding that out after downloading is finding it out late.
        RequireUsableTrust(plugin);

        _logger.Information("Fetching repo index for plugin {PluginId} from {Url}", plugin.Id, plugin.RepoIndexUrl);

        PluginRepoIndex index;
        try
        {
            byte[] bytes;
            try
            {
                bytes = await FetchBoundedAsync(plugin, ct);
            }
            catch (Exception ex) when (PluginRegistryClient.IsNetworkFailure(ex, ct))
            {
                // Offline, unreachable, or a body that stopped arriving: fall back to the last index
                // this machine accepted for THIS plugin. The cached copy is re-validated in full
                // against the CURRENT (signed) registry entry — signature, identity binding, replay
                // records — so the cache can't smuggle anything a live fetch would refuse.
                return await LoadCachedIndexAsync(plugin, ex);
            }

            // The acceptance and the snapshot that records it happen inside ONE transaction per trust
            // context. Split, two app copies can interleave: A accepts sequence 2 and stalls, B
            // accepts 3 and saves, A resumes and overwrites the snapshot with 2 — leaving durable
            // replay state at 3 and the only saved copy something it will correctly refuse offline.
            index = await AcceptIndexAsync(plugin, bytes, fromCache: false,
                onAccepted: () => SaveIndexCacheAsync(plugin, bytes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The live document was reached and refused — by verification, by validation, or by
            // being too large to accept at all. Refusing it does not mean erasing the last good one:
            // a previously accepted copy that independently passes the whole gate again is still
            // evidence of what this author published, and a hostile server can withhold a response
            // entirely, so blanking the catalog buys nothing. It IS shown with a warning, never
            // silently.
            //
            // The bounded READ is inside this scope deliberately. It used to sit in its own try
            // above, so an oversized live body escaped past the fallback and the plugin vanished
            // instead of showing its saved verified catalog.
            var lastVerified = await TryLastVerifiedAsync(plugin, ex);
            if (lastVerified is not null) return lastVerified;
            throw;
        }

        _logger.Information("Fetched index for {PluginId}: {GameCount} games, {ReleaseCount} total releases",
            plugin.Id, index.Games.Count,
            index.ReleasesByGameId.Values.Sum(r => r.Count));

        return new Fetched<PluginRepoIndex> { Value = index };
    }

    /// <summary>
    /// Fetches the index body with a hard ceiling AND a hard deadline.
    ///
    /// <para><c>ResponseHeadersRead</c> is what makes the ceiling real, and it is also what removes
    /// <c>HttpClient.Timeout</c> from the picture: that timeout covers the send, and with headers-only
    /// completion the send is already over. A host that answers <c>200 OK</c> and then simply stops
    /// sending would otherwise stall this read forever — and the Mods refresh walks plugins in
    /// sequence, so one such host hangs every plugin after it and the offline fallback with them.</para>
    ///
    /// <para>The deadline is linked to the caller's token, so a timeout is distinguishable from the
    /// user cancelling: cancellation propagates, while the deadline firing is classed as a network
    /// failure and is eligible for the saved copy.</para>
    /// </summary>
    private async Task<byte[]> FetchBoundedAsync(PluginEntry plugin, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_responseDeadline);

        // Cache-Control: no-cache + unique query param. GitHub's raw-URL CDN (Fastly) ignores
        // header-only revalidation hints for under-a-minute-old objects, so we make each
        // request a unique URL too — that forces the CDN to fetch origin and the
        // freshly-pushed index.json shows up immediately.
        var bustedUrl = PluginRegistryClient.AppendCacheBuster(plugin.RepoIndexUrl);
        var request = new HttpRequestMessage(HttpMethod.Get, bustedUrl);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        request.Headers.Pragma.ParseAdd("no-cache");

        // ResponseHeadersRead, or the ceiling below is decoration: the default completion option
        // buffers the ENTIRE body inside SendAsync, so a bound applied afterwards refuses just as
        // loudly having already held all of it. Caught by a test that counts the bytes the server
        // was asked for, rather than only that the refusal happened.
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();

        return await ReadBoundedAsync(response.Content, plugin.Id, deadline.Token);
    }

    /// <summary>
    /// How long a whole response may take once its headers have arrived. Generous — a large catalog
    /// on a slow connection is normal — but finite, which is the point.
    /// </summary>
    internal static readonly TimeSpan DefaultResponseDeadline = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _responseDeadline;

    /// <summary>
    /// The last copy this machine accepted, re-run through the entire gate, or null when there isn't
    /// one that passes. Never advances anything: the offline path is check-only by construction.
    /// </summary>
    private async Task<Fetched<PluginRepoIndex>?> TryLastVerifiedAsync(PluginEntry plugin, Exception liveFailure)
    {
        try
        {
            var cached = await LoadCachedIndexAsync(plugin, liveFailure);

            _logger.Warning(liveFailure,
                "Live catalog for {PluginId} refused; serving the last copy this machine accepted", plugin.Id);

            return new Fetched<PluginRepoIndex>
            {
                Value = cached.Value,
                FromCache = true,
                CachedAtUtc = cached.CachedAtUtc,
                LiveRejectionReason = liveFailure.Message
            };
        }
        catch (Exception ex)
        {
            // No usable saved copy either. The live failure is the one worth reporting — it is what
            // actually happened — so this one is only logged.
            _logger.Warning(ex, "No saved copy of {PluginId} could stand in for the refused live catalog", plugin.Id);
            return null;
        }
    }

    /// <summary>
    /// Refuses a plugin whose trust state is anything but a resolved answer.
    ///
    /// <para><see cref="IndexTrustStatus.Unresolved"/> means no registry acceptance ever spoke for
    /// this entry — a composition path that skipped the gate. It cannot be read as "unsigned",
    /// because that is a permission. <see cref="IndexTrustStatus.Unusable"/> is the registry naming
    /// a key that cannot be used; it refuses this plugin and leaves the rest of the catalog
    /// working.</para>
    /// </summary>
    private static void RequireUsableTrust(PluginEntry plugin)
    {
        var trust = plugin.IndexTrust;

        if (trust.Status == IndexTrustStatus.Unresolved)
        {
            throw new InvalidOperationException(
                $"The signing key for plugin '{plugin.Id}' was never looked up, so there is no way to " +
                "tell whether its catalog should be signed. Refusing it.");
        }

        if (trust.Status == IndexTrustStatus.Unusable)
        {
            // The reason comes from the registry reader and is already written to be read aloud. It
            // is quoted rather than re-worded, because whoever surfaces it adds the framing — the
            // AuthorTool says it to a publisher, the manager to a user.
            throw new InvalidOperationException(
                $"The registry's signing key for this developer can't be used — {trust.Reason}. The " +
                "registry needs fixing before their mods can appear.");
        }
    }

    /// <summary>
    /// Reads the response body with a hard ceiling, so a hostile or broken host cannot make the
    /// manager buffer an unbounded amount before anything looks at it. <c>Content-Length</c> is not
    /// used as the bound: it is the server's claim about the server's own body.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, string pluginId, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > ClaimProof.MaxIndexBytes)
            {
                throw new InvalidOperationException(
                    $"The catalog for plugin '{pluginId}' is larger than {ClaimProof.MaxIndexBytes} bytes. " +
                    "Refusing it.");
            }
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The one acceptance gate for index BYTES, shared by the network and cache paths.
    ///
    /// <para>Anchored: the proof must be present, every signature must verify, and the catalog that
    /// comes out is the one rebuilt from the claims — never the plaintext it travelled beside, which
    /// no signature covers. The claim replay records are consulted and advanced here, because a
    /// signature says who wrote a catalog and nothing at all about whether it is the current
    /// one.</para>
    ///
    /// <para>Unanchored: exactly the path it has always been.</para>
    /// </summary>
    /// <param name="onAccepted">
    /// Runs inside the replay store's transaction, after the records are committed. The snapshot
    /// write goes here so it cannot be reordered against another app copy's acceptance.
    /// </param>
    private async Task<PluginRepoIndex> AcceptIndexAsync(
        PluginEntry plugin, byte[] bytes, bool fromCache, Func<Task>? onAccepted = null)
    {
        if (bytes.Length > ClaimProof.MaxIndexBytes)
        {
            throw new InvalidOperationException(
                $"The catalog for plugin '{plugin.Id}' is larger than {ClaimProof.MaxIndexBytes} bytes. Refusing it.");
        }

        if (plugin.IndexTrust.Status != IndexTrustStatus.Anchored)
        {
            var unsigned = ValidateIndex(plugin, DecodeUnsigned(bytes, plugin.Id));
            if (onAccepted is not null) await onAccepted();
            return unsigned;
        }

        var anchor = plugin.IndexTrust.Anchor!;

        ClaimProofDocument? proofDocument;
        try
        {
            proofDocument = ClaimProof.TryExtract(bytes);
        }
        catch (ClaimFormatException ex)
        {
            throw new CatalogRefusedException(plugin.Id,
                $"Its signature block couldn't be read: {ex.Message}.", ex);
        }

        if (proofDocument is null)
        {
            throw new CatalogRefusedException(plugin.Id,
                "The registry says this developer signs their catalog, and the catalog served carries " +
                "no signature at all.");
        }

        VerifiedProof proof;
        try
        {
            proof = ClaimProof.ReadVerified(proofDocument, anchor, requireManifest: false);
        }
        catch (ClaimFormatException ex)
        {
            throw new CatalogRefusedException(plugin.Id,
                $"Its signature didn't check out: {ex.Message}.", ex);
        }

        // ONLY the projection. The plaintext catalog beside a proof is not covered by it, so a server
        // is free to rewrite it, and anyone acting on the published catalog has to read it from the
        // claims instead of from what they were handed. Built BEFORE the records advance, so a
        // catalog that fails validation never leaves a record saying it was accepted.
        var index = ValidateIndex(plugin, proof.CatalogJson);

        // After verification, before anything is shown. A perfectly signed proof can still be an old
        // one replayed to undo a withdrawal.
        await _replayStore.AcceptAsync(anchor, proof.Claims, checkOnly: fromCache, onCommitted: onAccepted);

        return index;
    }

    /// <summary>
    /// Decodes an unsigned index. A leading UTF-8 BOM is tolerated, because unsigned indexes are
    /// third-party files that some editors write one into and this path has always accepted them —
    /// unlike the signed path, where <see cref="ClaimProof.TryExtract"/> refuses one outright.
    /// </summary>
    private static string DecodeUnsigned(byte[] bytes, string pluginId)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

        try
        {
            return StrictUtf8.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                $"The catalog for plugin '{pluginId}' is not valid UTF-8 text. Refusing it.", ex);
        }
    }

    /// <summary>
    /// The single acceptance gate for an index, shared by the network and cache paths: identity
    /// binding to the signed registry entry, safe-id checks, and per-release validation.
    /// </summary>
    private PluginRepoIndex ValidateIndex(PluginEntry plugin, string json)
    {
        // THE validation lives in PluginIndexValidation, shared with the AuthorTool so the
        // author's own checks match exactly what this client enforces. Trust violations refuse
        // the whole index; unobtainable releases are dropped one by one with a warning (one
        // stale entry must never blank the entire catalog - it did, live, 2026-07-24).
        IndexValidationReport report;
        try
        {
            report = PluginIndexValidation.Validate(plugin.Id, json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A parser's message names CLR types, JSON paths, line numbers and byte offsets. That
            // belongs in the log, not read aloud to somebody who asked for a list of mods.
            throw new CatalogRefusedException(plugin.Id,
                "Its catalog isn't in a form the manager can read.", ex);
        }

        if (report.TrustErrors.Count > 0)
            throw new CatalogRefusedException(plugin.Id, report.TrustErrors[0]);

        foreach (var problem in report.UnobtainableReleases)
        {
            _logger.Warning("{Problem} Hiding it from the catalog - fix the release in the AuthorTool " +
                            "and republish the index.", problem);
        }

        return report.Index;
    }

    // ---- offline cache (audit finding 33) ----

    private sealed class IndexCacheEnvelope
    {
        /// <summary>
        /// Envelope format. Version 1 stored the plaintext catalog; version 2 stores the exact
        /// accepted bytes, proof included.
        ///
        /// <para>A v1 copy is refused for an ANCHORED plugin and still used for an unanchored one.
        /// For an anchored plugin it is a catalog with no proof, and there is no honest way to
        /// manufacture the evidence it never held. For an unanchored plugin nothing was ever
        /// verified and nothing is being skipped, so refusing it would take away the offline catalog
        /// — and with it every installed mod's row, which is where uninstall lives — from a user who
        /// upgraded while offline, over a check that does not apply to them.</para>
        /// </summary>
        public int Version { get; set; }

        public DateTimeOffset FetchedAtUtc { get; set; }

        /// <summary>The signed registry entry's RepoIndexUrl the copy was fetched from. When the
        /// registry re-points a plugin's index, the old cache is refused, not served.</summary>
        public string SourceUrl { get; set; } = "";

        /// <summary>
        /// The exact bytes that were accepted, proof included. Re-verified in full on the way back
        /// out, so a cached signed catalog is held to precisely what a live one is. Version 2 only.
        /// </summary>
        public string IndexBase64 { get; set; } = "";

        /// <summary>The plaintext catalog, as version 1 stored it. Read only for an unanchored plugin.</summary>
        public string IndexJson { get; set; } = "";
    }

    private const int CacheEnvelopeVersion = 2;

    /// <summary>The envelope holds the index as base64, so it needs headroom over the index ceiling.</summary>
    private const int MaxCacheEnvelopeBytes = (int)(ClaimProof.MaxIndexBytes * 1.5) + 4096;

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

    private async Task SaveIndexCacheAsync(PluginEntry plugin, byte[] indexBytes)
    {
        try
        {
            var envelope = new IndexCacheEnvelope
            {
                Version = CacheEnvelopeVersion,
                FetchedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = plugin.RepoIndexUrl.AbsoluteUri,
                IndexBase64 = Convert.ToBase64String(indexBytes)
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
                    BoundedFile.ReadAllBytes(path, MaxCacheEnvelopeBytes, "saved catalog"), JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Offline cache for plugin index {PluginId} is unreadable", plugin.Id);
        }

        // A copy written before this manager verified anything carries the plaintext catalog and no
        // proof. It is still perfectly good for a plugin the registry names no key for.
        var legacy = envelope is not null && envelope.Version != CacheEnvelopeVersion;

        if (envelope is null ||
            string.IsNullOrEmpty(legacy ? envelope.IndexJson : envelope.IndexBase64))
        {
            throw new InvalidOperationException(
                $"Couldn't reach the index for plugin '{plugin.Id}' ({networkError.Message}) and no offline " +
                "copy is saved yet.", networkError);
        }

        if (legacy && plugin.IndexTrust.Status == IndexTrustStatus.Anchored)
        {
            throw new InvalidOperationException(
                $"Couldn't reach the index for plugin '{plugin.Id}' ({networkError.Message}), and the saved " +
                "offline copy was made before this version checked signatures, so it carries none to " +
                "check. Connect to the internet and refresh.", networkError);
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

            if (legacy)
            {
                // Unanchored, by the check above. Nothing was ever verified about this catalog and
                // nothing is being skipped — the same path it has always taken.
                index = ValidateIndex(plugin, envelope.IndexJson);
            }
            else
            {
                byte[] cachedBytes;
                try
                {
                    // Refused BEFORE decoding: base64 is four characters per three bytes, so a
                    // string that decodes past the index ceiling is already over it here.
                    if ((long)envelope.IndexBase64.Length / 4 * 3 > ClaimProof.MaxIndexBytes)
                        throw new InvalidOperationException("the saved copy is larger than a catalog may be");

                    cachedBytes = Convert.FromBase64String(envelope.IndexBase64);
                }
                catch (FormatException ex)
                {
                    throw new InvalidOperationException("the saved copy is not readable", ex);
                }

                // checkOnly: a cached copy may only REPLAY an acceptance this machine provably made,
                // so every claim in it must already be recorded, and none of them advance anything.
                index = await AcceptIndexAsync(plugin, cachedBytes, fromCache: true);
            }
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
