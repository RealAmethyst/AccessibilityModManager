using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Authoring.Workflows;

public enum ReleaseAssetDestination
{
    GitHub,
    Server,
    PatreonPost
}

public sealed record CompleteReleasePublishRequest(
    ReleasePublishRequest Release,
    PublishDestination IndexDestination,
    string IndexCommitMessage,
    bool DryRun,
    ReleaseAssetDestination AssetDestination = ReleaseAssetDestination.GitHub,
    string? PatreonAttachmentSelectionId = null);

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
/// Composes release publication without hiding partial completion. Package publication precedes
/// the catalog. Restrictive Patreon gates precede new server bytes, while a changed or removed
/// gate follows an exact live-catalog read-back. Every reported phase is therefore a completed
/// fact rather than an optimistic plan.
/// </summary>
public sealed class CompleteReleasePublishWorkflow(
    AuthorProjectContext projects,
    CatalogWorkflow catalog,
    IReleaseWorkflow releases,
    IIndexWorkflow indexes,
    IServerWorkflow? server = null,
    IPatreonWorkflow? patreon = null,
    IPublishedAssetProbe? publishedAssets = null) : ICompleteReleasePublishWorkflow
{
    private enum DeferredGateChange
    {
        None,
        Set,
        Remove
    }

    private sealed record AssetPreview(
        ReleasePublishPreview Preview,
        ModRelease Release);

    private sealed record AssetPublication(
        ModRelease Release,
        string AssetPhase,
        DeferredGateChange DeferredGate,
        string? PublicUrl,
        bool VerifyPublicBeforeCatalog);

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

            var asset = await PreviewAssetAsync(normalized, ct);
            if (asset.ErrorKind != WorkflowErrorKind.None || asset.Value is null)
                return ForwardFailure<AssetPreview, CompleteReleasePublishPreview>(asset);

            var candidate = StampGeneratedAt(catalog.AddRelease(
                reconciled.Value,
                normalized.Release.GameId,
                asset.Value.Release));

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
                    DryRun: true,
                    PreserveExistingReleaseIdentities: true),
                ct);
            if (indexPreview.ErrorKind != WorkflowErrorKind.None || indexPreview.Value is null)
                return ForwardFailure<IndexPublishPreview, CompleteReleasePublishPreview>(indexPreview);

            var preview = new CompleteReleasePublishPreview(
                asset.Value.Preview,
                indexPreview.Value,
                candidate);
            return new WorkflowResult<CompleteReleasePublishPreview>(
                "completeReleasePreviewed",
                preview,
                new[]
                {
                    $"The package destination is {asset.Value.Preview.DestinationDescription}.",
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

            var release = FindRelease(preview.Value.Candidate, request.Release.GameId, request.Release.Version, request.Release.Channel);
            return new WorkflowResult<CompleteReleasePublishResult>(
                "completeReleaseDryRun",
                new CompleteReleasePublishResult(
                    release,
                    string.Empty,
                    preview.Value.Index.DestinationDescription,
                    []),
                new[] { "Dry run completed; no lock, file write, commit, upload, gate change, or configuration change occurred." });
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

            var preparedResult = await PreparePackageAsync(normalized, ct);
            if (preparedResult.ErrorKind != WorkflowErrorKind.None || preparedResult.Value is null)
                return ForwardFailure<PreparedRelease, CompleteReleasePublishResult>(preparedResult, phases);

            await using var prepared = preparedResult.Value;
            phases.Add("packageValidated");

            var publishedAsset = await PublishAssetAsync(normalized, prepared, ct);
            if (publishedAsset.ErrorKind != WorkflowErrorKind.None || publishedAsset.Value is null)
            {
                AddRemoteAssetPhaseIfCompleted(phases, publishedAsset.CompletedPhases);
                return ForwardFailure<AssetPublication, CompleteReleasePublishResult>(publishedAsset, phases);
            }

            var asset = publishedAsset.Value;
            phases.Add(asset.AssetPhase);

            if (asset.VerifyPublicBeforeCatalog)
            {
                var verified = await VerifyPublicAssetAsync(asset.PublicUrl, prepared.Sha256, ct);
                if (verified.ErrorKind != WorkflowErrorKind.None)
                    return ForwardFailure<bool, CompleteReleasePublishResult>(verified, phases);
                phases.Add("publicAssetVerified");
            }

            var candidate = StampGeneratedAt(catalog.AddRelease(
                reconciled.Value,
                normalized.Release.GameId,
                asset.Release));
            phases.Add("releaseRecorded");

            var validation = indexes.Validate(candidate);
            if (validation.PublishBlockers.Count > 0)
            {
                return new WorkflowResult<CompleteReleasePublishResult>(
                    "indexValidationFailed",
                    null,
                    new[] { "The package destination is ready, but the updated catalog is invalid." }
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

            var publishedIndex = await indexes.PublishAsync(
                new IndexPublishRequest(
                    normalized.Release.ProjectPath,
                    candidate,
                    normalized.IndexDestination,
                    normalized.IndexCommitMessage,
                    DryRun: false,
                    PreserveExistingReleaseIdentities: true),
                confirmed: true,
                ct);

            if (publishedIndex.ErrorKind != WorkflowErrorKind.None || publishedIndex.Value is null)
            {
                AddIndexPhases(phases, publishedIndex.CompletedPhases);
                return ForwardFailure<IndexPublishResult, CompleteReleasePublishResult>(publishedIndex, phases);
            }

            AddIndexPhases(phases, publishedIndex.Value.CompletedPhases);

            if (asset.DeferredGate == DeferredGateChange.Set)
            {
                var changed = await RequireServer().SetGateAsync(
                    asset.Release.GameId,
                    asset.Release.Version,
                    asset.Release.Patreon!,
                    confirmed: true,
                    dryRun: false,
                    ct);
                if (changed.ErrorKind != WorkflowErrorKind.None)
                    return ForwardFailure<bool, CompleteReleasePublishResult>(changed, phases);
                phases.Add("gateUpdated");
            }
            else if (asset.DeferredGate == DeferredGateChange.Remove)
            {
                var removed = await RequireServer().RemoveGateAsync(
                    asset.Release.GameId,
                    asset.Release.Version,
                    confirmed: true,
                    dryRun: false,
                    ct);
                if (removed.ErrorKind != WorkflowErrorKind.None)
                    return ForwardFailure<bool, CompleteReleasePublishResult>(removed, phases);
                phases.Add("gateRemoved");

                var verified = await VerifyPublicAssetAsync(asset.PublicUrl, prepared.Sha256, ct);
                if (verified.ErrorKind != WorkflowErrorKind.None)
                    return ForwardFailure<bool, CompleteReleasePublishResult>(verified, phases);
                phases.Add("publicAssetVerified");
            }

            EnsureExactOrder(phases, normalized.AssetDestination, asset);

            var result = new CompleteReleasePublishResult(
                asset.Release,
                publishedIndex.Value.PublishedSha256,
                publishedIndex.Value.DestinationDescription,
                phases.ToArray());
            return new WorkflowResult<CompleteReleasePublishResult>(
                "completeReleasePublished",
                result,
                new[]
                {
                    $"Published release {asset.Release.Version} ({asset.Release.Channel}) and verified the live catalog."
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

    private async Task<WorkflowResult<AssetPreview>> PreviewAssetAsync(
        CompleteReleasePublishRequest request,
        CancellationToken ct)
    {
        if (request.AssetDestination == ReleaseAssetDestination.GitHub)
        {
            var preview = await releases.PreviewAsync(request.Release, ct);
            if (preview.ErrorKind != WorkflowErrorKind.None || preview.Value is null)
                return ForwardFailure<ReleasePublishPreview, AssetPreview>(preview);
            return Success(
                "releaseDestinationPreviewed",
                new AssetPreview(preview.Value, BuildGitHubRelease(request.Release, preview.Value)),
                preview.Messages);
        }

        var stagedResult = await releases.StagePackageAsync(ToPackageRequest(request.Release), ct);
        if (stagedResult.ErrorKind != WorkflowErrorKind.None || stagedResult.Value is null)
            return ForwardFailure<PreparedRelease, AssetPreview>(stagedResult);

        await using var staged = stagedResult.Value;
        if (request.AssetDestination == ReleaseAssetDestination.Server)
        {
            var serverRequest = ToServerRequest(request.Release, staged.Preview.AssetFileName);
            var inspected = await RequireServer().InspectPreparedReleaseAsync(
                request.Release.PluginId,
                serverRequest,
                staged,
                ct);
            if (inspected.ErrorKind != WorkflowErrorKind.None || inspected.Value is null)
                return ForwardFailure<ServerReleaseInspection, AssetPreview>(inspected);
            if (ValidateRemote(inspected.Value.Remote) is { } remoteError)
                return Failure<AssetPreview>(WorkflowErrorKind.Conflict, remoteError.Status, remoteError.Message, []);

            var release = BuildServerRelease(request.Release, inspected.Value.PublicUrl, staged.Sha256);
            var preview = new ReleasePublishPreview(
                "author-server",
                request.Release.Version,
                staged.Preview.AssetFileName,
                staged.Sha256,
                CreatesRelease: !inspected.Value.Remote.PackageExists,
                ReplacesAsset: false)
            {
                Destination = ReleaseAssetDestination.Server,
                DestinationDescription = inspected.Value.PublicUrl
            };
            return Success("releaseDestinationPreviewed", new AssetPreview(preview, release), inspected.Messages);
        }

        var attachment = await ValidatePatreonAttachmentAsync(request, ct);
        if (attachment.ErrorKind != WorkflowErrorKind.None || attachment.Value is null)
            return ForwardFailure<PatreonAttachmentInfo, AssetPreview>(attachment);
        var patreonRelease = BuildPatreonRelease(request.Release, staged.Sha256, attachment.Value);
        var patreonPreview = new ReleasePublishPreview(
            "patreon",
            request.Release.Patreon!.PostId!,
            attachment.Value.FileName,
            staged.Sha256,
            CreatesRelease: false,
            ReplacesAsset: false)
        {
            Destination = ReleaseAssetDestination.PatreonPost,
            DestinationDescription = $"Patreon post {request.Release.Patreon.PostId}, attachment {attachment.Value.FileName}"
        };
        return Success("releaseDestinationPreviewed", new AssetPreview(patreonPreview, patreonRelease), attachment.Messages);
    }

    private Task<WorkflowResult<PreparedRelease>> PreparePackageAsync(
        CompleteReleasePublishRequest request,
        CancellationToken ct) =>
        request.AssetDestination == ReleaseAssetDestination.GitHub
            ? releases.PrepareAsync(request.Release, ct)
            : releases.StagePackageAsync(ToPackageRequest(request.Release), ct);

    private async Task<WorkflowResult<AssetPublication>> PublishAssetAsync(
        CompleteReleasePublishRequest request,
        PreparedRelease prepared,
        CancellationToken ct)
    {
        if (request.AssetDestination == ReleaseAssetDestination.GitHub)
        {
            var uploaded = await releases.PublishAsync(prepared, request.Release, confirmed: true, ct);
            if (uploaded.ErrorKind != WorkflowErrorKind.None || uploaded.Value is null)
                return ForwardFailure<ReleasePublishResult, AssetPublication>(uploaded);
            return new WorkflowResult<AssetPublication>(
                "assetPublished",
                new AssetPublication(
                    uploaded.Value.Release,
                    "assetUploaded",
                    DeferredGateChange.None,
                    uploaded.Value.AssetUrl,
                    VerifyPublicBeforeCatalog: false),
                uploaded.Messages,
                completedPhases: uploaded.CompletedPhases);
        }

        if (request.AssetDestination == ReleaseAssetDestination.Server)
        {
            var serverRequest = ToServerRequest(request.Release, prepared.Preview.AssetFileName);
            var uploaded = await RequireServer().UploadPreparedReleaseAsync(
                request.Release.PluginId,
                serverRequest,
                prepared,
                confirmed: true,
                dryRun: false,
                ct);
            if (uploaded.ErrorKind != WorkflowErrorKind.None || uploaded.Value is null)
                return ForwardFailure<ServerReleasePublishResult, AssetPublication>(uploaded);

            var release = BuildServerRelease(request.Release, uploaded.Value.Outcome.PublicUrl, prepared.Sha256);
            var deferred = uploaded.Value.Outcome.GateRemovalPending
                ? DeferredGateChange.Remove
                : uploaded.Value.Outcome.GateChangePending
                    ? DeferredGateChange.Set
                    : DeferredGateChange.None;
            return new WorkflowResult<AssetPublication>(
                "assetPublished",
                new AssetPublication(
                    release,
                    "assetUploaded",
                    deferred,
                    uploaded.Value.Outcome.PublicUrl,
                    VerifyPublicBeforeCatalog: release.Patreon is null && deferred == DeferredGateChange.None),
                uploaded.Messages);
        }

        var attachment = await ValidatePatreonAttachmentAsync(request, ct);
        if (attachment.ErrorKind != WorkflowErrorKind.None || attachment.Value is null)
            return ForwardFailure<PatreonAttachmentInfo, AssetPublication>(attachment);
        return new WorkflowResult<AssetPublication>(
            "patreonAssetValidated",
            new AssetPublication(
                BuildPatreonRelease(request.Release, prepared.Sha256, attachment.Value),
                "assetValidated",
                DeferredGateChange.None,
                PublicUrl: null,
                VerifyPublicBeforeCatalog: false),
            new[] { $"Validated Patreon attachment {attachment.Value.FileName} for this release." });
    }

    private async Task<WorkflowResult<PatreonAttachmentInfo>> ValidatePatreonAttachmentAsync(
        CompleteReleasePublishRequest request,
        CancellationToken ct)
    {
        var gate = request.Release.Patreon;
        if (gate is null)
            return Failure<PatreonAttachmentInfo>(WorkflowErrorKind.Validation, "patreonGateMissing", "A Patreon-post release requires Patreon gate metadata.", []);
        if (ValidateGate(gate, requirePost: true) is { } gateError)
            return Failure<PatreonAttachmentInfo>(WorkflowErrorKind.Validation, "patreonGateInvalid", gateError, []);
        if (string.IsNullOrWhiteSpace(request.PatreonAttachmentSelectionId))
        {
            return Failure<PatreonAttachmentInfo>(
                WorkflowErrorKind.Validation,
                "patreonAttachmentSelectionRequired",
                "Validate the Patreon post and supply the stable attachment selection id.",
                []);
        }

        var inspected = await RequirePatreon().InspectPostAsync(
            $"https://www.patreon.com/posts/{gate.PostId}",
            ct);
        if (inspected.ErrorKind != WorkflowErrorKind.None || inspected.Value is null)
            return ForwardFailure<PatreonPostInspection, PatreonAttachmentInfo>(inspected);
        if (!string.Equals(inspected.Value.PostId, gate.PostId, StringComparison.Ordinal))
        {
            return Failure<PatreonAttachmentInfo>(
                WorkflowErrorKind.Conflict,
                "patreonPostChanged",
                "Patreon returned a different post identity than the release requested.",
                []);
        }

        var matches = inspected.Value.Attachments
            .Where(candidate => string.Equals(
                candidate.SelectionId,
                request.PatreonAttachmentSelectionId,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            return Failure<PatreonAttachmentInfo>(
                WorkflowErrorKind.Validation,
                "patreonAttachmentNotFound",
                "The selected attachment id is not present exactly once on that Patreon post. Validate the post again and use a current selection id.",
                []);
        }

        return Success("patreonAttachmentSelected", matches[0], inspected.Messages);
    }

    private async Task<WorkflowResult<bool>> VerifyPublicAssetAsync(
        string? publicUrl,
        string expectedSha256,
        CancellationToken ct)
    {
        if (publishedAssets is null)
            return Failure<bool>(WorkflowErrorKind.Conflict, "publishedAssetProbeUnavailable", "Public-asset verification is unavailable.", []);
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Failure<bool>(WorkflowErrorKind.Validation, "publicAssetUrlInvalid", "The server returned no valid HTTPS public package URL.", []);
        }

        var state = await publishedAssets.ProbeAsync(uri, ct);
        if (state.Status != PublishedAssetStatus.Found)
        {
            return Failure<bool>(
                WorkflowErrorKind.Conflict,
                "publicAssetUnreachable",
                $"The package was published, but {uri} could not be read through the public web address.",
                []);
        }
        if (!string.Equals(state.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Failure<bool>(
                WorkflowErrorKind.Conflict,
                "publicAssetMismatch",
                $"The public web address {uri} serves different bytes than the validated package.",
                []);
        }

        return Success("publicAssetVerified", true, new[] { "The public web address serves the exact validated package bytes." });
    }

    private static CompleteReleasePublishRequest Normalize(CompleteReleasePublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Release);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Release.ProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Release.PluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Release.GameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Release.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Release.Channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Release.LocalZipPath);
        if (request.IndexDestination == PublishDestination.Unset)
            throw new InvalidOperationException("No catalog publishing destination is selected.");
        if (request.Release.ChangelogUrl is { Length: > 0 } changelog &&
            (!Uri.TryCreate(changelog, UriKind.Absolute, out var changelogUri) ||
             !string.Equals(changelogUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Changelog URL must be an absolute https:// URL.");
        }
        if (request.AssetDestination == ReleaseAssetDestination.GitHub && request.Release.Patreon is not null)
            throw new InvalidOperationException("Patreon-gated bytes cannot be published on a public GitHub release.");
        if (request.AssetDestination == ReleaseAssetDestination.PatreonPost && request.Release.Patreon is null)
            throw new InvalidOperationException("A Patreon-post destination requires Patreon gate metadata.");

        return request with
        {
            Release = request.Release with
            {
                ProjectPath = Path.GetFullPath(request.Release.ProjectPath),
                PluginId = request.Release.PluginId.Trim(),
                GameId = request.Release.GameId.Trim(),
                Version = request.Release.Version.Trim(),
                Channel = request.Release.Channel.Trim(),
                SourceRepo = request.Release.SourceRepo?.Trim() ?? string.Empty,
                LocalZipPath = Path.GetFullPath(request.Release.LocalZipPath),
                AssetFileName = NullIfBlank(request.Release.AssetFileName),
                Notes = NullIfBlank(request.Release.Notes),
                ChangelogUrl = NullIfBlank(request.Release.ChangelogUrl)
            },
            IndexCommitMessage = string.IsNullOrWhiteSpace(request.IndexCommitMessage)
                ? "Update accessibility mod index"
                : request.IndexCommitMessage.Trim(),
            PatreonAttachmentSelectionId = NullIfBlank(request.PatreonAttachmentSelectionId)
        };
    }

    private static PackageStageRequest ToPackageRequest(ReleasePublishRequest request) =>
        new(request.PluginId, request.GameId, request.Version, request.LocalZipPath, request.AssetFileName);

    private static ServerReleaseRequest ToServerRequest(ReleasePublishRequest request, string assetFileName) =>
        new(request.GameId, request.Version, assetFileName, request.LocalZipPath, request.Patreon);

    private static ModRelease BuildGitHubRelease(
        ReleasePublishRequest request,
        ReleasePublishPreview preview) =>
        BuildBaseRelease(
            request,
            preview.Sha256,
            GitHubService.BuildAssetUrl(preview.Repository, preview.Tag, preview.AssetFileName),
            patreonGate: null);

    private static ModRelease BuildServerRelease(
        ReleasePublishRequest request,
        string publicUrl,
        string sha256)
    {
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The server produced no valid HTTPS public URL for the release.");
        }

        if (request.Patreon is null)
            return BuildBaseRelease(request, sha256, uri, patreonGate: null);
        if (ValidateGate(request.Patreon, requirePost: false) is { } gateError)
            throw new InvalidOperationException(gateError);

        var gate = new PatreonGate
        {
            CampaignId = request.Patreon.CampaignId.Trim(),
            TierIds = request.Patreon.TierIds.Select(value => value.Trim()).ToList(),
            PostId = null,
            AttachmentFileName = null,
            ServerUrl = uri.AbsoluteUri
        };
        return BuildBaseRelease(request, sha256, packageUrl: null, gate);
    }

    private static ModRelease BuildPatreonRelease(
        ReleasePublishRequest request,
        string sha256,
        PatreonAttachmentInfo attachment)
    {
        var input = request.Patreon!;
        var gate = new PatreonGate
        {
            CampaignId = input.CampaignId.Trim(),
            TierIds = input.TierIds.Select(value => value.Trim()).ToList(),
            PostId = input.PostId,
            AttachmentFileName = attachment.FileName,
            ServerUrl = null
        };
        return BuildBaseRelease(request, sha256, packageUrl: null, gate);
    }

    private static ModRelease BuildBaseRelease(
        ReleasePublishRequest request,
        string sha256,
        Uri? packageUrl,
        PatreonGate? patreonGate) =>
        new()
        {
            GameId = request.GameId,
            PluginId = request.PluginId,
            Version = request.Version,
            Channel = request.Channel,
            PackageUrl = packageUrl,
            Sha256 = sha256,
            ChangelogUrl = NullIfBlank(request.ChangelogUrl),
            Notes = NullIfBlank(request.Notes),
            Patreon = patreonGate
        };

    private static (string Status, string Message)? ValidateRemote(
        ServerUploadService.RemoteReleaseState remote)
    {
        if (remote.OtherAssets.Count > 0)
        {
            return (
                "serverVersionFolderOccupied",
                $"The server version folder already contains another package: {string.Join(", ", remote.OtherAssets)}.");
        }
        if (remote.PackageExists && !remote.PackageMatches)
        {
            return (
                "serverReleaseImmutable",
                "This server version already exists with different bytes. Bump the version instead of replacing it.");
        }
        return null;
    }

    private static string? ValidateGate(PatreonGate gate, bool requirePost)
    {
        if (string.IsNullOrWhiteSpace(gate.CampaignId))
            return "Patreon campaign id is required.";
        if (gate.TierIds.Count == 0 || gate.TierIds.Any(string.IsNullOrWhiteSpace))
            return "At least one nonempty Patreon tier id is required.";
        if (gate.TierIds.Distinct(StringComparer.Ordinal).Count() != gate.TierIds.Count)
            return "Patreon tier ids must be unique.";
        if (requirePost && (string.IsNullOrWhiteSpace(gate.PostId) || !gate.PostId.All(char.IsAsciiDigit)))
            return "A numeric Patreon post id is required for Patreon-post delivery.";
        return null;
    }

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

    private static ModRelease FindRelease(
        PluginRepoIndex candidate,
        string gameId,
        string version,
        string channel) =>
        candidate.ReleasesByGameId[gameId].Single(release =>
            string.Equals(release.Version, version, StringComparison.Ordinal) &&
            string.Equals(release.Channel, channel, StringComparison.Ordinal));

    private IServerWorkflow RequireServer() =>
        server ?? throw new InvalidOperationException("Server authoring is unavailable in this build.");

    private IPatreonWorkflow RequirePatreon() =>
        patreon ?? throw new InvalidOperationException("Patreon authoring is unavailable in this build.");

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

    private static void EnsureExactOrder(
        IReadOnlyList<string> phases,
        ReleaseAssetDestination destination,
        AssetPublication asset)
    {
        var expected = new List<string>
        {
            "projectLocked",
            "catalogReconciled",
            "packageValidated",
            destination == ReleaseAssetDestination.PatreonPost ? "assetValidated" : "assetUploaded"
        };
        if (asset.VerifyPublicBeforeCatalog)
            expected.Add("publicAssetVerified");
        expected.AddRange(
        [
            "releaseRecorded",
            "indexValidated",
            "indexSaved",
            "indexPublished",
            "liveVerified"
        ]);
        if (asset.DeferredGate == DeferredGateChange.Set)
            expected.Add("gateUpdated");
        else if (asset.DeferredGate == DeferredGateChange.Remove)
        {
            expected.Add("gateRemoved");
            expected.Add("publicAssetVerified");
        }

        if (!phases.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The complete release transaction reported phases out of order; refusing to call it complete.");
        }
    }

    private static WorkflowResult<T> Success<T>(
        string status,
        T value,
        IReadOnlyList<string> messages) =>
        new(status, value, messages);

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
