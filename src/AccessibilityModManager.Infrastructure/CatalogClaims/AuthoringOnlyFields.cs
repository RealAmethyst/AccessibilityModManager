using System.Text.Json.Nodes;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// The parts of an index that belong to the author and never to a reader.
///
/// One list, used in both directions, because the two uses have to agree exactly:
///
/// - **Publishing** strips them, so they never reach a signed claim. They would otherwise put
///   authoring state in front of every user, and re-sign a public claim whenever a template
///   changed — churn on a public claim is the activity signal this design keeps out of the
///   anonymous view.
/// - **Adopting a published index** keeps the local copy of them, and never takes the server's.
///   No claim will ever cover these fields, so no amount of verification downstream protects them,
///   and each one feeds something the author later signs: a preset fills in a dependency's download
///   URL and hash, a default lifecycle script fills in a release form, and a version-discovery rule
///   decides which upstream build a dependency points at. A server that edited any of them would be
///   choosing content for the author to put their signing key behind.
///
/// If the two lists ever disagreed, the gap would be silent: a field stripped from claims but
/// adopted from the wire is exactly the unprotected path.
/// </summary>
public static class AuthoringOnlyFields
{
    /// <summary>Templates the AuthorTool pre-fills a release form from. The manager only ever reads
    /// a release's own manifest.</summary>
    public static readonly string[] GameMembers =
        ["defaultPreInstall", "defaultPostInstall", "defaultPostUninstall"];

    /// <summary>Documented in the model as having no runtime effect — an authoring hint only.</summary>
    public static readonly string[] DependencyMembers = ["versionDiscovery"];

    /// <summary>Author-only members at the top level of an index.</summary>
    public static readonly string[] IndexMembers = ["dependencyPresets"];

    public static void StripFromGame(JsonObject game)
    {
        foreach (var member in GameMembers) game.Remove(member);

        if (game["dependencies"] is not JsonArray dependencies) return;
        foreach (var dependency in dependencies.OfType<JsonObject>())
        {
            foreach (var member in DependencyMembers) dependency.Remove(member);
        }
    }

    /// <summary>
    /// Rewrites <paramref name="adopted"/> so every author-only field comes from
    /// <paramref name="local"/> — matched by game id and dependency id — or is absent when the
    /// local copy has none. Anything the server put there is dropped either way.
    /// </summary>
    public static void RestoreFromLocal(JsonObject adopted, JsonObject local)
    {
        foreach (var member in IndexMembers)
        {
            adopted.Remove(member);
            if (local[member] is { } mine) adopted[member] = mine.DeepClone();
        }

        var localGames = Index(local["games"] as JsonArray, "gameId");
        if (adopted["games"] is not JsonArray adoptedGames) return;

        foreach (var game in adoptedGames.OfType<JsonObject>())
        {
            var mine = game["gameId"]?.GetValue<string>() is { } id && localGames.TryGetValue(id, out var match)
                ? match
                : null;

            Restore(game, mine, GameMembers);

            if (game["dependencies"] is not JsonArray dependencies) continue;
            var localDependencies = Index(mine?["dependencies"] as JsonArray, "id");

            foreach (var dependency in dependencies.OfType<JsonObject>())
            {
                var mineDependency =
                    dependency["id"]?.GetValue<string>() is { } depId &&
                    localDependencies.TryGetValue(depId, out var depMatch)
                        ? depMatch
                        : null;

                Restore(dependency, mineDependency, DependencyMembers);
            }
        }
    }

    private static void Restore(JsonObject target, JsonObject? source, string[] members)
    {
        foreach (var member in members)
        {
            target.Remove(member);
            if (source?[member] is { } mine) target[member] = mine.DeepClone();
        }
    }

    /// <summary>
    /// By id, keeping the first of any duplicates. A duplicated id is an authoring fault the
    /// validator reports separately; here it only has to not throw.
    /// </summary>
    private static Dictionary<string, JsonObject> Index(JsonArray? array, string idMember)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (array is null) return result;

        foreach (var item in array.OfType<JsonObject>())
        {
            if (item[idMember]?.GetValue<string>() is { } id) result.TryAdd(id, item);
        }

        return result;
    }
}
