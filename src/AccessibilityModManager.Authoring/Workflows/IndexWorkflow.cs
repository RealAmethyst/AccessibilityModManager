using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.Authoring.Workflows;

public sealed record IndexPublishRequest(
    string ProjectPath,
    PluginRepoIndex Candidate,
    PublishDestination Destination,
    string CommitMessage,
    bool DryRun,
    bool PreserveExistingReleaseIdentities = false);

public sealed record IndexPublishPreview(
    string PluginId,
    PublishDestination Destination,
    string DestinationDescription,
    string CommitMessage,
    IReadOnlyList<string> CatalogChanges);

public sealed record IndexPublishResult(
    string PluginId,
    string PublishedSha256,
    string DestinationDescription,
    IReadOnlyList<string> CompletedPhases);

public interface IIndexWorkflow
{
    IndexValidationReport Validate(PluginRepoIndex candidate);
    Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(string projectPath, CancellationToken ct);
    Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(
        string projectPath,
        bool dryRun,
        CancellationToken ct);
    Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(
        string projectPath,
        bool dryRun,
        bool confirmAdoption,
        CancellationToken ct) =>
        ReconcileAsync(projectPath, dryRun, ct);
    Task<WorkflowResult<string>> SaveAsync(
        string projectPath,
        PluginRepoIndex candidate,
        bool dryRun,
        CancellationToken ct);
    Task<WorkflowResult<IndexPublishPreview>> PreviewPublishAsync(
        IndexPublishRequest request,
        CancellationToken ct);
    Task<WorkflowResult<IndexPublishResult>> PublishAsync(
        IndexPublishRequest request,
        bool confirmed,
        CancellationToken ct);

}

public sealed class IndexWorkflow : IIndexWorkflow
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private readonly GitHubIndexPublisher _gitHubPublisher;
    private readonly UnsignedPublishGate _unsignedGate;
    private readonly RegistryMembershipChecker _registryChecker;
    private readonly AuthorConfigService _config;
    private readonly IndexFileService _indexFiles;
    private readonly IGitHubService _gitHub;
    private readonly ILogger _logger;
    private readonly PluginDirectoryClient? _directory;

    public IndexWorkflow(
        GitHubIndexPublisher gitHubPublisher,
        UnsignedPublishGate unsignedGate,
        RegistryMembershipChecker registryChecker,
        AuthorConfigService config,
        IndexFileService indexFiles,
        IGitHubService gitHub,
        ILogger logger,
        PluginDirectoryClient? directory = null)
    {
        _gitHubPublisher = gitHubPublisher ?? throw new ArgumentNullException(nameof(gitHubPublisher));
        _unsignedGate = unsignedGate ?? throw new ArgumentNullException(nameof(unsignedGate));
        _registryChecker = registryChecker ?? throw new ArgumentNullException(nameof(registryChecker));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _indexFiles = indexFiles ?? throw new ArgumentNullException(nameof(indexFiles));
        _gitHub = gitHub ?? throw new ArgumentNullException(nameof(gitHub));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _directory = directory;
    }

    public IndexValidationReport Validate(PluginRepoIndex candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var report = PluginIndexValidation.Validate(candidate.PluginId, SerializeText(candidate, trailingNewline: false));
        return report with
        {
            AuthoringProblems = report.AuthoringProblems.Concat(
                candidate.Games.SelectMany(game => DependencyAuthoringValidation.Errors(game.Dependencies))).ToArray()
        };
    }

    public Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(
        string projectPath,
        CancellationToken ct) =>
        ReconcileAsync(projectPath, dryRun: false, confirmAdoption: false, ct);

    public Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(
        string projectPath,
        bool dryRun,
        CancellationToken ct) =>
        ReconcileAsync(projectPath, dryRun, confirmAdoption: false, ct);

    public async Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(
        string projectPath,
        bool dryRun,
        bool confirmAdoption,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var fullProjectPath = Path.GetFullPath(projectPath);
        var indexPath = IndexFileService.GetIndexPath(fullProjectPath);
        if (!File.Exists(indexPath))
            return Failure<PluginRepoIndex>(WorkflowErrorKind.Validation, "indexMissing", $"index.json not found at {indexPath}");

        var localBytes = await File.ReadAllBytesAsync(indexPath, ct);
        PluginRepoIndex local;
        try
        {
            local = _indexFiles.Load(fullProjectPath);
        }
        catch (Exception ex)
        {
            return Failure<PluginRepoIndex>(WorkflowErrorKind.Validation, "indexLoadFailed", ex.Message);
        }

        var authorization = await _unsignedGate.AuthorizeAsync(
            new RegistryVerifiedSource(_registryChecker), local.PluginId, ct);
        if (!authorization.Allowed)
            return Failure<PluginRepoIndex>(WorkflowErrorKind.Authentication, "catalogReconcileBlocked", authorization.Message);
        return await ReconcileUnsignedAsync(fullProjectPath, local, localBytes, dryRun, confirmAdoption, ct);
    }

    public async Task<WorkflowResult<string>> SaveAsync(
        string projectPath,
        PluginRepoIndex candidate,
        bool dryRun,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(candidate);
        ct.ThrowIfCancellationRequested();

        var report = Validate(candidate);
        if (report.PublishBlockers.Count > 0)
        {
            return new WorkflowResult<string>(
                "indexValidationFailed",
                null,
                new[] { "The index cannot be saved for publication." }.Concat(report.PublishBlockers).ToArray(),
                WorkflowErrorKind.Validation);
        }

        var bytes = SerializeBytes(candidate);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!dryRun)
        {
            var indexPath = IndexFileService.GetIndexPath(Path.GetFullPath(projectPath));
            DurableFile.Write(indexPath, bytes);
        }

        return Success(
            dryRun ? "indexSavePreviewed" : "indexSaved",
            sha,
            dryRun ? $"index.json is valid and would be saved with SHA256 {sha}." : $"Saved index.json with SHA256 {sha}.");
    }

    public async Task<WorkflowResult<IndexPublishPreview>> PreviewPublishAsync(
        IndexPublishRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = Validate(request.Candidate);
        if (validation.PublishBlockers.Count > 0)
        {
            return new WorkflowResult<IndexPublishPreview>(
                "indexValidationFailed",
                null,
                new[] { "The index cannot be published." }.Concat(validation.PublishBlockers).ToArray(),
                WorkflowErrorKind.Validation);
        }

        if (request.Destination != PublishDestination.GitHub)
        {
            return Failure<IndexPublishPreview>(
                WorkflowErrorKind.Validation,
                "publishDestinationMissing",
                "The author CLI publishes catalogs to GitHub only.");
        }

        var projectPath = Path.GetFullPath(request.ProjectPath);
        var changes = DescribeChanges(projectPath, request.Candidate);


        var (target, error) = await _gitHubPublisher.ResolveTargetAsync(projectPath, ct);
        if (target is null)
            return Failure<IndexPublishPreview>(WorkflowErrorKind.Validation, "githubTargetInvalid", error ?? "Couldn't resolve the GitHub target.");

        var registry = new RegistryVerifiedSource(_registryChecker);
        var authorized = await _unsignedGate.AuthorizeAsync(registry, request.Candidate.PluginId, ct);
        if (!authorized.Allowed)
            return Failure<IndexPublishPreview>(WorkflowErrorKind.Authentication, "unsignedPublishRefused", authorized.Message);

        var privateState = await _gitHub.IsRepoPrivateAsync($"{target.Owner}/{target.Repo}", ct);
        if (privateState is true)
            return Failure<IndexPublishPreview>(WorkflowErrorKind.Validation, "privateRepositoryRefused", $"{target.Describe} is private, so managers cannot read its raw index anonymously.");
        if (privateState is null)
            return Failure<IndexPublishPreview>(WorkflowErrorKind.Conflict, "repositoryVisibilityUnknown", $"Couldn't verify whether {target.Describe} is public.");

        if (authorized.RegisteredIndexUrl is { } registered &&
            !string.Equals(registered.TrimEnd('/'), target.BranchRawUrl, StringComparison.Ordinal))
        {
            return Failure<IndexPublishPreview>(
                WorkflowErrorKind.Conflict,
                "registeredIndexUrlMismatch",
                $"The registry tells managers to read '{registered}', but this project would publish '{target.BranchRawUrl}'.");
        }

        return Success(
            "indexPublishPreviewed",
            new IndexPublishPreview(
                request.Candidate.PluginId,
                request.Destination,
                target.Describe,
                NormalizeCommitMessage(request.CommitMessage),
                changes),
            $"Index publication is valid and would push to {target.Describe}.");
    }

    public async Task<WorkflowResult<IndexPublishResult>> PublishAsync(
        IndexPublishRequest request,
        bool confirmed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var previewResult = await PreviewPublishAsync(request, ct);
        if (previewResult.ErrorKind != WorkflowErrorKind.None || previewResult.Value is null)
        {
            return new WorkflowResult<IndexPublishResult>(
                previewResult.Status,
                null,
                previewResult.Messages,
                previewResult.ErrorKind,
                previewResult.CompletedPhases);
        }

        if (request.DryRun)
        {
            return Success(
                "indexPublishDryRun",
                new IndexPublishResult(
                    request.Candidate.PluginId,
                    Convert.ToHexStringLower(SHA256.HashData(SerializeBytes(request.Candidate))),
                    previewResult.Value.DestinationDescription,
                    Array.Empty<string>()),
                "Dry run completed; nothing was committed, uploaded, or changed.");
        }

        if (!confirmed)
        {
            return Failure<IndexPublishResult>(
                WorkflowErrorKind.Conflict,
                "confirmationRequired",
                $"Publishing requires confirmation of this exact destination: {previewResult.Value.DestinationDescription}.");
        }

        return await PublishGitHubAsync(request, previewResult.Value, ct);
    }

    private async Task<WorkflowResult<IndexPublishResult>> PublishGitHubAsync(
        IndexPublishRequest request,
        IndexPublishPreview preview,
        CancellationToken ct)
    {
        var (target, targetError) = await _gitHubPublisher.ResolveTargetAsync(request.ProjectPath, ct);
        if (target is null)
            return Failure<IndexPublishResult>(WorkflowErrorKind.Validation, "githubTargetInvalid", targetError ?? "Couldn't resolve the GitHub target.");

        var registry = new RegistryVerifiedSource(_registryChecker);
        var candidate = SerializeBytes(request.Candidate);
        var result = await _gitHubPublisher.PublishAsync(
            target,
            candidate,
            NormalizeCommitMessage(request.CommitMessage),
            async () =>
            {
                var secondAuthorization = await _unsignedGate.AuthorizeAsync(registry, request.Candidate.PluginId, ct);
                return secondAuthorization.Allowed ? null : secondAuthorization.Message;
            },
            request.PreserveExistingReleaseIdentities
                ? ReleaseIdentityPreservation.ValidateTransition
                : null,
            ct);

        if (result.Outcome is not (GitPublishOutcome.Published or GitPublishOutcome.PublishedPendingCdn))
        {
            var remoteChanged = result.Outcome == GitPublishOutcome.PublishedVerificationFailed;
            return Failure<IndexPublishResult>(
                result.Outcome == GitPublishOutcome.CommittedNotPushed || remoteChanged
                    ? WorkflowErrorKind.Conflict
                    : WorkflowErrorKind.Validation,
                "githubIndexPublishFailed",
                result.Message,
                result.Outcome == GitPublishOutcome.CommittedNotPushed
                    ? new[] { "indexCommitted" }
                    : remoteChanged
                        ? new[] { "indexPublished" }
                        : null);
        }

        var publishedBytes = result.PublishedBytes ?? GitHubIndexPublisher.NormalizeToLf(candidate);
        var sha = RecordPublishedBytes(request.ProjectPath, publishedBytes);
        var phases = new List<string> { "indexPublished", "liveVerified" };
        if (_directory is not null)
        {
            try
            {
                await _directory.SubmitAsync(new PluginDirectorySubmission($"{target.Owner}/{target.Repo}",
                    target.IndexPathInRepo, request.Candidate.PluginId, target.BranchRawUrl), ct);
                phases.Add("directorySubmitted");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Failure<IndexPublishResult>(WorkflowErrorKind.Cancelled, "directorySubmissionCancelled",
                    "The GitHub catalog was published and verified. Directory submission was cancelled; retry with 'amm-author directory submit --yes'.", phases);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.Warning(ex, "Directory submission failed after catalog publication");
                return Failure<IndexPublishResult>(WorkflowErrorKind.Conflict, "directorySubmissionFailed",
                    "The GitHub catalog was published and verified, but directory submission failed. Publish on the repository's default branch, then retry with 'amm-author directory submit --yes'.", phases);
            }
        }
        return Success(
            "indexPublished",
            new IndexPublishResult(request.Candidate.PluginId, sha, preview.DestinationDescription, phases),
            result.Message,
            phases);
    }

    private async Task<WorkflowResult<PluginRepoIndex>> ReconcileUnsignedAsync(
        string projectPath,
        PluginRepoIndex local,
        byte[] localBytes,
        bool dryRun,
        bool confirmAdoption,
        CancellationToken ct)
    {
        byte[]? live = null;
        byte[]? committed = null;
        try
        {
            var (target, _) = await _gitHubPublisher.ResolveTargetAsync(projectPath, ct);
            if (target is null)
                return Success("catalogReconcileSkipped", local, "The GitHub publication target could not be resolved, so no live catalog was adopted.");
            var remote = await _gitHubPublisher.ReadRemoteIndexAsync(target, ct);
            if (!remote.Succeeded)
                return Failure("catalogLiveReadFailed", WorkflowErrorKind.Conflict, remote.Error ?? "The exact remote catalog could not be read.", local);
            live = remote.IndexBytes;
            if (_config.GetLastPublishedIndexSha(projectPath) is null)
            {
                var head = await ProcessRunner.RunBinaryAsync("git",
                    new[] { "show", $"HEAD:{target.IndexPathInRepo}" }, target.WorktreeRoot, ct: ct);
                if (head.ExitCode == 0)
                    committed = head.Stdout;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure("catalogLiveReadFailed", WorkflowErrorKind.Conflict, $"Couldn't read the live unsigned catalog: {ex.Message}", local);
        }

        if (live is null || live.AsSpan().SequenceEqual(localBytes))
            return Success("catalogAlreadyCurrent", local, "The local catalog is already current.");

        PluginRepoIndex liveIndex;
        try
        {
            liveIndex = Deserialize(live);
            var report = Validate(liveIndex);
            if (report.PublishBlockers.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, report.PublishBlockers));
            if (!string.Equals(liveIndex.PluginId, local.PluginId, StringComparison.Ordinal))
                throw new InvalidOperationException("The live catalog belongs to a different plugin id.");
        }
        catch (Exception ex)
        {
            return Failure("catalogLiveInvalid", WorkflowErrorKind.Conflict, $"The live catalog couldn't be adopted safely: {ex.Message}", local);
        }

        var decision = DecideReconciliation(localBytes, live, _config.GetLastPublishedIndexSha(projectPath), committed);
        if (decision == ReconciliationDecision.KeepLocal)
            return Success("catalogLocalChangesPreserved", local, "The remote catalog has not changed; local edits were preserved.");
        if (decision == ReconciliationDecision.RequireConfirmation && !confirmAdoption)
        {
            return Failure(
                WorkflowErrorKind.Conflict,
                "catalogAdoptionConfirmationRequired",
                "The live catalog differs, and this folder contains changes that were never published. Adopting it would discard those changes.",
                liveIndex);
        }

        if (dryRun)
        {
            return Success(
                "catalogReconcilePreviewed",
                liveIndex,
                "The newer live unsigned catalog would replace this folder's stale catalog.");
        }

        var adoption = LocalIndexAdoption.ReplaceIfUnchanged(
            IndexFileService.GetIndexPath(projectPath),
            localBytes,
            live,
            out var error);
        if (adoption != AdoptionResult.Replaced)
            return Failure<PluginRepoIndex>(WorkflowErrorKind.Conflict, "catalogAdoptionSuperseded", error ?? "index.json changed during reconciliation.");

        RecordPublishedBytes(projectPath, live);
        return Success("catalogReconciled", liveIndex, "Adopted the newer live unsigned catalog.");
    }

    internal enum ReconciliationDecision { KeepLocal, AdoptRemote, RequireConfirmation }

    internal static ReconciliationDecision DecideReconciliation(
        byte[] local, byte[] live, string? lastPublishedSha, byte[]? committed)
    {
        // Git normalizes catalogs to LF. A Windows checkout's line endings are not local edits.
        static string Hash(byte[] bytes) => Convert.ToHexStringLower(
            SHA256.HashData(GitHubIndexPublisher.NormalizeToLf(bytes)));
        var baseline = lastPublishedSha ?? (committed is null ? null : Hash(committed));
        if (string.Equals(Hash(live), baseline, StringComparison.OrdinalIgnoreCase))
            return ReconciliationDecision.KeepLocal;
        if (string.Equals(Hash(local), baseline, StringComparison.OrdinalIgnoreCase))
            return ReconciliationDecision.AdoptRemote;
        return ReconciliationDecision.RequireConfirmation;
    }

    private IReadOnlyList<string> DescribeChanges(string projectPath, PluginRepoIndex candidate)
    {
        try
        {
            var current = _indexFiles.Load(projectPath);
            var currentBytes = SerializeBytes(current);
            var candidateBytes = SerializeBytes(candidate);
            if (currentBytes.AsSpan().SequenceEqual(candidateBytes))
                return new[] { "No in-memory difference from the saved index." };
            return new[]
            {
                $"Games: {current.Games.Count} to {candidate.Games.Count}.",
                $"Releases: {current.ReleasesByGameId.Values.Sum(list => list.Count)} to {candidate.ReleasesByGameId.Values.Sum(list => list.Count)}."
            };
        }
        catch
        {
            return new[]
            {
                $"Candidate contains {candidate.Games.Count} game(s) and {candidate.ReleasesByGameId.Values.Sum(list => list.Count)} release(s)."
            };
        }
    }

    private string RecordPublishedBytes(string projectPath, byte[] bytes)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        _config.RecordRecent(projectPath, Path.GetFileName(projectPath));
        _config.SetLastPublishedIndexSha(projectPath, sha);
        return sha;
    }

    private static byte[] SerializeBytes(PluginRepoIndex candidate) =>
        Encoding.UTF8.GetBytes(SerializeText(candidate, trailingNewline: true));

    private static string SerializeText(PluginRepoIndex candidate, bool trailingNewline)
    {
        var json = JsonSerializer.Serialize(candidate, JsonOptions);
        return trailingNewline ? json + Environment.NewLine : json;
    }

    private static PluginRepoIndex Deserialize(byte[] bytes) =>
        JsonSerializer.Deserialize<PluginRepoIndex>(bytes, JsonOptions)
        ?? throw new InvalidOperationException("index.json deserialized to null.");

    private static string NormalizeCommitMessage(string message) =>
        string.IsNullOrWhiteSpace(message) ? "Update accessibility mod index" : message.Trim();

    private static WorkflowResult<T> Success<T>(
        string status,
        T value,
        string message,
        IReadOnlyList<string>? completedPhases = null) =>
        new(status, value, new[] { message }, completedPhases: completedPhases);

    private static WorkflowResult<T> Failure<T>(
        WorkflowErrorKind kind,
        string status,
        string message,
        IReadOnlyList<string>? completedPhases = null) =>
        new(status, default, new[] { message }, kind, completedPhases);

    private static WorkflowResult<T> Failure<T>(
        WorkflowErrorKind kind,
        string status,
        string message,
        T value) =>
        new(status, value, new[] { message }, kind);

    private static WorkflowResult<T> Failure<T>(
        string status,
        WorkflowErrorKind kind,
        string message,
        T value) =>
        new(status, value, new[] { message }, kind);
}
