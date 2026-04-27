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
    private readonly RegistrySignatureVerifier? _signatureVerifier;
    private readonly ILogger _logger;

    public PluginRegistryClient(HttpClient httpClient, ILogger logger, RegistrySignatureVerifier? signatureVerifier = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _signatureVerifier = signatureVerifier;
    }

    public async Task<PluginRegistry> FetchRegistryAsync(Uri registryUrl, CancellationToken ct = default)
    {
        UrlValidator.RequireHttps(registryUrl, "plugin registry URL");

        _logger.Information("Fetching plugin registry from {Url}", registryUrl);

        var response = await SendNoCacheAsync(registryUrl, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);

        // Verify signature if verifier is configured
        if (_signatureVerifier != null)
        {
            var sigUrl = new Uri(registryUrl.AbsoluteUri + ".sig");
            _logger.Information("Fetching registry signature from {Url}", sigUrl);

            var sigResponse = await SendNoCacheAsync(sigUrl, ct);
            sigResponse.EnsureSuccessStatusCode();

            var signatureBase64 = (await sigResponse.Content.ReadAsStringAsync(ct)).Trim();

            if (!_signatureVerifier.Verify(json, signatureBase64))
                throw new InvalidOperationException(
                    "Plugin registry signature verification failed. The registry may have been tampered with.");

            _logger.Information("Registry signature verified successfully");
        }
        else
        {
            _logger.Warning("No signature verifier configured — registry signature not checked");
        }

        var registry = JsonSerializer.Deserialize<PluginRegistry>(json, JsonOptions)
            ?? throw new InvalidOperationException("Plugin registry deserialized to null");

        // Validate all plugin URLs are HTTPS
        foreach (var plugin in registry.Plugins)
        {
            UrlValidator.RequireHttps(plugin.RepoIndexUrl, $"plugin '{plugin.Id}' repoIndexUrl");
            if (plugin.Website != null)
                UrlValidator.RequireHttps(plugin.Website, $"plugin '{plugin.Id}' website");
        }

        _logger.Information("Fetched registry v{Version} with {Count} plugins",
            registry.RegistryVersion, registry.Plugins.Count);

        return registry;
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
