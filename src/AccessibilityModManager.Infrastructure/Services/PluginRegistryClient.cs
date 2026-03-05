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
    private readonly ILogger _logger;

    public PluginRegistryClient(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PluginRegistry> FetchRegistryAsync(Uri registryUrl, CancellationToken ct = default)
    {
        UrlValidator.RequireHttps(registryUrl, "plugin registry URL");

        _logger.Information("Fetching plugin registry from {Url}", registryUrl);

        var response = await _httpClient.GetAsync(registryUrl, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
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
}
