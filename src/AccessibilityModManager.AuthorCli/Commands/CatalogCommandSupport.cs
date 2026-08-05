using System.CommandLine;
using System.Text.Json;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.AuthorCli.Commands;

internal static class CatalogCommandSupport
{
    public const string InputOptionName = "--input";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static bool GetJson(ParseResult parseResult) => GetBooleanOption(parseResult, RootCommands.JsonOptionName);

    public static bool GetDryRun(ParseResult parseResult) => GetBooleanOption(parseResult, RootCommands.DryRunOptionName);

    public static bool GetYes(ParseResult parseResult) => GetBooleanOption(parseResult, RootCommands.YesOptionName);

    public static string? GetProjectOption(ParseResult parseResult) => GetOptionValue<string?>(parseResult, RootCommands.ProjectOptionName);

    public static int Complete<T>(OutcomeWriter outcomeWriter, ParseResult parseResult, WorkflowResult<T> result)
    {
        outcomeWriter.Write(result, GetJson(parseResult));
        return (int)CliExitCode.Success;
    }

    public static WorkflowResult<object> Success(string status, object? value, params string[] messages) =>
        new(status, value, messages);

    public static WorkflowException Usage(params string[] messages) =>
        new(WorkflowErrorKind.Usage, "usage", messages);

    public static WorkflowException Validation(params string[] messages) =>
        new(WorkflowErrorKind.Validation, "validation", messages);

    public static WorkflowException Authentication(params string[] messages) =>
        new(WorkflowErrorKind.Authentication, "authentication", messages);

    public static WorkflowException Conflict(params string[] messages) =>
        new(WorkflowErrorKind.Conflict, "conflict", messages);

    public static async Task<T> ReadInputModelAsync<T>(
        JsonPayloadService jsonPayloads,
        ICliConsole console,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            return await jsonPayloads.ReadAsync<T>(source, console.In, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            throw Validation(ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw Validation(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Validation(ex.Message);
        }
        catch (IOException ex)
        {
            throw Validation(ex.Message);
        }
        catch (JsonException ex)
        {
            throw Validation(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw Validation(ex.Message);
        }
    }

    public static async Task<ResolvedAuthorProject> ResolveProjectAsync(
        AuthorProjectContext projectContext,
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        try
        {
            return await projectContext.ResolveAsync(
                GetProjectOption(parseResult),
                Environment.CurrentDirectory,
                cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            throw Validation(ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw Validation(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Validation(ex.Message);
        }
        catch (IOException ex)
        {
            throw Validation(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw Validation(ex.Message);
        }
    }

    public static async Task<WorkflowResult<object>> SaveMutationAsync(
        ParseResult parseResult,
        AuthorProjectContext projectContext,
        IndexFileService indexFiles,
        Func<PluginRepoIndex, PluginRepoIndex> mutate,
        string status,
        string successMessage,
        string dryRunMessage,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveProjectAsync(projectContext, parseResult, cancellationToken);

        if (GetDryRun(parseResult))
        {
            var candidate = ApplyMutation(mutate, resolved.Index);
            ValidateIndexCandidate(candidate);
            return CreateCatalogMutationResult(status, resolved.ProjectPath, candidate, dryRun: true, dryRunMessage);
        }

        await using var lease = await projectContext.AcquireWriteLeaseAsync(resolved.ProjectPath, cancellationToken);
        var current = LoadIndex(indexFiles, resolved.ProjectPath);
        var durableCandidate = StampGeneratedAt(ApplyMutation(mutate, current));
        ValidateIndexCandidate(durableCandidate);
        indexFiles.Save(resolved.ProjectPath, durableCandidate);

        return CreateCatalogMutationResult(status, resolved.ProjectPath, durableCandidate, dryRun: false, successMessage);
    }

    public static WorkflowResult<object> CreateCatalogMutationResult(
        string status,
        string projectPath,
        PluginRepoIndex candidate,
        bool dryRun,
        string message) =>
        new(
            status,
            new
            {
                projectPath,
                pluginId = candidate.PluginId,
                repoVersion = candidate.RepoVersion,
                generatedAt = candidate.GeneratedAt,
                gameCount = candidate.Games.Count,
                releaseBucketCount = candidate.ReleasesByGameId.Count,
                dryRun,
                candidate = dryRun ? candidate : null
            },
            new[] { message });

    public static WorkflowResult<object> CreateProjectSummaryResult(
        string status,
        string message,
        string projectPath,
        PluginRepoIndex index,
        bool dryRun,
        string? gitHubRepo = null,
        bool? exists = null) =>
        new(
            status,
            new
            {
                projectPath,
                pluginId = index.PluginId,
                repoVersion = index.RepoVersion,
                generatedAt = index.GeneratedAt,
                gameCount = index.Games.Count,
                releaseBucketCount = index.ReleasesByGameId.Count,
                gitHubRepo,
                exists,
                dryRun,
                candidate = dryRun ? index : null
            },
            new[] { message });

    public static void ValidateIndexCandidate(PluginRepoIndex candidate)
    {
        try
        {
            var json = JsonSerializer.Serialize(candidate, JsonOptions);
            var report = PluginIndexValidation.Validate(candidate.PluginId, json);

            if (report.PublishBlockers.Count == 0)
            {
                return;
            }

            var messages = new List<string>
            {
                $"The candidate index for '{candidate.PluginId}' failed validation."
            };
            messages.AddRange(report.PublishBlockers);

            throw new WorkflowException(
                WorkflowErrorKind.Validation,
                "validation",
                messages);
        }
        catch (WorkflowException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw Validation(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw Validation(ex.Message);
        }
    }

    public static void RejectMixedInput(string? inputSource, params bool[] fieldFlagsPresent)
    {
        if (string.IsNullOrWhiteSpace(inputSource))
        {
            return;
        }

        if (fieldFlagsPresent.Any(x => x))
        {
            throw Usage(
                "Don't mix --input with individual field flags. Supply either a complete camelCase model with --input or use field flags alone.");
        }
    }

    public static void EnsureYes(ParseResult parseResult, string explanation)
    {
        if (!GetYes(parseResult))
        {
            throw Usage(explanation);
        }
    }

    public static LifecycleSlot ParseSlot(string token) =>
        token switch
        {
            "pre-install" => LifecycleSlot.PreInstall,
            "post-install" => LifecycleSlot.PostInstall,
            "post-uninstall" => LifecycleSlot.PostUninstall,
            _ => throw Usage(
                $"Unknown lifecycle slot '{token}'. Use pre-install, post-install, or post-uninstall.")
        };

    public static GameDefinition FindGame(PluginRepoIndex index, string gameId)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var matches = index.Games
            .Where(game => string.Equals(game.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => throw Validation($"Game '{gameId}' was not found."),
            1 => matches[0],
            _ => throw Conflict(
                $"Multiple games already use id '{gameId}' when compared case-insensitively. Refusing to guess which game to read.")
        };
    }

    public static Dependency FindDependency(GameDefinition game, string dependencyId)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyId);

        var matches = game.Dependencies
            .Where(dependency => string.Equals(dependency.Id, dependencyId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => throw Validation($"Dependency '{dependencyId}' was not found for game '{game.GameId}'."),
            1 => matches[0],
            _ => throw Conflict(
                $"Game '{game.GameId}' already contains multiple dependencies with id '{dependencyId}' that differ only by capitalisation. Refusing to guess which dependency to read.")
        };
    }

    public static List<ModRelease> GetReleasesForGame(PluginRepoIndex index, string gameId)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var matchingKeys = index.ReleasesByGameId.Keys
            .Where(key => string.Equals(key, gameId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matchingKeys.Count switch
        {
            0 => new List<ModRelease>(),
            1 => index.ReleasesByGameId[matchingKeys[0]],
            _ => throw Conflict(
                $"Multiple release buckets already use game id '{gameId}' when compared case-insensitively. Refusing to guess which release bucket belongs to that game.")
        };
    }

    public static void EnsureGitAvailable(bool available, string toolName)
    {
        if (!available)
        {
            throw Validation($"{toolName} is not installed or not available on PATH.");
        }
    }

    public static void EnsureGitHubAuthenticated(bool authenticated)
    {
        if (!authenticated)
        {
            throw Authentication(
                "You're not signed in to the GitHub CLI yet. Run 'gh auth login' and try again.");
        }
    }

    public static string NormalizeGitHubRepo(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Validation("A GitHub repository name is required.");
        }

        var trimmed = value.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw Validation($"Only github.com repositories are supported here, not '{uri.Host}'.");
            }

            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                throw Validation($"'{value}' is not a GitHub repository path.");
            }

            return NormalizeGitHubRepo($"{segments[0]}/{segments[1]}");
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw Validation(
                $"'{value}' is not a valid GitHub repository. Use 'owner/name' or a GitHub HTTPS URL.");
        }

        var owner = parts[0].Trim();
        var name = parts[1].Trim();
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
        {
            throw Validation(
                $"'{value}' is not a valid GitHub repository. Use 'owner/name' or a GitHub HTTPS URL.");
        }

        return $"{owner}/{name}";
    }

    public static string DefaultProjectDisplayName(string projectPath) =>
        Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public static WorkflowException MapCatalogFailure(InvalidOperationException ex)
    {
        var kind = IsConflictMessage(ex.Message)
            ? WorkflowErrorKind.Conflict
            : WorkflowErrorKind.Validation;

        return new WorkflowException(
            kind,
            kind == WorkflowErrorKind.Conflict ? "conflict" : "validation",
            new[] { ex.Message },
            innerException: ex);
    }

    public static bool IsSpecified<T>(ParseResult parseResult, System.CommandLine.Option<T> option) =>
        parseResult.GetResult(option) is not null;

    public static PluginRepoIndex StampGeneratedAt(PluginRepoIndex candidate) =>
        new()
        {
            PluginId = candidate.PluginId,
            RepoVersion = candidate.RepoVersion,
            GeneratedAt = DateTime.UtcNow,
            Games = Clone(candidate.Games) ?? new List<GameDefinition>(),
            ReleasesByGameId = Clone(candidate.ReleasesByGameId) ?? new Dictionary<string, List<ModRelease>>(),
            Author = Clone(candidate.Author),
            DependencyPresets = Clone(candidate.DependencyPresets) ?? new List<DependencyPreset>()
        };

    private static bool GetBooleanOption(ParseResult parseResult, string optionName)
    {
        try
        {
            return parseResult.GetValue<bool>(optionName);
        }
        catch
        {
            return false;
        }
    }

    private static T? GetOptionValue<T>(ParseResult parseResult, string optionName)
    {
        try
        {
            return parseResult.GetValue<T>(optionName);
        }
        catch
        {
            return default;
        }
    }

    private static PluginRepoIndex LoadIndex(IndexFileService indexFiles, string projectPath)
    {
        try
        {
            return indexFiles.Load(projectPath);
        }
        catch (FileNotFoundException ex)
        {
            throw Validation(ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw Validation(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw Validation(ex.Message);
        }
    }

    private static PluginRepoIndex ApplyMutation(
        Func<PluginRepoIndex, PluginRepoIndex> mutate,
        PluginRepoIndex current)
    {
        try
        {
            return mutate(current);
        }
        catch (WorkflowException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            throw MapCatalogFailure(ex);
        }
    }

    private static T? Clone<T>(T? value)
    {
        if (value is null)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(
            JsonSerializer.Serialize(value, value.GetType(), JsonOptions),
            JsonOptions);
    }

    private static bool IsConflictMessage(string message) =>
        message.Contains("already uses", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("already contains", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("release bucket", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("duplicate dependency id", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("multiple games", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("multiple release buckets", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("capitalisation", StringComparison.OrdinalIgnoreCase);
}
