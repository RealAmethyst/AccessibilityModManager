using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed class PluginRegistryClient : IPluginRegistryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly RegistrySignatureVerifier _signatureVerifier;
    private readonly ILogger _logger;
    private readonly string _stateDirectory;
    private readonly string _minimumRegistryVersion;

    /// <summary>
    /// The verifier is REQUIRED: the registry is the trust anchor for everything else, and an
    /// optional verifier meant a composition mistake silently turned the whole trust chain
    /// fail-open (accepting unsigned registries with only a log line).
    /// </summary>
    /// <remarks>
    /// There is deliberately no way to lower the version floor. An override existed briefly as a
    /// test seam and was removed: "test seam only" is documentation, not an access restriction, and
    /// a constructor parameter that switches off a compiled-in security floor is one future
    /// composition mistake away from switching it off in production. The tests use registry versions
    /// above the floor instead, which costs them nothing.
    /// </remarks>
    public PluginRegistryClient(
        HttpClient httpClient,
        ILogger logger,
        RegistrySignatureVerifier signatureVerifier,
        string? highwaterDirectoryOverride = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _signatureVerifier = signatureVerifier
            ?? throw new ArgumentNullException(nameof(signatureVerifier),
                "Registry signature verification is mandatory.");
        _stateDirectory = highwaterDirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibilityModManager");
        _minimumRegistryVersion = RegistryTrustKey.MinimumRegistryVersion;
    }

    /// <summary>
    /// How many times to re-fetch the registry pair when the signature doesn't match its JSON.
    /// The two files are switched live by back-to-back renames on the server, so a request landing
    /// in the gap between them gets a new JSON with the previous signature — a mismatch that means
    /// "you caught a publish mid-flight", not "someone tampered with this". Re-fetching a moment
    /// later gets a consistent pair. Tampering fails all three attempts and still refuses, so this
    /// costs nothing in trust: no unverified content is ever accepted, only re-requested.
    /// </summary>
    private const int SignatureMismatchRetries = 3;

    public async Task<Fetched<PluginRegistry>> FetchRegistryAsync(Uri registryUrl, CancellationToken ct = default)
    {
        UrlValidator.RequireHttps(registryUrl, "plugin registry URL");

        _logger.Information("Fetching plugin registry from {Url}", registryUrl);

        string json = "";
        string signatureBase64 = "";
        var sawSignatureMismatch = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await SendNoCacheAsync(registryUrl, ct);
                response.EnsureSuccessStatusCode();

                json = await response.Content.ReadAsStringAsync(ct);

                var sigUrl = new Uri(registryUrl.AbsoluteUri + ".sig");
                _logger.Information("Fetching registry signature from {Url}", sigUrl);

                var sigResponse = await SendNoCacheAsync(sigUrl, ct);
                sigResponse.EnsureSuccessStatusCode();

                signatureBase64 = (await sigResponse.Content.ReadAsStringAsync(ct)).Trim();
            }
            catch (Exception ex) when (IsNetworkFailure(ex, ct))
            {
                // Offline (or the catalog host is down): fall back to the last registry this machine
                // ACCEPTED. Only network-level failures take this path — a bad signature, a failed
                // validation, or a replay rejection is a trust decision and must surface, never be
                // papered over with a cached copy. (Non-success HTTP statuses count as unreachable
                // on purpose: the host being misconfigured must not blank the catalog, and an
                // attacker who can force a status could just as easily block the connection.)
                //
                // Unless we already saw the live pair fail its signature: from that point on, a
                // network failure must not quietly become a cached success, or a genuine trust
                // failure would be reported as a healthy offline catalog.
                if (sawSignatureMismatch)
                {
                    _logger.Error(ex, "Registry signature didn't match, and the retry couldn't reach the server");
                    throw new InvalidOperationException(
                        "The plugin registry's signature didn't match its contents, and it couldn't be " +
                        "re-read to check whether that was a publish in progress.", ex);
                }
                return await LoadCachedRegistryAsync(registryUrl, ex);
            }

            if (_signatureVerifier.Verify(json, signatureBase64)) break;
            sawSignatureMismatch = true;
            if (attempt >= SignatureMismatchRetries) break;

            _logger.Warning(
                "Registry signature didn't match its JSON (attempt {Attempt} of {Max}) — re-fetching in case " +
                "the pair was caught mid-publish", attempt, SignatureMismatchRetries);
            await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
        }

        // Whatever the retries produced still goes through the one acceptance gate below, which
        // verifies the signature again and refuses on mismatch.
        var registry = await ValidateAndAcceptAsync(json, signatureBase64, fromCache: false);
        await SaveRegistryCacheAsync(registry.RegistryVersion, json, signatureBase64, registryUrl);

        _logger.Information("Fetched registry v{Version} with {Count} plugins",
            registry.RegistryVersion, registry.Plugins.Count);

        return new Fetched<PluginRegistry> { Value = registry };
    }

    /// <summary>
    /// The single acceptance gate, shared by the network and cache paths so a cached registry can
    /// never bypass anything a live one is held to: signature verification, per-entry validation,
    /// and the replay highwater. A tampered cache file dies on the signature exactly like a
    /// tampered download would. The cache path is CHECK-ONLY against the highwater and requires
    /// the marker to exist: a cache can only ever replay an acceptance this machine provably made,
    /// and it never advances the marker itself.
    /// </summary>
    private async Task<PluginRegistry> ValidateAndAcceptAsync(string json, string signatureBase64, bool fromCache)
    {
        if (!_signatureVerifier.Verify(json, signatureBase64))
            throw new InvalidOperationException(
                "Plugin registry signature verification failed. The registry may have been tampered with.");

        _logger.Information("Registry signature verified successfully");

        var registry = JsonSerializer.Deserialize<PluginRegistry>(json, JsonOptions)
            ?? throw new InvalidOperationException("Plugin registry deserialized to null");

        // Validate plugin entries BEFORE the replay marker moves: a signed-but-malformed
        // higher-version registry must never pin the machine to a version it refused (that
        // would lock out every valid registry below it).
        //
        // This is PluginRegistryValidation, the same pass the AuthorTool runs before signing, so the
        // two cannot drift. It used to be a near-copy of these rules living here — and the copies
        // had already diverged: the AuthorTool refused a blank registryVersion and duplicate or
        // case-colliding plugin ids, and the manager, the only side whose opinion actually protects
        // a user, did not.
        var report = PluginRegistryValidation.Validate(registry);
        if (!report.IsValid)
        {
            // The original exception where there was one, so sharing the rules does not silently
            // downgrade a SecurityException from the HTTPS check into a generic failure. Dispatched
            // rather than plainly rethrown so the stack still points at the check that objected.
            if (report.FirstFailure is { } failure)
                ExceptionDispatchInfo.Capture(failure).Throw();
            throw new InvalidOperationException(report.Errors[0]);
        }

        // The shipped floor, applied before the marker and independently of it. On a machine with no
        // marker — a fresh install, or one whose app data was cleared — the marker refuses nothing,
        // and a validly signed pre-signing registry would be accepted and take the manager down the
        // unsigned path. A constant compiled into the binary cannot be absent or rolled back.
        if (VersionComparer.Instance.Compare(registry.RegistryVersion, _minimumRegistryVersion) < 0)
        {
            throw new InvalidOperationException(
                $"The plugin registry served version {registry.RegistryVersion}, older than version " +
                $"{_minimumRegistryVersion}, which this version of the manager was built to expect. " +
                "Refusing it — an old catalog can be a stale mirror or a replayed copy. Try again " +
                "later; if it persists, the registry itself needs attention.");
        }

        // Replay guard: a validly-signed OLD registry (stale CDN, deliberate replay) must not
        // roll this machine back below the newest content it has already accepted.
        await EnforceRegistryHighwaterAsync(registry.RegistryVersion, json, checkOnly: fromCache);

        return registry;
    }

    // ---- offline cache (audit finding 33) ----

    private sealed class RegistryCacheEnvelope
    {
        public DateTimeOffset FetchedAtUtc { get; set; }

        /// <summary>The URL the copy was fetched from. A cache made under a different trust
        /// anchor (an old default URL) is refused rather than silently served.</summary>
        public string SourceUrl { get; set; } = "";

        /// <summary>The registry's own version, so cache writes can stay monotonic without
        /// re-parsing the payload.</summary>
        public string RegistryVersion { get; set; } = "";

        public string RegistryJson { get; set; } = "";
        public string SignatureBase64 { get; set; } = "";
    }

    private string RegistryCachePath => Path.Combine(_stateDirectory, "cache", "registry.json");

    private async Task SaveRegistryCacheAsync(string registryVersion, string json, string signatureBase64, Uri sourceUrl)
    {
        try
        {
            // Same cross-process lock as the marker, and never overwrite a NEWER cached version:
            // two concurrent accepts (v1 and v2) can finish their writes in either order, and a
            // last-writer-wins cache ending at v1 would then be refused offline as below the
            // highwater — exactly the state the cache exists to prevent. The lock is REQUIRED
            // here: cache persistence is best-effort, so on a wedged lock the right degradation
            // is to skip this write (the catch below), never to write unlocked and racy.
            using var crossProcessLock = await TryAcquireHighwaterLockAsync(required: true);

            try
            {
                if (File.Exists(RegistryCachePath))
                {
                    var existing = JsonSerializer.Deserialize<RegistryCacheEnvelope>(
                        await File.ReadAllBytesAsync(RegistryCachePath), JsonOptions);
                    if (existing is not null &&
                        !string.IsNullOrEmpty(existing.RegistryVersion) &&
                        VersionComparer.Instance.Compare(existing.RegistryVersion, registryVersion) > 0)
                    {
                        _logger.Information(
                            "Keeping cached registry v{Existing}; not overwriting with older v{New}",
                            existing.RegistryVersion, registryVersion);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Couldn't read the existing registry cache; overwriting it");
            }

            var envelope = new RegistryCacheEnvelope
            {
                FetchedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = sourceUrl.AbsoluteUri,
                RegistryVersion = registryVersion,
                RegistryJson = json,
                SignatureBase64 = signatureBase64
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            await AtomicJson.WriteAtomicAsync(RegistryCachePath, bytes);
        }
        catch (Exception ex)
        {
            // Cache persistence is best-effort — a full disk must never fail a good fetch.
            _logger.Warning(ex, "Couldn't save the registry offline cache");
        }
    }

    private async Task<Fetched<PluginRegistry>> LoadCachedRegistryAsync(Uri requestedUrl, Exception networkError)
    {
        RegistryCacheEnvelope? envelope = null;
        try
        {
            if (File.Exists(RegistryCachePath))
            {
                envelope = JsonSerializer.Deserialize<RegistryCacheEnvelope>(
                    await File.ReadAllBytesAsync(RegistryCachePath), JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Registry offline cache is unreadable");
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.RegistryJson))
        {
            throw new InvalidOperationException(
                $"Couldn't reach the plugin registry ({networkError.Message}) and no offline copy is saved yet. " +
                "Connect to the internet and refresh.", networkError);
        }

        PluginRegistry registry;
        try
        {
            // Exact textual identity: URI paths can be case-sensitive, so a re-point that only
            // changes case must still invalidate the cache.
            if (!string.Equals(envelope.SourceUrl, requestedUrl.AbsoluteUri, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "the saved copy came from a different registry address than the one in use");
            }
            registry = await ValidateAndAcceptAsync(envelope.RegistryJson, envelope.SignatureBase64, fromCache: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Registry offline cache failed validation; refusing it");
            throw new InvalidOperationException(
                $"Couldn't reach the plugin registry ({networkError.Message}), and the saved offline copy " +
                $"was rejected ({ex.Message}). Connect to the internet and refresh.", networkError);
        }

        _logger.Information("Offline: serving cached registry v{Version} fetched {At}",
            registry.RegistryVersion, envelope.FetchedAtUtc);

        return new Fetched<PluginRegistry>
        {
            Value = registry,
            FromCache = true,
            CachedAtUtc = envelope.FetchedAtUtc
        };
    }

    /// <summary>
    /// True for failures of the NETWORK itself — connection, DNS, non-success status, timeouts,
    /// interrupted reads — as opposed to trust/validation failures or the user cancelling.
    /// Shared with <see cref="PluginRepoClient"/> so both clients fall back identically.
    /// </summary>
    internal static bool IsNetworkFailure(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException => !ct.IsCancellationRequested, // HttpClient timeout, not the user
        HttpRequestException => true,
        SocketException => true,
        IOException => true,
        _ => false
    };

    /// <summary>
    /// Rejects a registry older than the newest one this machine has already seen — by version,
    /// and by content when the version is unchanged (the signature proves authorship, not
    /// freshness; a same-version replay carries different bytes under an already-seen number,
    /// which the publishing rules forbid). Accepted registries advance the marker atomically.
    ///
    /// <para><b>Absent and unreadable are different answers.</b> A missing marker is the ordinary
    /// state of a fresh install and must proceed — nothing could ever start otherwise — and the
    /// shipped floor is what protects that case. A marker that EXISTS and cannot be read is not a
    /// fresh install: it is this machine's ratchet in an unknown position, and continuing would
    /// silently accept a rollback of every registry published since this build. That refuses.</para>
    ///
    /// <para>The same reasoning applies to writing it. Accepting a registry this machine cannot
    /// record means the next fetch judges against a stale position, so the ratchet is lost exactly
    /// when it was supposed to advance — and nothing would ever say so. Both were previously a
    /// logged warning.</para>
    /// </summary>
    // Serializes marker read/compare/write within this process: the Mods and Developers tabs can
    // fetch concurrently, and an interleaved pair could regress the marker to the older of two
    // accepted registries. A second app COPY is serialized by the file lock below.
    private static readonly SemaphoreSlim HighwaterGate = new(1, 1);

    private async Task EnforceRegistryHighwaterAsync(string registryVersion, string registryJson, bool checkOnly)
    {
        await HighwaterGate.WaitAsync();
        try
        {
            // Required on BOTH paths. The read-compare-write it guards takes milliseconds, so a lock
            // that stays busy for the full ten-second wait is a machine in trouble, not contention —
            // and the handle is released by the OS however a holder dies, so a crashed process
            // cannot leave one behind. The network path used to proceed unlocked, which let two app
            // copies interleave and regress the marker to the older of two accepted registries.
            using var crossProcessLock = await TryAcquireHighwaterLockAsync(required: true);
            await EnforceRegistryHighwaterCoreAsync(registryVersion, registryJson, checkOnly);
        }
        finally
        {
            HighwaterGate.Release();
        }
    }

    /// <summary>
    /// Cross-process serialization of the marker's read-compare-write: without it, two app copies
    /// could each read the same marker and then write their acceptances in reverse order,
    /// regressing the marker. Waits up to ten seconds (a marker transaction takes milliseconds);
    /// after that, <paramref name="required"/> decides between failing closed and degrading to
    /// the process-local guarantee.
    /// </summary>
    private async Task<FileStream?> TryAcquireHighwaterLockAsync(bool required)
    {
        var lockPath = Path.Combine(_stateDirectory, "registry-highwater.lock");
        try
        {
            Directory.CreateDirectory(_stateDirectory);
        }
        catch (Exception ex)
        {
            if (required)
                throw new InvalidOperationException("couldn't prepare the registry marker lock", ex);
            _logger.Warning(ex, "Couldn't create the state directory for the registry marker lock");
            return null;
        }

        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                if (required)
                    throw new InvalidOperationException("couldn't acquire the registry marker lock", ex);
                _logger.Warning(ex, "Couldn't acquire the registry marker lock; continuing with in-process serialization only");
                return null;
            }
        }

        if (required)
            throw new InvalidOperationException("the registry marker lock stayed busy");
        _logger.Warning("Registry marker lock stayed busy; continuing with in-process serialization only");
        return null;
    }

    private async Task EnforceRegistryHighwaterCoreAsync(string registryVersion, string registryJson, bool checkOnly)
    {
        var markerPath = Path.Combine(_stateDirectory, "registry-highwater.txt");
        string? seenVersion = null;
        string? seenSha = null;

        // Absence is tracked EXPLICITLY rather than inferred from `seenVersion` being empty. There
        // are two ways to end up with no version — the file not existing, and the file existing but
        // saying nothing — and only the first may proceed. Inferring it conflated them: a marker
        // truncated to zero bytes read successfully, left `seenVersion` null, skipped the comparison
        // entirely and then recorded whatever arrived. A machine that had accepted version 5 would
        // accept a replayed version 4, which is precisely the protection this exists to provide.
        var markerAbsent = false;
        try
        {
            var lines = File.ReadAllLines(markerPath);
            seenVersion = lines.Length > 0 ? lines[0].Trim() : null;
            seenSha = lines.Length > 1 ? lines[1].Trim() : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Genuinely absent — a fresh install, or app data cleared. The only state that may
            // proceed without a recorded position, and the shipped floor is what guards it.
            // Deliberately NOT File.Exists first: that answers a different question a moment
            // earlier, and a permission error from it would have read as absence.
            markerAbsent = true;
        }
        catch (Exception ex)
        {
            // Present and unreadable. Continuing here would accept a rollback of everything
            // published since this build, silently, on the one machine that had the evidence.
            _logger.Error(ex, "Registry high-water marker exists but couldn't be read");
            throw new InvalidOperationException(
                "This machine's record of which catalog versions it has already accepted couldn't be " +
                "read, so an older catalog can't be told apart from a current one. The catalog wasn't " +
                "refreshed.", ex);
        }

        // Read fine and says nothing: truncated, emptied, or otherwise corrupt. Same consequence as
        // unreadable, so the same refusal — and it must NOT overwrite the file, because the position
        // it would record is the one being replayed.
        //
        // Only the VERSION is required. A marker carrying a version but no hash is treated as
        // "version known, content not" by the comparison below, exactly as it always has been;
        // demanding a hash here could lock out a marker written by an earlier build.
        if (!markerAbsent && string.IsNullOrEmpty(seenVersion))
        {
            _logger.Error("Registry high-water marker at {Path} is present but empty", markerPath);
            throw new InvalidOperationException(
                "This machine's record of which catalog versions it has already accepted is damaged, " +
                "so an older catalog can't be told apart from a current one. The catalog wasn't " +
                "refreshed.");
        }

        // A cached copy may only REPLAY an acceptance this machine provably made: no marker means no
        // proven acceptance, so the cache is refused rather than treated as a first fetch — deleting
        // the marker must not let an old cache back in.
        if (checkOnly && markerAbsent)
        {
            throw new InvalidOperationException(
                "no record of this machine ever accepting a registry, so the saved copy can't be trusted");
        }

        var sha = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(registryJson)));

        if (!string.IsNullOrEmpty(seenVersion))
        {
            var cmp = VersionComparer.Instance.Compare(registryVersion, seenVersion);
            if (cmp < 0)
            {
                throw new InvalidOperationException(
                    $"The plugin registry served version {registryVersion}, older than version {seenVersion} this " +
                    "machine has already seen. Refusing it — this can be a stale mirror or a replayed old copy. " +
                    "Try again later; if it persists, the registry itself needs attention.");
            }
            if (cmp == 0)
            {
                // A marker naming a version but no content hash is a TRUNCATED one. Every build that
                // has ever written this file wrote both lines (since 580692a), so there is no legacy
                // one-line format to be tolerant of — checked in the history rather than assumed,
                // after a first pass declined this fix on exactly that unverified assumption.
                //
                // Tolerating it silently drops the same-version half of the ratchet, which is the
                // half guarding the publishing failure the hash exists for: two differently signed
                // documents under one version number. The replayed one would be accepted AND its
                // hash written into the marker, making the loss permanent. A strictly newer version
                // still passes below and rebuilds the marker with both lines.
                if (string.IsNullOrEmpty(seenSha))
                {
                    _logger.Error("Registry high-water marker at {Path} names version {Version} but no content hash",
                        markerPath, seenVersion);
                    throw new InvalidOperationException(
                        "This machine's record of the catalog it has already accepted is incomplete, so a " +
                        "different catalog carrying the same version number can't be told apart from the one " +
                        "already trusted. The catalog wasn't refreshed.");
                }

                if (!string.Equals(sha, seenSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The plugin registry's content changed without a version bump (still {registryVersion}). " +
                        "Refusing it — this can be a replayed old copy, or the registry was republished without " +
                        "raising registryVersion (which its publishing tool now enforces). A newer registryVersion fixes this.");
                }
            }
        }

        if (checkOnly)
            return; // the cache path never advances the marker

        // Already recorded, exactly. Writing would reproduce the file byte for byte, so there is
        // nothing to advance and nothing a failure could cost — while insisting on the write turns a
        // read-only marker or a restrictive ACL into a catalog that refuses to refresh every time,
        // even as the server returns precisely the registry this machine already trusts. Unlike a
        // full disk, a permissions fault does not clear itself.
        if (!markerAbsent &&
            VersionComparer.Instance.Compare(registryVersion, seenVersion) == 0 &&
            string.Equals(sha, seenSha, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await AtomicJson.WriteAtomicAsync(markerPath,
                System.Text.Encoding.UTF8.GetBytes(registryVersion + Environment.NewLine + sha));
        }
        catch (Exception ex)
        {
            // Refusing a registry that verified is a real cost, and it is the smaller one. Accepting
            // it without recording it leaves the next fetch judging against a stale position, so the
            // ratchet is lost at the moment it should have advanced and no later run can tell. The
            // condition is also self-correcting: whatever stopped the write is a local fault, and
            // the next refresh succeeds once it is gone.
            _logger.Error(ex, "Couldn't persist registry high-water marker");
            throw new InvalidOperationException(
                "This catalog verified, but the record of having accepted it couldn't be saved, so an " +
                "older catalog couldn't be refused later. The catalog wasn't refreshed.", ex);
        }
    }

    /// <summary>
    /// GET <paramref name="url"/> with both <c>Cache-Control: no-cache</c> and a unique
    /// cache-buster query parameter. The header alone isn't enough — GitHub's raw-URL CDN
    /// (Fastly) ignores client revalidation hints for under-a-minute-old objects, so users
    /// see content from minutes ago for the window right after a push. Appending
    /// <c>?_=&lt;ms&gt;</c> makes every request a different URL from the CDN's perspective,
    /// forcing it to fetch from origin.
    /// </summary>
    private Task<HttpResponseMessage> SendNoCacheAsync(Uri url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, AppendCacheBuster(url));
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        return _httpClient.SendAsync(request, ct);
    }

    /// <summary>
    /// Adds a fresh <c>_=&lt;unix-ms&gt;</c> query parameter so each request looks unique to
    /// CDNs. Preserves any existing query string.
    /// </summary>
    internal static Uri AppendCacheBuster(Uri url)
    {
        var separator = string.IsNullOrEmpty(url.Query) ? "?" : "&";
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new Uri(url.AbsoluteUri + separator + "_=" + ms);
    }
}
