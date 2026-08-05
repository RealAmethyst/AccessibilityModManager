namespace AccessibilityModManager.Core.Models;

/// <summary>One source that was dropped when the config was read, and why — written to be read aloud.</summary>
public sealed record RejectedUserSource(string Describe, string Reason);

/// <summary>The usable sources from a config file, and what was dropped getting there.</summary>
public sealed record UserSourceAcceptance(
    IReadOnlyList<UserPluginSource> Accepted,
    IReadOnlyList<RejectedUserSource> Rejected);

/// <summary>
/// Decides which persisted sources may be used.
///
/// <para>This runs on LOAD, not only when a source is added. The config file is an ordinary file on
/// disk: anything that can write it can append a source, and that path never passes through the
/// screen that shows the risk notice. So the checks the add flow performs are re-performed here,
/// and a source that carries no record of the user accepting it is not used until they are asked.
/// A rule enforced only where the UI happens to call it is not a rule.</para>
///
/// <para>Nothing here is silent. A dropped source is reported so the user can be told which one and
/// why, rather than wondering where a developer went.</para>
/// </summary>
public static class UserPluginSourceValidation
{
    /// <summary>
    /// Bound on how many sources one config may carry. Not a security boundary — it is a brake on a
    /// config that has been scribbled on, so a corrupted or hostile file cannot turn one refresh
    /// into hundreds of outbound requests.
    /// </summary>
    public const int MaxSources = 64;

    public static UserSourceAcceptance Accept(IEnumerable<UserPluginSource>? sources)
    {
        var accepted = new List<UserPluginSource>();
        var rejected = new List<RejectedUserSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources ?? [])
        {
            if (source is null) continue;

            var describe = Describe(source);

            if (accepted.Count >= MaxSources)
            {
                rejected.Add(new RejectedUserSource(describe,
                    $"there are already {MaxSources} sources, which is as many as the manager will read"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(source.PluginId))
            {
                rejected.Add(new RejectedUserSource(describe, "it has no developer id"));
                continue;
            }

            // The id becomes a folder name for receipts and the cache, so the same containment rule
            // the rest of the app uses applies here — before it is ever combined into a path.
            if (!SafeId.IsValid(source.PluginId, out var idReason))
            {
                rejected.Add(new RejectedUserSource(describe, $"its developer id can't be used because {idReason}"));
                continue;
            }

            if (!Uri.TryCreate(source.IndexUrl, UriKind.Absolute, out var url))
            {
                rejected.Add(new RejectedUserSource(describe, "its address isn't a valid web address"));
                continue;
            }

            if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add(new RejectedUserSource(describe, "its address isn't secure — sources must use https"));
                continue;
            }

            // Acceptance is recorded on the source and bound to its identity, so an approval cannot
            // travel to a different developer or a different address by editing the row.
            //
            // Deliberately NOT claimed as proof of who wrote the line: this file belongs to the
            // user's own account, so anything able to forge a source can forge this too. What it
            // catches is a record edited in place, or one appended without the full shape — which
            // is what actually happens.
            // Two ways a source can stand: the user accepted the notice, or it was carried over
            // from the signed registry because their mods were already installed. Both are recorded
            // as facts about what happened; neither is inferred here.
            if (source.NoticeAcceptedUtc is null && source.MigratedFromRegistryUtc is null)
            {
                rejected.Add(new RejectedUserSource(describe, "you haven't confirmed it yet"));
                continue;
            }

            var expected = UserPluginSource.AcceptanceKey(source.PluginId, source.IndexUrl);
            if (!string.Equals(source.AcceptedFor, expected, StringComparison.Ordinal))
            {
                rejected.Add(new RejectedUserSource(describe,
                    "its developer id or address changed since it was set up, so it needs confirming again"));
                continue;
            }

            // The SAVED name, so a reserved one cannot sit in the settings file at all. This is not
            // the main defence — the name a row announces comes from the source's own catalog, which
            // it re-serves on every refresh, so the real check runs there. This just stops the
            // stored copy being one more place the wrong name can live.
            if (ReservedDeveloperNames.IsReserved(source.DisplayName))
            {
                rejected.Add(new RejectedUserSource(describe,
                    "it is saved under a developer name it isn't allowed to use"));
                continue;
            }

            // Two entries for one id in the same file is a scribbled-on config, not a normal state.
            // The first wins, matching the first-come rule everywhere else.
            if (!seen.Add(SafeId.Canonical(source.PluginId)))
            {
                rejected.Add(new RejectedUserSource(describe, "another source in your settings already uses that developer id"));
                continue;
            }

            accepted.Add(source);
        }

        return new UserSourceAcceptance(accepted, rejected);
    }

    private static string Describe(UserPluginSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.DisplayName)) return source.DisplayName!;
        if (!string.IsNullOrWhiteSpace(source.PluginId)) return source.PluginId;
        return string.IsNullOrWhiteSpace(source.IndexUrl) ? "an unnamed source" : source.IndexUrl;
    }
}
