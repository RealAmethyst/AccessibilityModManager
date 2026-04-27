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
        new("https://github.com/RealAmethyst/accessibility-mod-manager-registry/releases/latest/download/plugin-registry.json");

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

    /// <summary>
    /// Tag attached to the fenced code block carrying the proposed PluginEntry. The admin
    /// tool parses by this tag — having a stable identifier means the body can change wording
    /// without breaking parsing.
    /// </summary>
    public const string EntryFenceTag = "registry-entry";

    /// <summary>
    /// Builds a pre-filled "Add plugin" GitHub issue URL for the registry repo. The body
    /// includes a fenced JSON block (tagged <see cref="EntryFenceTag"/>) carrying the
    /// proposed <c>PluginEntry</c>. The admin tool extracts the block and uses it as the
    /// authoritative source for the registry edit.
    /// </summary>
    public static string BuildFeatureRequestUrl(
        string pluginId,
        string? authorDisplayName,
        string? githubRepo,
        string? bio)
    {
        var indexUrl = !string.IsNullOrWhiteSpace(githubRepo)
            ? $"https://raw.githubusercontent.com/{githubRepo}/main/index.json"
            : "https://example.com/your-index.json";

        // Build the proposed PluginEntry. Maintainer can adjust during review if needed —
        // this is a starting point, not a contract.
        var entry = new PluginEntry
        {
            Id = pluginId,
            Name = authorDisplayName ?? pluginId,
            Author = authorDisplayName ?? pluginId,
            Description = string.IsNullOrWhiteSpace(bio) ? "" : bio.Trim(),
            RepoIndexUrl = new Uri(indexUrl),
            IsBuiltIn = false,
            Links = [],
            Metadata = []
        };

        var entryJson = JsonSerializer.Serialize(entry, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var title = $"Add plugin: {pluginId}";
        var body = $@"## Plugin to add

- **Plugin ID:** `{pluginId}`
- **Author:** {authorDisplayName ?? "(your display name)"}
- **Index URL:** {indexUrl}

## About

{(string.IsNullOrWhiteSpace(bio) ? "(short description of what your mods do — copied from your author bio if you set one)" : bio)}

## Submitted from

Plugin Index Author tool. The index linked above passed local sanity checks.

## Registry entry (auto-generated, do not edit)

```json {EntryFenceTag}
{entryJson}
```
";

        var encodedTitle = Uri.EscapeDataString(title);
        var encodedBody = Uri.EscapeDataString(body);
        return $"https://github.com/{RegistryRepo}/issues/new?title={encodedTitle}&body={encodedBody}";
    }

    /// <summary>
    /// Pulls a <see cref="PluginEntry"/> out of an issue body's <c>registry-entry</c> fenced
    /// block. Returns null if the block is missing or doesn't deserialize. The admin tool uses
    /// this to drive its "Accept and merge" pipeline — issues without a parseable block fall
    /// back to the F2 manual-comment-and-close flow.
    /// </summary>
    public static PluginEntry? TryExtractEntryFromIssueBody(string? issueBody)
    {
        if (string.IsNullOrWhiteSpace(issueBody)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            issueBody,
            $@"```json\s+{EntryFenceTag}\s*\n(?<json>[\s\S]*?)```",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success) return null;

        try
        {
            var json = match.Groups["json"].Value.Trim();
            return JsonSerializer.Deserialize<PluginEntry>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
