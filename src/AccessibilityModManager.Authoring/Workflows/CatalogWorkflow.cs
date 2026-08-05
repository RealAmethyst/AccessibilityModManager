using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Authoring.Workflows;

public enum LifecycleSlot
{
    PreInstall,
    PostInstall,
    PostUninstall
}

public sealed class CatalogWorkflow
{
    private static readonly StringComparer IdentityComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PluginRepoIndex SetAuthor(PluginRepoIndex index, PluginAuthorInfo? author)
    {
        ArgumentNullException.ThrowIfNull(index);

        var clone = DeepClone(index);
        return new PluginRepoIndex
        {
            PluginId = clone.PluginId,
            RepoVersion = clone.RepoVersion,
            GeneratedAt = clone.GeneratedAt,
            Games = clone.Games,
            ReleasesByGameId = clone.ReleasesByGameId,
            Author = DeepCloneOrNull(author),
            DependencyPresets = clone.DependencyPresets
        };
    }

    public PluginRepoIndex CreateProject(string pluginId)
    {
        PathSafety.EnsureSafeId(pluginId, "Plugin id");

        return new PluginRepoIndex
        {
            PluginId = pluginId,
            RepoVersion = "1",
            GeneratedAt = DateTime.UtcNow,
            Games = [],
            ReleasesByGameId = new Dictionary<string, List<ModRelease>>(),
            Author = null,
            DependencyPresets = []
        };
    }

    public PluginRepoIndex AddGame(PluginRepoIndex index, GameDefinition game)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(game);

        ValidateGame(game);
        EnsureNoGameCollision(index, game.GameId, excludedGameIndex: null, excludedReleaseBucketKey: null, "add");

        var clone = DeepClone(index);
        clone.Games.Add(DeepClone(game));
        clone.ReleasesByGameId[game.GameId] = [];
        return clone;
    }

    public PluginRepoIndex UpdateGame(PluginRepoIndex index, string currentGameId, GameDefinition replacement) =>
        UpdateGame(index, currentGameId, replacement, rewriteReleaseGameIds: false);

    public PluginRepoIndex UpdateGame(
        PluginRepoIndex index,
        string currentGameId,
        GameDefinition replacement,
        bool rewriteReleaseGameIds)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentGameId);
        ArgumentNullException.ThrowIfNull(replacement);

        ValidateGame(replacement);

        var clone = DeepClone(index);
        var gameIndex = FindUniqueGameIndex(clone.Games, currentGameId);
        var existing = clone.Games[gameIndex];
        var sourceReleaseBucketKey = FindUniqueReleaseBucketKey(clone.ReleasesByGameId, currentGameId);

        EnsureNoGameCollision(
            clone,
            replacement.GameId,
            excludedGameIndex: gameIndex,
            excludedReleaseBucketKey: sourceReleaseBucketKey,
            "rename");

        var renamed = !string.Equals(existing.GameId, replacement.GameId, StringComparison.Ordinal);
        if (renamed)
        {
            var releases = sourceReleaseBucketKey is null
                ? null
                : clone.ReleasesByGameId[sourceReleaseBucketKey];

            if (releases is { Count: > 0 } && !rewriteReleaseGameIds)
            {
                throw new InvalidOperationException(
                    $"Can't rename game '{existing.GameId}' to '{replacement.GameId}' because its release bucket contains {releases.Count} release(s). " +
                    "Their embedded GameId values would no longer match the bucket key. Pass rewriteReleaseGameIds: true to rewrite every embedded release GameId explicitly.");
            }
        }

        clone.Games[gameIndex] = DeepClone(replacement);

        if (!renamed)
        {
            return clone;
        }

        if (sourceReleaseBucketKey is null)
        {
            clone.ReleasesByGameId[replacement.GameId] = [];
            return clone;
        }

        var sourceReleases = clone.ReleasesByGameId[sourceReleaseBucketKey];
        clone.ReleasesByGameId.Remove(sourceReleaseBucketKey);
        clone.ReleasesByGameId[replacement.GameId] = rewriteReleaseGameIds
            ? [.. sourceReleases.Select(release => RewriteReleaseGameId(release, replacement.GameId))]
            : sourceReleases;

        return clone;
    }

    public PluginRepoIndex RemoveGame(PluginRepoIndex index, string gameId)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var clone = DeepClone(index);
        var gameIndex = FindUniqueGameIndex(clone.Games, gameId);
        clone.Games.RemoveAt(gameIndex);

        var releaseBucketKey = FindUniqueReleaseBucketKey(clone.ReleasesByGameId, gameId);
        if (releaseBucketKey is not null)
        {
            clone.ReleasesByGameId.Remove(releaseBucketKey);
        }

        return clone;
    }

    public PluginRepoIndex UpsertDependency(PluginRepoIndex index, string gameId, Dependency dependency)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(dependency);

        ValidateDependency(dependency);

        var clone = DeepClone(index);
        var game = GetGame(clone.Games, gameId);
        var existingIndex = FindUniqueDependencyIndex(game.Dependencies, dependency.Id, game.GameId, throwWhenMissing: false);

        if (existingIndex >= 0)
        {
            game.Dependencies[existingIndex] = DeepClone(dependency);
        }
        else
        {
            game.Dependencies.Add(DeepClone(dependency));
        }

        return clone;
    }

    public PluginRepoIndex RemoveDependency(PluginRepoIndex index, string gameId, string dependencyId)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyId);

        var clone = DeepClone(index);
        var game = GetGame(clone.Games, gameId);
        var dependencyIndex = FindUniqueDependencyIndex(game.Dependencies, dependencyId, game.GameId, throwWhenMissing: true);
        game.Dependencies.RemoveAt(dependencyIndex);
        return clone;
    }

    public PluginRepoIndex SetLifecycleScript(PluginRepoIndex index, string gameId, LifecycleSlot slot, LifecycleScript script)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(script);

        ValidateLifecycleScript(script);

        var clone = DeepClone(index);
        var gameIndex = FindUniqueGameIndex(clone.Games, gameId);
        clone.Games[gameIndex] = CopyGameWithLifecycle(clone.Games[gameIndex], slot, DeepClone(script));
        return clone;
    }

    public PluginRepoIndex ClearLifecycleScript(PluginRepoIndex index, string gameId, LifecycleSlot slot)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var clone = DeepClone(index);
        var gameIndex = FindUniqueGameIndex(clone.Games, gameId);
        clone.Games[gameIndex] = CopyGameWithLifecycle(clone.Games[gameIndex], slot, null);
        return clone;
    }

    public PluginRepoIndex AddRelease(PluginRepoIndex index, string gameId, ModRelease release)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(release);

        var clone = DeepClone(index);
        var game = GetGame(clone.Games, gameId);
        ValidateRelease(clone, game, release);
        var releases = GetOrCreateReleaseBucket(clone, game.GameId);
        var existing = FindUniqueReleaseIndex(
            releases,
            release.Version,
            release.Channel,
            game.GameId,
            throwWhenMissing: false);
        if (existing >= 0)
            releases[existing] = DeepClone(release);
        else
            releases.Add(DeepClone(release));
        return clone;
    }

    public PluginRepoIndex EditRelease(
        PluginRepoIndex index,
        string gameId,
        string currentVersion,
        string currentChannel,
        ModRelease replacement)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentChannel);
        ArgumentNullException.ThrowIfNull(replacement);

        var clone = DeepClone(index);
        var game = GetGame(clone.Games, gameId);
        ValidateRelease(clone, game, replacement);
        var releases = GetOrCreateReleaseBucket(clone, game.GameId);
        var currentIndex = FindUniqueReleaseIndex(
            releases,
            currentVersion,
            currentChannel,
            game.GameId,
            throwWhenMissing: true);
        releases.RemoveAt(currentIndex);

        var collision = FindUniqueReleaseIndex(
            releases,
            replacement.Version,
            replacement.Channel,
            game.GameId,
            throwWhenMissing: false);
        if (collision >= 0)
            releases[collision] = DeepClone(replacement);
        else
            releases.Add(DeepClone(replacement));
        return clone;
    }

    public PluginRepoIndex RemoveRelease(
        PluginRepoIndex index,
        string gameId,
        string version,
        string channel)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        var clone = DeepClone(index);
        var game = GetGame(clone.Games, gameId);
        var releases = GetOrCreateReleaseBucket(clone, game.GameId);
        var releaseIndex = FindUniqueReleaseIndex(
            releases,
            version,
            channel,
            game.GameId,
            throwWhenMissing: true);
        releases.RemoveAt(releaseIndex);
        return clone;
    }

    private static GameDefinition GetGame(List<GameDefinition> games, string gameId) =>
        games[FindUniqueGameIndex(games, gameId)];

    private static int FindUniqueGameIndex(List<GameDefinition> games, string gameId)
    {
        var matches = games
            .Select((game, index) => new { game, index })
            .Where(x => IdentityComparer.Equals(x.game.GameId, gameId))
            .ToList();

        return matches.Count switch
        {
            0 => throw new InvalidOperationException($"Game '{gameId}' was not found."),
            > 1 => throw new InvalidOperationException(
                $"Multiple games already use id '{gameId}' when compared case-insensitively. Refusing to guess which game to change."),
            _ => matches[0].index
        };
    }

    private static string? FindUniqueReleaseBucketKey(Dictionary<string, List<ModRelease>> releasesByGameId, string gameId)
    {
        var matches = releasesByGameId.Keys
            .Where(key => IdentityComparer.Equals(key, gameId))
            .ToList();

        return matches.Count switch
        {
            0 => null,
            > 1 => throw new InvalidOperationException(
                $"Multiple release buckets already use game id '{gameId}' when compared case-insensitively. Refusing to guess which release bucket belongs to that game."),
            _ => matches[0]
        };
    }

    private static int FindUniqueDependencyIndex(
        List<Dependency> dependencies,
        string dependencyId,
        string gameId,
        bool throwWhenMissing)
    {
        var matches = dependencies
            .Select((dependency, index) => new { dependency, index })
            .Where(x => IdentityComparer.Equals(x.dependency.Id, dependencyId))
            .ToList();

        return matches.Count switch
        {
            0 when throwWhenMissing => throw new InvalidOperationException(
                $"Dependency '{dependencyId}' was not found for game '{gameId}'."),
            0 => -1,
            > 1 => throw new InvalidOperationException(
                $"Game '{gameId}' already contains multiple dependencies with id '{dependencyId}' that differ only by capitalisation. Refusing to guess which dependency to change."),
            _ => matches[0].index
        };
    }

    private static int FindUniqueReleaseIndex(
        List<ModRelease> releases,
        string version,
        string channel,
        string gameId,
        bool throwWhenMissing)
    {
        var matches = releases
            .Select((release, index) => new { release, index })
            .Where(candidate =>
                string.Equals(candidate.release.Version, version, StringComparison.Ordinal) &&
                string.Equals(candidate.release.Channel, channel, StringComparison.Ordinal))
            .ToList();

        return matches.Count switch
        {
            0 when throwWhenMissing => throw new InvalidOperationException(
                $"Release version '{version}' on channel '{channel}' was not found for game '{gameId}'."),
            0 => -1,
            > 1 => throw new InvalidOperationException(
                $"Game '{gameId}' contains multiple releases with version '{version}' and channel '{channel}'. Refusing to guess which release to change."),
            _ => matches[0].index
        };
    }

    private static List<ModRelease> GetOrCreateReleaseBucket(PluginRepoIndex index, string gameId)
    {
        var key = FindUniqueReleaseBucketKey(index.ReleasesByGameId, gameId);
        if (key is not null)
            return index.ReleasesByGameId[key];

        var releases = new List<ModRelease>();
        index.ReleasesByGameId[gameId] = releases;
        return releases;
    }

    private static void EnsureNoGameCollision(
        PluginRepoIndex index,
        string targetGameId,
        int? excludedGameIndex,
        string? excludedReleaseBucketKey,
        string operation)
    {
        PathSafety.EnsureSafeId(targetGameId, "Game id");

        foreach (var candidate in index.Games.Select((game, index) => new { game, index }))
        {
            if (excludedGameIndex.HasValue && candidate.index == excludedGameIndex.Value)
            {
                continue;
            }

            if (IdentityComparer.Equals(candidate.game.GameId, targetGameId))
            {
                throw new InvalidOperationException(
                    $"Can't {operation} game '{targetGameId}' because another game already uses that id (game ids are case-insensitive on Windows)."
                );
            }
        }

        foreach (var key in index.ReleasesByGameId.Keys)
        {
            if (string.Equals(key, excludedReleaseBucketKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (IdentityComparer.Equals(key, targetGameId))
            {
                throw new InvalidOperationException(
                    $"Can't {operation} game '{targetGameId}' because a release bucket already uses that id (game ids are case-insensitive on Windows)."
                );
            }
        }
    }

    private static void ValidateGame(GameDefinition game)
    {
        PathSafety.EnsureSafeId(game.GameId, "Game id");
        EnsureRequiredText(game.DisplayName, "Game display name");
        EnsureDependencyIdsUnique(game.Dependencies, game.GameId);

        foreach (var dependency in game.Dependencies)
        {
            ValidateDependency(dependency);
        }
    }

    private static void EnsureDependencyIdsUnique(IEnumerable<Dependency> dependencies, string gameId)
    {
        var seen = new HashSet<string>(IdentityComparer);
        foreach (var dependency in dependencies)
        {
            PathSafety.EnsureSafeId(dependency.Id, "Dependency id");
            if (!seen.Add(dependency.Id))
            {
                throw new InvalidOperationException(
                    $"Game '{gameId}' contains duplicate dependency id '{dependency.Id}'. Dependency ids are case-insensitive on Windows, so ids that differ only by capitalisation are ambiguous.");
            }
        }
    }

    private static void ValidateDependency(Dependency dependency)
    {
        PathSafety.EnsureSafeId(dependency.Id, "Dependency id");
        EnsureRequiredText(dependency.Type, $"Dependency '{dependency.Id}' type");
    }

    private static void ValidateLifecycleScript(LifecycleScript script)
    {
        EnsureRequiredText(script.Executable, "Lifecycle script executable");
        EnsureRequiredText(script.What, "Lifecycle script what");
        EnsureRequiredText(script.Why, "Lifecycle script why");
        EnsureRequiredText(script.Modifies, "Lifecycle script modifies");
    }

    private static void ValidateRelease(
        PluginRepoIndex index,
        GameDefinition game,
        ModRelease release)
    {
        EnsureRequiredText(release.Version, "Release version");
        EnsureRequiredText(release.Channel, "Release channel");
        EnsureRequiredText(release.Sha256, "Release SHA256");

        if (!string.Equals(release.PluginId, index.PluginId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Release pluginId '{release.PluginId}' doesn't match project pluginId '{index.PluginId}'.");
        }

        if (!string.Equals(release.GameId, game.GameId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Release gameId '{release.GameId}' doesn't match game '{game.GameId}'.");
        }

        if (release.Sha256.Length != 64 || release.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Release SHA256 must contain exactly 64 hexadecimal characters.");
    }

    private static void EnsureRequiredText(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{description} is required.");
        }
    }

    private static GameDefinition CopyGameWithLifecycle(
        GameDefinition game,
        LifecycleSlot slot,
        LifecycleScript? script) =>
        slot switch
        {
            LifecycleSlot.PreInstall => new GameDefinition
            {
                GameId = game.GameId,
                DisplayName = game.DisplayName,
                ModName = game.ModName,
                Description = game.Description,
                SteamAppId = game.SteamAppId,
                ExeName = game.ExeName,
                ProbeRules = game.ProbeRules,
                RegistryProbe = game.RegistryProbe,
                AsciiPathShim = game.AsciiPathShim,
                Dependencies = game.Dependencies,
                Tags = game.Tags,
                Languages = game.Languages,
                DefaultPreInstall = script,
                DefaultPostInstall = game.DefaultPostInstall,
                DefaultPostUninstall = game.DefaultPostUninstall
            },
            LifecycleSlot.PostInstall => new GameDefinition
            {
                GameId = game.GameId,
                DisplayName = game.DisplayName,
                ModName = game.ModName,
                Description = game.Description,
                SteamAppId = game.SteamAppId,
                ExeName = game.ExeName,
                ProbeRules = game.ProbeRules,
                RegistryProbe = game.RegistryProbe,
                AsciiPathShim = game.AsciiPathShim,
                Dependencies = game.Dependencies,
                Tags = game.Tags,
                Languages = game.Languages,
                DefaultPreInstall = game.DefaultPreInstall,
                DefaultPostInstall = script,
                DefaultPostUninstall = game.DefaultPostUninstall
            },
            LifecycleSlot.PostUninstall => new GameDefinition
            {
                GameId = game.GameId,
                DisplayName = game.DisplayName,
                ModName = game.ModName,
                Description = game.Description,
                SteamAppId = game.SteamAppId,
                ExeName = game.ExeName,
                ProbeRules = game.ProbeRules,
                RegistryProbe = game.RegistryProbe,
                AsciiPathShim = game.AsciiPathShim,
                Dependencies = game.Dependencies,
                Tags = game.Tags,
                Languages = game.Languages,
                DefaultPreInstall = game.DefaultPreInstall,
                DefaultPostInstall = game.DefaultPostInstall,
                DefaultPostUninstall = script
            },
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null)
        };

    private static ModRelease RewriteReleaseGameId(ModRelease release, string gameId) =>
        new()
        {
            GameId = gameId,
            PluginId = release.PluginId,
            Version = release.Version,
            Channel = release.Channel,
            PackageUrl = release.PackageUrl,
            Sha256 = release.Sha256,
            ChangelogUrl = release.ChangelogUrl,
            Notes = release.Notes,
            Compatibility = release.Compatibility,
            Patreon = release.Patreon
        };

    private static T DeepClone<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var json = JsonSerializer.Serialize(value, value.GetType(), JsonOptions);
        var clone = JsonSerializer.Deserialize<T>(json, JsonOptions);
        return clone ?? throw new InvalidOperationException($"Couldn't clone {typeof(T).Name}.");
    }

    private static T? DeepCloneOrNull<T>(T? value)
        where T : class =>
        value is null ? null : DeepClone(value);
}
