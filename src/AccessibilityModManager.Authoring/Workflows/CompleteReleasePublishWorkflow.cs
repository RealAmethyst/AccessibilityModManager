using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Authoring.Workflows;

public sealed record CompleteReleasePublishRequest(
    ReleasePublishRequest Release,
    PublishDestination IndexDestination,
    string IndexCommitMessage,
    bool DryRun);

public sealed record CompleteReleasePublishPreview(
    ReleasePublishPreview Release,
    IndexPublishPreview Index,
    PluginRepoIndex Candidate);

public sealed record CompleteReleasePublishResult(
    ModRelease Release,
    string PublishedIndexSha256,
    string IndexDestination,
    IReadOnlyList<string> CompletedPhases);

public interface ICompleteReleasePublishWorkflow
{
    Task<WorkflowResult<CompleteReleasePublishPreview>> PreviewAsync(
        CompleteReleasePublishRequest request,
        CancellationToken ct);

    Task<WorkflowResult<CompleteReleasePublishResult>> PublishAsync(
        CompleteReleasePublishRequest request,
        bool confirmed,
        CancellationToken ct);
}

/// <summary>
/// Composes the normal release operation without hiding partial completion. Remote package
/// publication intentionally precedes the local catalog write, matching the AuthorTool: a catalog
/// must never point at bytes that are not yet anonymously downloadable. Every returned phase is
/// therefore a fact that has already happened, not a plan or an optimistic status message.
/// </summary>
public sealed class CompleteReleasePublishWorkflow(
    AuthorProjectContext projects,
    CatalogWorkflow catalog,
    IReleaseWorkflow releases,
    IIndexWorkflow indexes) : ICompleteReleasePublishWorkflow
{
    private static readonly string[] ExactPhaseOrder =
    [
        "projectLocked",
        "catalogReconciled",
        "packageValidated",
        "assetUploaded",
        "releaseRecorded",
        "indexValidated",
        "indexSaved",
        "indexPublished",
        "liveVerified"
    ];

    public async Task<WorkflowResult<CompleteReleasePublishPreview>> PreviewAsync(
        CompleteReleasePublishRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var normalized = Normalize(request);
            var reconciled = await indexes.ReconcileAsync(
                normalized.Release.ProjectPath,
                dryRun: true,
                ct);
            if (reconciled.ErrorKind != WorkflowErrorKind.None || reconciled.Value is null)
                return ForwardFailure<PluginRepoIndex, CompleteReleasePublishPreview>(reconciled);

            var releasePreview = await releases.PreviewAsync(normalized.Release, ct);
            if (releasePreview.ErrorKind != WorkflowErrorKind.None || releasePreview.Value is null)
                return ForwardFailure<ReleasePublishPreview, CompleteReleasePublishPreview>(releasePreview);

            var release = BuildRelease(normalized.Release, releasePreview.Value);
            var candidate = StampGeneratedAt(catalog.AddRelease(
                reconciled.Value,
                normalized.Release.GameId,
                release));

            var validation = indexes.Validate(candidate);
            if (validation.PublishBlockers.Count > 0)
            {
                return new WorkflowResult<CompleteReleasePublishPreview>(
                    "indexValidationFailed",
                    null,
                    new[] { "The complete catalog would not be publishable." }
                        .Concat(validation.PublishBlockers)
                        .ToArray(),
                    WorkflowErrorKind.Validation);
            }

            var indexPreview = await indexes.PreviewPublishAsync(
                new IndexPublishRequest(
                    normalized.Release.ProjectPath,
                    candidate,
                    normalized.IndexDestination,
                    normalized.IndexCommitMessage,
                    DryRun: true),
                ct);
            if (indexPreview.ErrorKind != WorkflowErrorKind.None || indexPreview.Value is null)
                return ForwardFailure<IndexPublishPreview, CompleteReleasePublishPreview>(indexPreview);

            var preview = new CompleteReleasePublishPreview(
                releasePreview.Value,
                indexPreview.Value,
                candidate);
            return new WorkflowResult<CompleteReleasePublishPreview>(
                "completeReleasePreviewed",
                preview,
                new[]
                {
                    $"The package would publish to {releasePreview.Value.Repository} {releasePreview.Value.Tag} as {releasePreview.Value.AssetFileName}.",
                    $"The updated catalog would publish to {indexPreview.Value.DestinationDescription}."
                });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Failure<CompleteReleasePublishPreview>(
                WorkflowErrorKind.Validation,
                "completeReleasePreviewFailed",
                ex.Message,
                []);
        }
    }

    public async Task<WorkflowResult<CompleteReleasePublishResult>> PublishAsync(
        CompleteReleasePublishRequest request,
        bool confirmed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var phases = new List<string>();

        if (request.DryRun)
        {
            var preview = await PreviewAsync(request, ct);
            if (preview.ErrorKind != WorkflowErrorKind.None || preview.Value is null)
                return ForwardFailure<CompleteReleasePublishPreview, CompleteReleasePublishResult>(preview);

            var release = BuildRelease(request.Release, preview.Value.Release);
            return new WorkflowResult<CompleteReleasePublishResult>(
                "completeReleaseDryRun",
                new CompleteReleasePublishResult(
                    release,
                    string.Empty,
                    preview.Value.Index.DestinationDescription,
                    []),
                new[] { "Dry run completed; no lock, file write, commit, upload, or configuration change occurred." });
        }

        if (!confirmed)
        {
            return Failure<CompleteReleasePublishResult>(
                WorkflowErrorKind.Conflict,
                "confirmationRequired",
                "Complete release publication requires confirmation after reviewing both the package and catalog destinations.",
                phases);
        }

        try
        {
            var normalized = Normalize(request);
            await using var lease = await projects.AcquireWriteLeaseAsync(normalized.Release.ProjectPath, ct);
            phases.Add("projectLocked");

            var reconciled = await indexes.ReconcileAsync(normalized.Release.ProjectPath, dryRun: false, ct);
            if (reconciled.ErrorKind != WorkflowErrorKind.None || reconciled.Value is null)
                return ForwardFailure<PluginRepoIndex, CompleteReleasePublishResult>(reconciled, phases);
            phases.Add("catalogReconciled");

            var preparedResult = await releases.PrepareAsync(normalized.Release, ct);
            if (preparedResult.ErrorKind != WorkflowErrorKind.None || preparedResult.Value is null)
                return ForwardFailure<PreparedRelease, CompleteReleasePublishResult>(preparedResult, phases);

            await using var prepared = preparedResult.Value;
            phases.Add("packageValidated");

            var uploaded = await releases.PublishAsync(prepared, normalized.Release, confirmed: true, ct);
            if (uploaded.ErrorKind != WorkflowErrorKind.None || uploaded.Value is null)
            {
                AddRemoteAssetPhaseIfCompleted(phases, uploaded.CompletedPhases);
                return ForwardFailure<ReleasePublishResult, CompleteReleasePublishResult>(uploaded, phases);
            }
            phases.Add("assetUploaded");

            var candidate = StampGeneratedAt(catalog.AddRelease(
                reconciled.Value,
                normalized.Release.GameId,
                uploaded.Value.Release));
            phases.Add("releaseRecorded");

            var validation = indexes.Validate(candidate);
            if (validation.PublishBlockers.Count > 0)
            {
                return new WorkflowResult<CompleteReleasePublishResult>(
                    "indexValidationFailed",
                    null,
                    new[] { "The package is live, but the updated catalog is invalid." }
                        .Concat(validation.PublishBlockers)
                        .ToArray(),
                    WorkflowErrorKind.Validation,
                    phases.ToArray());
            }
            phases.Add("indexValidated");

            var saved = await indexes.SaveAsync(
                normalized.Release.ProjectPath,
                candidate,
                dryRun: false,
                ct);
            if (saved.ErrorKind != WorkflowErrorKind.None)
                return ForwardFailure<string, CompleteReleasePublishResult>(saved, phases);
            phases.Add("indexSaved");

            var published = await indexes.PublishAsync(
                new IndexPublishRequest(
                    normalized.Release.ProjectPath,
                    candidate,
                    normalized.IndexDestination,
                    normalized.IndexCommitMessage,
                    DryRun: false),
                confirmed: true,
                ct);

            if (published.ErrorKind != WorkflowErrorKind.None || published.Value is null)
            {
                AddIndexPhases(phases, published.CompletedPhases);
                return ForwardFailure<IndexPublishResult, CompleteReleasePublishResult>(published, phases);
            }

            AddIndexPhases(phases, published.Value.CompletedPhases);
            EnsureExactOrder(phases);

            var result = new CompleteReleasePublishResult(
                uploaded.Value.Release,
                published.Value.PublishedSha256,
                published.Value.DestinationDescription,
                phases.ToArray());
            return new WorkflowResult<CompleteReleasePublishResult>(
                "completeReleasePublished",
                result,
                new[]
                {
                    $"Published release {uploaded.Value.Release.Version} ({uploaded.Value.Release.Channel}) and verified the live catalog."
                },
                completedPhases: result.CompletedPhases);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<CompleteReleasePublishResult>(
                ex is IOException or UnauthorizedAccessException
                    ? WorkflowErrorKind.Conflict
                    : WorkflowErrorKind.Validation,
                "completeReleasePublishFailed",
                ex.Message,
                phases);
        }
    }

    private static CompleteReleasePublishRequest Normalize(CompleteReleasePublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Release);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Release.ProjectPath);
        if (request.IndexDestination == PublishDestination.Unset)
            throw new InvalidOperationException("No catalog publishing destination is selected.");

        return request with
        {
            Release = request.Release with
            {
                ProjectPath = Path.GetFullPath(request.Release.ProjectPath)
            },
            IndexCommitMessage = string.IsNullOrWhiteSpace(request.IndexCommitMessage)
                ? "Update accessibility mod index"
                : request.IndexCommitMessage.Trim()
        };
    }

    private static ModRelease BuildRelease(
        ReleasePublishRequest request,
        ReleasePublishPreview preview) =>
        new()
        {
            GameId = request.GameId.Trim(),
            PluginId = request.PluginId.Trim(),
            Version = request.Version.Trim(),
            Channel = request.Channel.Trim(),
            PackageUrl = GitHubService.BuildAssetUrl(
                preview.Repository,
                preview.Tag,
                preview.AssetFileName),
            Sha256 = preview.Sha256,
            ChangelogUrl = NullIfBlank(request.ChangelogUrl),
            Notes = NullIfBlank(request.Notes),
            Patreon = null
        };

    private static PluginRepoIndex StampGeneratedAt(PluginRepoIndex candidate) =>
        new()
        {
            PluginId = candidate.PluginId,
            RepoVersion = candidate.RepoVersion,
            GeneratedAt = DateTime.UtcNow,
            Games = candidate.Games,
            ReleasesByGameId = candidate.ReleasesByGameId,
            Author = candidate.Author,
            DependencyPresets = candidate.DependencyPresets
        };

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddRemoteAssetPhaseIfCompleted(
        ICollection<string> phases,
        IReadOnlyList<string>? releasePhases)
    {
        if (releasePhases?.Any(phase => phase is
                "githubReleaseCreated" or
                "githubAssetUploaded" or
                "githubAssetAlreadyMatched") == true)
        {
            phases.Add("assetUploaded");
        }
    }

    private static void AddIndexPhases(
        ICollection<string> phases,
        IReadOnlyList<string>? indexPhases)
    {
        if (indexPhases?.Contains("indexPublished", StringComparer.Ordinal) == true)
            phases.Add("indexPublished");
        if (indexPhases?.Contains("liveVerified", StringComparer.Ordinal) == true)
            phases.Add("liveVerified");
    }

    private static void EnsureExactOrder(IReadOnlyList<string> phases)
    {
        if (!phases.SequenceEqual(ExactPhaseOrder, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The complete release transaction reported phases out of order; refusing to call it complete.");
        }
    }

    private static WorkflowResult<TOut> ForwardFailure<TIn, TOut>(
        WorkflowResult<TIn> result,
        IReadOnlyList<string>? completedPhases = null) =>
        new(
            result.Status,
            default,
            result.Messages,
            result.ErrorKind == WorkflowErrorKind.None ? WorkflowErrorKind.Conflict : result.ErrorKind,
            completedPhases ?? result.CompletedPhases);

    private static WorkflowResult<T> Failure<T>(
        WorkflowErrorKind kind,
        string status,
        string message,
        IReadOnlyList<string> phases) =>
        new(status, default, new[] { message }, kind, phases.ToArray());
}
