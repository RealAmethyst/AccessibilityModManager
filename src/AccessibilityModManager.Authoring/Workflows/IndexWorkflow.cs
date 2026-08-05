using System.Net;
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
    bool DryRun);

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
    Task<WorkflowResult<ServerUploadService.RemoteLock>> InspectLockAsync(
        string pluginId,
        CancellationToken ct);
    Task<WorkflowResult<bool>> BreakLockAsync(
        string pluginId,
        string expectedFingerprint,
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

    private readonly ProjectReconciler _reconciler;
    private readonly IndexPublishCoordinator _coordinator;
    private readonly GitHubIndexPublisher _gitHubPublisher;
    private readonly UnsignedPublishGate _unsignedGate;
    private readonly RegistryMembershipChecker _registryChecker;
    private readonly ServerUploadService _server;
    private readonly AuthorConfigService _config;
    private readonly IndexFileService _indexFiles;
    private readonly IGitHubService _gitHub;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public IndexWorkflow(
        ProjectReconciler reconciler,
        IndexPublishCoordinator coordinator,
        GitHubIndexPublisher gitHubPublisher,
        UnsignedPublishGate unsignedGate,
        RegistryMembershipChecker registryChecker,
        ServerUploadService server,
        AuthorConfigService config,
        IndexFileService indexFiles,
        IGitHubService gitHub,
        HttpClient http,
        ILogger logger)
    {
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _gitHubPublisher = gitHubPublisher ?? throw new ArgumentNullException(nameof(gitHubPublisher));
        _unsignedGate = unsignedGate ?? throw new ArgumentNullException(nameof(unsignedGate));
        _registryChecker = registryChecker ?? throw new ArgumentNullException(nameof(registryChecker));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _indexFiles = indexFiles ?? throw new ArgumentNullException(nameof(indexFiles));
        _gitHub = gitHub ?? throw new ArgumentNullException(nameof(gitHub));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IndexValidationReport Validate(PluginRepoIndex candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return PluginIndexValidation.Validate(candidate.PluginId, SerializeText(candidate, trailingNewline: false));
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

        var serverConfig = _config.GetServerUploadConfig();
        var outcome = await _reconciler.InspectAsync(
            serverConfig is null ? null : new ServerUploadPublishTransport(_server, serverConfig),
            new RegistryVerifiedSource(_registryChecker),
            local.PluginId,
            localBytes,
            _config.GetLastPublishedIndexSha(fullProjectPath),
            ct);

        switch (outcome.Action)
        {
            case ReconcileAction.Nothing:
                return Success("catalogAlreadyCurrent", local, "The local catalog is already current.");
            case ReconcileAction.Explain:
                return Failure(
                    WorkflowErrorKind.Conflict,
                    "catalogReconcileBlocked",
                    outcome.Message ?? "The published catalog could not be reconciled safely.",
                    local);
            case ReconcileAction.Unsigned:
                return await ReconcileUnsignedAsync(
                    fullProjectPath,
                    local,
                    localBytes,
                    dryRun,
                    confirmAdoption,
                    ct);
            case ReconcileAction.AdoptWithConsent when !confirmAdoption:
            {
                var candidate = Deserialize(outcome.Document!);
                return Failure(
                    WorkflowErrorKind.Conflict,
                    "catalogAdoptionConfirmationRequired",
                    outcome.Message ?? "Adopting the published catalog would replace unpublished local work.",
                    candidate);
            }
            case ReconcileAction.AdoptWithConsent:
            case ReconcileAction.Adopt:
            {
                var replacement = outcome.Document!;
                if (dryRun)
                {
                    return Success(
                        "catalogReconcilePreviewed",
                        Deserialize(replacement),
                        $"Verified publish {outcome.Generation} would replace the stale local catalog.");
                }

                var adoption = LocalIndexAdoption.ReplaceIfUnchanged(indexPath, localBytes, replacement, out var error);
                if (adoption != AdoptionResult.Replaced)
                {
                    return Failure<PluginRepoIndex>(
                        WorkflowErrorKind.Conflict,
                        "catalogAdoptionSuperseded",
                        error ?? "index.json changed while the published catalog was being reconciled.");
                }

                RecordPublishedBytes(fullProjectPath, replacement);
                return Success(
                    "catalogReconciled",
                    Deserialize(replacement),
                    $"Adopted verified publish {outcome.Generation} from the server.");
            }
            default:
                return Failure<PluginRepoIndex>(WorkflowErrorKind.Conflict, "catalogReconcileBlocked", "Unknown reconciliation result.");
        }
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

        if (request.Destination == PublishDestination.Unset)
        {
            return Failure<IndexPublishPreview>(
                WorkflowErrorKind.Validation,
                "publishDestinationMissing",
                "No index publishing destination is selected. Choose GitHub or server first.");
        }

        var projectPath = Path.GetFullPath(request.ProjectPath);
        var changes = DescribeChanges(projectPath, request.Candidate);

        if (request.Destination == PublishDestination.GitHub)
        {
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

        var cfg = _config.GetServerUploadConfig();
        if (cfg is null)
            return Failure<IndexPublishPreview>(WorkflowErrorKind.Validation, "serverNotConfigured", "Server upload is not configured.");

        return Success(
            "indexPublishPreviewed",
            new IndexPublishPreview(
                request.Candidate.PluginId,
                request.Destination,
                $"{cfg.Host} at {IndexPublishCoordinator.CanonicalIndexUrl(request.Candidate.PluginId)}",
                NormalizeCommitMessage(request.CommitMessage),
                changes),
            $"Index publication is valid and would upload atomically to {cfg.Host}.");
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

        return request.Destination == PublishDestination.GitHub
            ? await PublishGitHubAsync(request, previewResult.Value, ct)
            : await PublishServerAsync(request, previewResult.Value, ct);
    }

    public async Task<WorkflowResult<ServerUploadService.RemoteLock>> InspectLockAsync(
        string pluginId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var cfg = _config.GetServerUploadConfig();
        if (cfg is null)
            return Failure<ServerUploadService.RemoteLock>(WorkflowErrorKind.Validation, "serverNotConfigured", "Server upload is not configured.");

        try
        {
            var remoteLock = await _server.ReadPublishLockAsync(cfg, pluginId, ct);
            return Success(
                "publishLockInspected",
                remoteLock,
                remoteLock.Present
                    ? $"A publish lock is present with fingerprint {remoteLock.Fingerprint}."
                    : "No publish lock is present.");
        }
        catch (Exception ex)
        {
            return Failure<ServerUploadService.RemoteLock>(WorkflowErrorKind.Conflict, "publishLockReadFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<bool>> BreakLockAsync(
        string pluginId,
        string expectedFingerprint,
        bool confirmed,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        if (!confirmed)
            return Failure<bool>(WorkflowErrorKind.Conflict, "confirmationRequired", "Breaking a publish lock requires confirmation after reading its fingerprint.");

        var cfg = _config.GetServerUploadConfig();
        if (cfg is null)
            return Failure<bool>(WorkflowErrorKind.Validation, "serverNotConfigured", "Server upload is not configured.");

        try
        {
            var removed = await _server.BreakPublishLockAsync(cfg, pluginId, expectedFingerprint, ct);
            return removed
                ? Success("publishLockBroken", true, "Cleared the exact publish lock that was displayed.")
                : Failure<bool>(WorkflowErrorKind.Conflict, "publishLockChanged", "The publish lock changed after it was displayed, so it was left alone.");
        }
        catch (Exception ex)
        {
            return Failure<bool>(WorkflowErrorKind.Conflict, "publishLockBreakFailed", ex.Message);
        }
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
            ct);

        if (result.Outcome is not (GitPublishOutcome.Published or GitPublishOutcome.PublishedPendingCdn))
        {
            return Failure<IndexPublishResult>(
                result.Outcome == GitPublishOutcome.CommittedNotPushed ? WorkflowErrorKind.Conflict : WorkflowErrorKind.Validation,
                "githubIndexPublishFailed",
                result.Message,
                result.Outcome == GitPublishOutcome.CommittedNotPushed ? new[] { "indexCommitted" } : null);
        }

        var publishedBytes = result.PublishedBytes ?? GitHubIndexPublisher.NormalizeToLf(candidate);
        var sha = RecordPublishedBytes(request.ProjectPath, publishedBytes);
        var phases = new[] { "indexPublished", "liveVerified" };
        return Success(
            "indexPublished",
            new IndexPublishResult(request.Candidate.PluginId, sha, preview.DestinationDescription, phases),
            result.Message,
            phases);
    }

    private async Task<WorkflowResult<IndexPublishResult>> PublishServerAsync(
        IndexPublishRequest request,
        IndexPublishPreview preview,
        CancellationToken ct)
    {
        var cfg = _config.GetServerUploadConfig();
        if (cfg is null)
            return Failure<IndexPublishResult>(WorkflowErrorKind.Validation, "serverNotConfigured", "Server upload is not configured.");

        var candidate = SerializeBytes(request.Candidate);
        var publish = await _coordinator.PublishAsync(
            new ServerUploadPublishTransport(_server, cfg),
            new RegistryVerifiedSource(_registryChecker),
            new PublishRequest(request.Candidate.PluginId, candidate)
            {
                ConfirmOrdinary = false,
                ChangeSummary = NormalizeCommitMessage(request.CommitMessage)
            },
            _ => true,
            ct);

        if (publish.Status == PublishStatus.NotSigned)
        {
            return await PublishUnsignedServerAsync(
                request,
                preview,
                cfg,
                candidate,
                publish.VerifiedRegistryJson!,
                ct);
        }

        if (publish.Status is not (PublishStatus.Published or PublishStatus.AlreadyUpToDate or PublishStatus.Recovered) ||
            !publish.LocalSourceIsLive)
        {
            return Failure<IndexPublishResult>(
                publish.Status == PublishStatus.Cancelled ? WorkflowErrorKind.Cancelled : WorkflowErrorKind.Conflict,
                "serverIndexPublishFailed",
                publish.Message);
        }

        var sha = RecordPublishedBytes(request.ProjectPath, candidate);
        var phases = new[] { "indexPublished", "liveVerified" };
        return Success(
            "indexPublished",
            new IndexPublishResult(request.Candidate.PluginId, sha, preview.DestinationDescription, phases),
            publish.Message,
            phases);
    }

    private async Task<WorkflowResult<IndexPublishResult>> PublishUnsignedServerAsync(
        IndexPublishRequest request,
        IndexPublishPreview preview,
        ServerUploadConfig cfg,
        byte[] candidate,
        string verifiedRegistryJson,
        CancellationToken ct)
    {
        var registered = IndexProofService.TryReadIndexUrl(verifiedRegistryJson, request.Candidate.PluginId);
        if (registered.IdCaseDiffers)
            return Failure<IndexPublishResult>(WorkflowErrorKind.Conflict, "registryIdentityMismatch", "The registry spells this plugin id with different capitalisation.");
        if (registered.Listed && registered.Url is null)
            return Failure<IndexPublishResult>(WorkflowErrorKind.Conflict, "registeredIndexUrlMissing", "The registry lists this plugin but carries no usable index URL.");
        if (registered.Url is { } address &&
            IndexPublishCoordinator.IndexUrlMismatch(address, request.Candidate.PluginId) is { } mismatch)
            return Failure<IndexPublishResult>(WorkflowErrorKind.Conflict, "registeredIndexUrlMismatch", mismatch);

        try
        {
            await _server.PublishIndexAsync(cfg, request.Candidate.PluginId, candidate, beforeSwitchAsync: null, ct);
            var readBack = await _server.ReadPluginIndexAsync(cfg, request.Candidate.PluginId, ct);
            if (!readBack.Present || readBack.Bytes is null || !readBack.Bytes.AsSpan().SequenceEqual(candidate))
            {
                return Failure<IndexPublishResult>(
                    WorkflowErrorKind.Conflict,
                    "liveReadBackMismatch",
                    "The index switched live, but the read-back bytes did not match the candidate.",
                    new[] { "indexPublished" });
            }

            var sha = RecordPublishedBytes(request.ProjectPath, candidate);
            var phases = new[] { "indexPublished", "liveVerified" };
            return Success(
                "indexPublished",
                new IndexPublishResult(request.Candidate.PluginId, sha, preview.DestinationDescription, phases),
                "Published the unsigned index and verified the exact live bytes.",
                phases);
        }
        catch (IndexPublishFailedException ex) when (ex.RenameAttempted)
        {
            return Failure<IndexPublishResult>(WorkflowErrorKind.Conflict, "indexPublishInterrupted", ex.Message);
        }
        catch (Exception ex)
        {
            return Failure<IndexPublishResult>(WorkflowErrorKind.Conflict, "indexPublishFailed", ex.Message);
        }
    }

    private async Task<WorkflowResult<PluginRepoIndex>> ReconcileUnsignedAsync(
        string projectPath,
        PluginRepoIndex local,
        byte[] localBytes,
        bool dryRun,
        bool confirmAdoption,
        CancellationToken ct)
    {
        var destination = _config.GetPublishDestination(projectPath, local.PluginId);
        byte[]? live = null;
        try
        {
            if (destination == PublishDestination.Server)
            {
                var cfg = _config.GetServerUploadConfig();
                if (cfg is null)
                    return Success("catalogReconcileSkipped", local, "Server upload is not configured, so no unsigned live catalog was adopted.");
                var remote = await _server.ReadPluginIndexAsync(cfg, local.PluginId, ct);
                live = remote.Present ? remote.Bytes : null;
            }
            else if (destination == PublishDestination.GitHub)
            {
                var (target, _) = await _gitHubPublisher.ResolveTargetAsync(projectPath, ct);
                if (target is null)
                    return Success("catalogReconcileSkipped", local, "The GitHub publication target could not be resolved, so no live catalog was adopted.");
                using var response = await _http.GetAsync(target.BranchRawUrl, ct);
                if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone))
                {
                    response.EnsureSuccessStatusCode();
                    live = await response.Content.ReadAsByteArrayAsync(ct);
                }
            }
            else
            {
                return Success("catalogReconcileSkipped", local, "No publishing destination is selected, so no live catalog was adopted.");
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

        var localSha = Convert.ToHexStringLower(SHA256.HashData(localBytes));
        var lastPublished = _config.GetLastPublishedIndexSha(projectPath);
        if ((lastPublished is null || !string.Equals(localSha, lastPublished, StringComparison.OrdinalIgnoreCase)) &&
            !confirmAdoption)
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
