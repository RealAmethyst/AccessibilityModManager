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

    /// <summary>
    /// The exception behind <see cref="Errors"/>[0], when that error came from a check that threw
    /// one, so a caller that refuses on the first problem can surface the ORIGINAL failure rather
    /// than a generic wrapper around its message.
    ///
    /// <para>It exists because collecting these rules into one place would otherwise have quietly
    /// downgraded the manager's refusals: <c>UrlValidator.RequireHttps</c> throws
    /// <c>SecurityException</c>, and flattening every check to a string turned an explicit security
    /// signal into <c>InvalidOperationException</c>. Nothing consumes the type today, which is an
    /// argument for keeping it rather than for discarding it.</para>
    ///
    /// <para>Null when the first problem is one this validator states itself (a blank
    /// <c>registryVersion</c>, a duplicate id) rather than one a check threw.</para>
    /// </summary>
    public Exception? FirstFailure { get; init; }
}

/// <summary>
/// The registry acceptance rules, run by <see cref="PluginRegistryClient"/> when a manager accepts a
/// registry and by the AuthorTool before one is signed — so the AuthorTool cannot sign and publish a
/// registry every manager will then refuse.
/// <para>
/// This is the same class of failure as audit finding 8 (publishing a stale signature), and it
/// is worse than a single broken plugin: one unsafe id or one <c>http:</c> link anywhere in the
/// document takes down the entire catalog for every user, with a perfectly valid signature over
/// it. The registry is the one document with no per-item tolerance, so it gets checked before
/// it is signed rather than after it is live.
/// </para>
/// <para>
/// It was described as the AuthorTool's "mirror" of the manager's rules until 2026-07-31, and it was
/// exactly that — a second copy, which production never called. The copies had already drifted: the
/// blank-<c>registryVersion</c> and duplicate-id checks below existed only on the authoring side, so
/// the one place whose refusal actually protects a user was the weaker of the two. There is now one
/// implementation and both call it.
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

        return Validate(registry);
    }

    /// <summary>
    /// The same rules against an already-deserialized registry, so the manager's acceptance path
    /// does not parse the document a second time purely to reuse them.
    /// </summary>
    public static RegistryValidationReport Validate(PluginRegistry registry)
    {
        // Message and (where there was one) the exception that produced it, kept together so the
        // exception reported alongside the list is provably the one behind its FIRST entry.
        var errors = new List<(string Message, Exception? Source)>();

        if (string.IsNullOrWhiteSpace(registry.RegistryVersion))
            errors.Add(("registryVersion is missing. Managers use it to refuse replayed older registries.", null));

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
                errors.Add((string.Equals(firstSpelling, plugin.Id, StringComparison.Ordinal)
                    ? $"Plugin id '{plugin.Id}' is listed twice."
                    : $"Plugin ids '{firstSpelling}' and '{plugin.Id}' differ only in capitalisation — " +
                      "they'd share one folder on a user's machine.", null));
            }
            else
            {
                seen[plugin.Id] = plugin.Id;
            }
        }

        return new RegistryValidationReport([.. errors.Select(e => e.Message)])
        {
            FirstFailure = errors.Count > 0 ? errors[0].Source : null
        };
    }

    private static void Collect(List<(string Message, Exception? Source)> errors, Action check)
    {
        try { check(); }
        catch (Exception ex) { errors.Add((ex.Message, ex)); }
    }
}
