using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.Authoring.Workflows;

/// <summary>
/// Shared entry point for WPF and command-line authoring surfaces. It keeps project locking and
/// workflow selection out of presentation code, while prompts and announcements remain owned by
/// the caller.
/// </summary>
public sealed class AuthoringWorkflowFacade
{
    private readonly AuthorProjectContext _projects;
    private readonly PackageWorkflow _packages;
    private readonly IReleaseWorkflow _releases;
    private readonly IIndexWorkflow _indexes;
    private readonly ICompleteReleasePublishWorkflow _completeReleases;

    public AuthoringWorkflowFacade(
        AuthorProjectContext projects,
        PackageWorkflow packages,
        IReleaseWorkflow releases,
        IIndexWorkflow indexes,
        ICompleteReleasePublishWorkflow completeReleases)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _releases = releases ?? throw new ArgumentNullException(nameof(releases));
        _indexes = indexes ?? throw new ArgumentNullException(nameof(indexes));
        _completeReleases = completeReleases ?? throw new ArgumentNullException(nameof(completeReleases));
    }

    public PackageBuildPreview PreviewPackageBuild(PackageBuildRequest request) =>
        _packages.PreviewBuild(request);

    public Task<PackageInspection> BuildPackageAsync(PackageBuildRequest request, CancellationToken ct) =>
        _packages.BuildAsync(request, ct);

    public Task<PackageInspection> ValidatePackageAsync(
        string zipPath,
        string pluginId,
        string gameId,
        string version,
        CancellationToken ct) =>
        _packages.ValidateAsync(zipPath, pluginId, gameId, version, ct);

    public Task<WorkflowResult<PreparedRelease>> StageReleasePackageAsync(
        PackageStageRequest request,
        CancellationToken ct) =>
        _releases.StagePackageAsync(request, ct);

    public Task<WorkflowResult<ReleasePublishPreview>> PreviewReleaseAsync(
        ReleasePublishRequest request,
        CancellationToken ct) =>
        _releases.PreviewAsync(request, ct);

    public Task<WorkflowResult<PreparedRelease>> PrepareReleaseAsync(
        ReleasePublishRequest request,
        CancellationToken ct) =>
        _releases.PrepareAsync(request, ct);

    public Task<WorkflowResult<ReleasePublishResult>> PublishReleaseAsync(
        PreparedRelease prepared,
        ReleasePublishRequest request,
        bool confirmed,
        CancellationToken ct) =>
        _releases.PublishAsync(prepared, request, confirmed, ct);

    public IndexValidationReport ValidateIndex(PluginRepoIndex candidate) =>
        _indexes.Validate(candidate);

    public Task<WorkflowResult<IndexPublishPreview>> PreviewIndexPublicationAsync(
        IndexPublishRequest request,
        CancellationToken ct) =>
        _indexes.PreviewPublishAsync(request, ct);

    public async Task<WorkflowResult<PluginRepoIndex>> ReconcileIndexAsync(
        string projectPath,
        bool dryRun,
        CancellationToken ct) =>
        await ReconcileIndexAsync(projectPath, dryRun, confirmAdoption: false, ct);

    public async Task<WorkflowResult<PluginRepoIndex>> ReconcileIndexAsync(
        string projectPath,
        bool dryRun,
        bool confirmAdoption,
        CancellationToken ct)
    {
        if (dryRun)
            return await _indexes.ReconcileAsync(projectPath, dryRun: true, confirmAdoption, ct);

        await using var lease = await _projects.AcquireWriteLeaseAsync(projectPath, ct);
        return await _indexes.ReconcileAsync(projectPath, dryRun: false, confirmAdoption, ct);
    }

    public async Task<WorkflowResult<string>> SaveIndexAsync(
        string projectPath,
        PluginRepoIndex candidate,
        bool dryRun,
        CancellationToken ct)
    {
        if (dryRun)
            return await _indexes.SaveAsync(projectPath, candidate, dryRun: true, ct);

        await using var lease = await _projects.AcquireWriteLeaseAsync(projectPath, ct);
        return await _indexes.SaveAsync(projectPath, candidate, dryRun: false, ct);
    }

    public async Task<WorkflowResult<IndexPublishResult>> PublishIndexAsync(
        IndexPublishRequest request,
        bool confirmed,
        CancellationToken ct)
    {
        if (request.DryRun)
            return await _indexes.PublishAsync(request, confirmed, ct);

        await using var lease = await _projects.AcquireWriteLeaseAsync(request.ProjectPath, ct);
        return await _indexes.PublishAsync(request, confirmed, ct);
    }

    public Task<WorkflowResult<ServerUploadService.RemoteLock>> InspectIndexLockAsync(
        string pluginId,
        CancellationToken ct) =>
        _indexes.InspectLockAsync(pluginId, ct);

    public Task<WorkflowResult<bool>> BreakIndexLockAsync(
        string pluginId,
        string expectedFingerprint,
        bool confirmed,
        CancellationToken ct) =>
        _indexes.BreakLockAsync(pluginId, expectedFingerprint, confirmed, ct);

    public Task<WorkflowResult<CompleteReleasePublishPreview>> PreviewCompleteReleaseAsync(
        CompleteReleasePublishRequest request,
        CancellationToken ct) =>
        _completeReleases.PreviewAsync(request, ct);

    public Task<WorkflowResult<CompleteReleasePublishResult>> PublishCompleteReleaseAsync(
        CompleteReleasePublishRequest request,
        bool confirmed,
        CancellationToken ct) =>
        _completeReleases.PublishAsync(request, confirmed, ct);
}
