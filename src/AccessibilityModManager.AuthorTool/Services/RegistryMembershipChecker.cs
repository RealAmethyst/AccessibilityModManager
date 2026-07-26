using System.Net.Http;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

public sealed record RegistryMembershipResult(
    bool RegistryReachable,
    bool IsListed,
    string? Error,
    PluginEntry? Entry)
{
    /// <summary>
    /// The registry JSON exactly as fetched, and only ever set once its signature verified against
    /// the offline key. Callers that read trust-bearing data out of the registry — which key signs a
    /// plugin's claims, where its index lives — must use this rather than re-fetching, because an
    /// unverified registry can say anything.
    /// </summary>
    public string? VerifiedJson { get; init; }

    /// <summary>
    /// True when the registry was reachable but its signature did not verify. Distinct from simply
    /// unreachable: one is a network problem, the other means the file has been tampered with or a
    /// publish is half-finished, and no trust decision may be made from it.
    /// </summary>
    public bool SignatureFailed { get; init; }
}

/// <summary>
/// Checks whether a plugin id appears in the public registry. Used by the AuthorTool to tell
/// authors whether their plugin is listed yet, and to surface a feature-request action when
/// it isn't. Read-only — modifying the registry is a maintainer task.
/// </summary>
public sealed class RegistryMembershipChecker
{
    public static readonly Uri RegistryUrl =
        // The registry's canonical home is the VPS (GitHub retired with the publish-to-VPS
        // move); this must match the manager's own hardcoded trust anchor in AppConfig.
        new("https://accessibilitymods.com/registry/plugin-registry.json");

    public const string RegistryRepo = "RealAmethyst/accessibility-mod-manager-registry";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public RegistryMembershipChecker(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the detached signature and checks it over the exact bytes the manager would hash:
    /// the response body decoded as UTF-8 text, matching RegistrySignatureVerifier.
    /// </summary>
    private async Task<bool> VerifySignatureAsync(string registryJson, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(RegistryUrl.AbsoluteUri + ".sig"));
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var resp = await _httpClient.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var signature = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            return new RegistrySignatureVerifier(RegistryTrustKey.PublicKeyPem, _logger)
                .Verify(registryJson, signature);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't fetch or check the registry signature");
            return false;
        }
    }

    public async Task<RegistryMembershipResult> CheckAsync(string pluginId, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, RegistryUrl);
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var resp = await _httpClient.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);

            // Verify before believing any of it. This check did not exist before, which meant the
            // tool compared index URLs — and would have chosen a claim-signing key — against a
            // document nothing vouched for.
            var signatureVerified = await VerifySignatureAsync(json, ct);
            if (!signatureVerified)
            {
                _logger.Error("The registry's signature did not verify; refusing to trust its contents");
                return new RegistryMembershipResult(
                    RegistryReachable: true, IsListed: false,
                    Error: "The registry's signature did not verify.", Entry: null)
                {
                    SignatureFailed = true
                };
            }

            var registry = JsonSerializer.Deserialize<PluginRegistry>(json, JsonOptions)
                ?? throw new InvalidOperationException("Registry deserialized to null");

            var entry = registry.Plugins.FirstOrDefault(p =>
                string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));

            return new RegistryMembershipResult(
                RegistryReachable: true,
                IsListed: entry != null,
                Error: null,
                Entry: entry)
            {
                VerifiedJson = json
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not check registry membership for {PluginId}", pluginId);
            return new RegistryMembershipResult(
                RegistryReachable: false,
                IsListed: false,
                Error: ex.Message,
                Entry: null);
        }
    }
}
