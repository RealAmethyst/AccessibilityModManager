using System.Net.Http;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

public sealed record IndexValidationResult(
    bool Ok,
    string? Error,
    int GameCount,
    int ReleaseCount);

/// <summary>
/// Per Q3=C: fetch the proposed plugin's index.json, deserialize it, and check the basics
/// (claimed pluginId matches, at least one release present). Surfaces a green/red status next
/// to each issue so the maintainer can see at a glance whether to accept.
/// </summary>
public sealed class IndexValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public IndexValidator(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IndexValidationResult> ValidateAsync(PluginEntry entry, CancellationToken ct = default)
    {
        try
        {
            if (entry.RepoIndexUrl.Scheme != "https")
                return new IndexValidationResult(false, "RepoIndexUrl must use https://.", 0, 0);

            using var req = new HttpRequestMessage(HttpMethod.Get, entry.RepoIndexUrl);
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return new IndexValidationResult(false,
                    $"index.json fetch returned HTTP {(int)resp.StatusCode}.", 0, 0);

            var json = await resp.Content.ReadAsStringAsync(ct);
            PluginRepoIndex? index;
            try
            {
                index = JsonSerializer.Deserialize<PluginRepoIndex>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                return new IndexValidationResult(false, $"index.json is not valid JSON: {ex.Message}", 0, 0);
            }

            if (index == null)
                return new IndexValidationResult(false, "index.json deserialized to null.", 0, 0);

            if (!string.Equals(index.PluginId, entry.Id, StringComparison.OrdinalIgnoreCase))
                return new IndexValidationResult(false,
                    $"Plugin ID mismatch: registry entry says \"{entry.Id}\" but the index.json says \"{index.PluginId}\".",
                    0, 0);

            var releaseCount = index.ReleasesByGameId.Values.Sum(rs => rs.Count);
            if (releaseCount == 0)
                return new IndexValidationResult(false,
                    "index.json has no releases yet. Wait until the author publishes at least one before accepting.",
                    index.Games.Count, 0);

            return new IndexValidationResult(true, null, index.Games.Count, releaseCount);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "index.json validation failed for {PluginId}", entry.Id);
            return new IndexValidationResult(false, $"Validation error: {ex.Message}", 0, 0);
        }
    }
}
