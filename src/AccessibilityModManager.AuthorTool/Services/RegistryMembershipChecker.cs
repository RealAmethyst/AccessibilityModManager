using System.Net.Http;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

public sealed record RegistryMembershipResult(
    bool RegistryReachable,
    bool IsListed,
    string? Error,
    PluginEntry? Entry);

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

    public async Task<RegistryMembershipResult> CheckAsync(string pluginId, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, RegistryUrl);
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var resp = await _httpClient.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            var registry = JsonSerializer.Deserialize<PluginRegistry>(json, JsonOptions)
                ?? throw new InvalidOperationException("Registry deserialized to null");

            var entry = registry.Plugins.FirstOrDefault(p =>
                string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));

            return new RegistryMembershipResult(
                RegistryReachable: true,
                IsListed: entry != null,
                Error: null,
                Entry: entry);
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
