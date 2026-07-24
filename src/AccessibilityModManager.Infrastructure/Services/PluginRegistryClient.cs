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
    private readonly string _highwaterDirectory;

    /// <summary>
    /// The verifier is REQUIRED: the registry is the trust anchor for everything else, and an
    /// optional verifier meant a composition mistake silently turned the whole trust chain
    /// fail-open (accepting unsigned registries with only a log line).
    /// </summary>
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
        _highwaterDirectory = highwaterDirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibilityModManager");
    }

    public async Task<PluginRegistry> FetchRegistryAsync(Uri registryUrl, CancellationToken ct = default)
    {
        UrlValidator.RequireHttps(registryUrl, "plugin registry URL");

        _logger.Information("Fetching plugin registry from {Url}", registryUrl);

        var response = await SendNoCacheAsync(registryUrl, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);

        var sigUrl = new Uri(registryUrl.AbsoluteUri + ".sig");
        _logger.Information("Fetching registry signature from {Url}", sigUrl);

        var sigResponse = await SendNoCacheAsync(sigUrl, ct);
        sigResponse.EnsureSuccessStatusCode();

        var signatureBase64 = (await sigResponse.Content.ReadAsStringAsync(ct)).Trim();

        if (!_signatureVerifier.Verify(json, signatureBase64))
            throw new InvalidOperationException(
                "Plugin registry signature verification failed. The registry may have been tampered with.");

        _logger.Information("Registry signature verified successfully");

        var registry = JsonSerializer.Deserialize<PluginRegistry>(json, JsonOptions)
            ?? throw new InvalidOperationException("Plugin registry deserialized to null");

        // Validate plugin entries BEFORE the replay marker moves: a signed-but-malformed
        // higher-version registry must never pin the machine to a version it refused (that
        // would lock out every valid registry below it).
        foreach (var plugin in registry.Plugins)
        {
            PathSafety.EnsureSafeId(plugin.Id, "registry plugin id");
            UrlValidator.RequireHttps(plugin.RepoIndexUrl, $"plugin '{plugin.Id}' repoIndexUrl");
            if (plugin.Website != null)
                UrlValidator.RequireHttps(plugin.Website, $"plugin '{plugin.Id}' website");
            foreach (var (linkName, linkUri) in plugin.Links)
                UrlValidator.RequireHttps(linkUri, $"plugin '{plugin.Id}' link '{linkName}'");
        }

        // Replay guard: a validly-signed OLD registry (stale CDN, deliberate replay) must not
        // roll this machine back below the newest content it has already accepted.
        await EnforceRegistryHighwaterAsync(registry.RegistryVersion, json);

        _logger.Information("Fetched registry v{Version} with {Count} plugins",
            registry.RegistryVersion, registry.Plugins.Count);

        return registry;
    }

    /// <summary>
    /// Rejects a registry older than the newest one this machine has already seen — by version,
    /// and by content when the version is unchanged (the signature proves authorship, not
    /// freshness; a same-version replay carries different bytes under an already-seen number,
    /// which the publishing rules forbid). Accepted registries advance the marker atomically.
    /// The marker is plain local state — an unreadable marker is treated as absent (fail open
    /// on the MARKER, closed on the registry signature).
    /// </summary>
    // Serializes marker read/compare/write: the Mods and Developers tabs can fetch concurrently,
    // and an interleaved pair could regress the marker to the older of two accepted registries.
    private static readonly SemaphoreSlim HighwaterGate = new(1, 1);

    private async Task EnforceRegistryHighwaterAsync(string registryVersion, string registryJson)
    {
        await HighwaterGate.WaitAsync();
        try
        {
            await EnforceRegistryHighwaterCoreAsync(registryVersion, registryJson);
        }
        finally
        {
            HighwaterGate.Release();
        }
    }

    private async Task EnforceRegistryHighwaterCoreAsync(string registryVersion, string registryJson)
    {
        var markerPath = Path.Combine(_highwaterDirectory, "registry-highwater.txt");
        string? seenVersion = null;
        string? seenSha = null;
        try
        {
            if (File.Exists(markerPath))
            {
                var lines = File.ReadAllLines(markerPath);
                seenVersion = lines.Length > 0 ? lines[0].Trim() : null;
                seenSha = lines.Length > 1 ? lines[1].Trim() : null;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't read registry high-water marker; treating as first fetch");
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
            if (cmp == 0 && !string.IsNullOrEmpty(seenSha) &&
                !string.Equals(sha, seenSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The plugin registry's content changed without a version bump (still {registryVersion}). " +
                    "Refusing it — this can be a replayed old copy, or the registry was republished without " +
                    "raising registryVersion (which its publishing tool now enforces). A newer registryVersion fixes this.");
            }
        }

        try
        {
            await AtomicJson.WriteAtomicAsync(markerPath,
                System.Text.Encoding.UTF8.GetBytes(registryVersion + Environment.NewLine + sha));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't persist registry high-water marker");
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
