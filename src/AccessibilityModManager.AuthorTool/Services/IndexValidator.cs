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

            // The MANAGER's validation, run strictly: what the green tick approves must be
            // exactly what every user's manager will accept (audit finding 38 — the old check
            // here looked at the id and a release count and nothing else). For the author,
            // even a per-release problem the manager would merely hide is a failure to fix.
            AccessibilityModManager.Infrastructure.Services.IndexValidationReport report;
            try
            {
                report = AccessibilityModManager.Infrastructure.Services.PluginIndexValidation
                    .Validate(entry.Id, json);
            }
            catch (JsonException ex)
            {
                return new IndexValidationResult(false, $"index.json is not valid JSON: {ex.Message}", 0, 0);
            }
            catch (InvalidOperationException ex)
            {
                return new IndexValidationResult(false, ex.Message, 0, 0);
            }

            var problems = report.TrustErrors.Concat(report.UnobtainableReleases).ToList();
            if (problems.Count > 0)
            {
                const int shown = 5;
                var text = string.Join(Environment.NewLine, problems.Take(shown));
                if (problems.Count > shown)
                    text += Environment.NewLine + $"...and {problems.Count - shown} more.";
                return new IndexValidationResult(false, text,
                    report.Index.Games.Count, report.Index.ReleasesByGameId.Values.Sum(rs => rs.Count));
            }

            var releaseCount = report.Index.ReleasesByGameId.Values.Sum(rs => rs.Count);
            if (releaseCount == 0)
                return new IndexValidationResult(false,
                    "index.json has no releases yet. Wait until the author publishes at least one before accepting.",
                    report.Index.Games.Count, 0);

            return new IndexValidationResult(true, null, report.Index.Games.Count, releaseCount);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "index.json validation failed for {PluginId}", entry.Id);
            return new IndexValidationResult(false, $"Validation error: {ex.Message}", 0, 0);
        }
    }
}
