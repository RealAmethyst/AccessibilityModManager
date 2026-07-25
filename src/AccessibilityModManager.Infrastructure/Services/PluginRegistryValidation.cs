using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// What one pass over a registry document found. Every entry here is fatal to the WHOLE
/// registry on the manager side — there is no per-entry degradation, because the registry is
/// the signed trust anchor: it is accepted entirely or not at all.
/// </summary>
public sealed record RegistryValidationReport(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The authoring-side mirror of <see cref="PluginRegistryClient"/>'s acceptance rules, so the
/// AuthorTool can't sign and publish a registry every manager will then refuse.
/// <para>
/// This is the same class of failure as audit finding 8 (publishing a stale signature), and it
/// is worse than a single broken plugin: one unsafe id or one <c>http:</c> link anywhere in the
/// document takes down the entire catalog for every user, with a perfectly valid signature over
/// it. The registry is the one document with no per-item tolerance, so it gets checked before
/// it is signed rather than after it is live.
/// </para>
/// </summary>
public static class PluginRegistryValidation
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Validates registry JSON exactly as the manager will. Throws <see cref="JsonException"/>
    /// only when the document doesn't parse at all; everything else lands in the report.
    /// </summary>
    public static RegistryValidationReport Validate(string json)
    {
        var registry = JsonSerializer.Deserialize<PluginRegistry>(json, JsonOptions)
            ?? throw new InvalidOperationException("Registry deserialized to null");

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(registry.RegistryVersion))
            errors.Add("registryVersion is missing. Managers use it to refuse replayed older registries.");

        // Ids are compared ordinally by the manager but become folder names on Windows, so two
        // entries differing only in case are one plugin as far as a user's disk is concerned.
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in registry.Plugins)
        {
            Collect(errors, () => PathSafety.EnsureSafeId(plugin.Id, "registry plugin id"));
            Collect(errors, () => UrlValidator.RequireHttps(plugin.RepoIndexUrl, $"plugin '{plugin.Id}' repoIndexUrl"));
            if (plugin.Website != null)
                Collect(errors, () => UrlValidator.RequireHttps(plugin.Website, $"plugin '{plugin.Id}' website"));
            foreach (var (linkName, linkUri) in plugin.Links)
                Collect(errors, () => UrlValidator.RequireHttps(linkUri, $"plugin '{plugin.Id}' link '{linkName}'"));

            if (string.IsNullOrWhiteSpace(plugin.Id)) continue;
            if (seen.TryGetValue(plugin.Id, out var firstSpelling))
            {
                errors.Add(string.Equals(firstSpelling, plugin.Id, StringComparison.Ordinal)
                    ? $"Plugin id '{plugin.Id}' is listed twice."
                    : $"Plugin ids '{firstSpelling}' and '{plugin.Id}' differ only in capitalisation — " +
                      "they'd share one folder on a user's machine.");
            }
            else
            {
                seen[plugin.Id] = plugin.Id;
            }
        }

        return new RegistryValidationReport(errors);
    }

    private static void Collect(List<string> errors, Action check)
    {
        try { check(); }
        catch (Exception ex) { errors.Add(ex.Message); }
    }
}
