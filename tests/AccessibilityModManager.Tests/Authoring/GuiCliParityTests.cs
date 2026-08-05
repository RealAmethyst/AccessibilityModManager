using System.Text.Json;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Authoring;

public sealed class GuiCliParityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "amm-gui-cli-parity-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Wpf_and_cli_surfaces_preserve_the_same_shared_workflow_decisions()
    {
        Directory.CreateDirectory(_root);
        var package = await BuildPackageAsync();
        var request = CreateRequest(package);
        var wpf = CreateSurface();
        var cli = CreateSurface();

        var wpfPackage = await wpf.ValidatePackageAsync(
            package, request.Release.PluginId, request.Release.GameId, request.Release.Version,
            CancellationToken.None);
        var cliPackage = await cli.ValidatePackageAsync(
            package, request.Release.PluginId, request.Release.GameId, request.Release.Version,
            CancellationToken.None);
        var wpfPreview = await wpf.PreviewCompleteReleaseAsync(request, CancellationToken.None);
        var cliPreview = await cli.PreviewCompleteReleaseAsync(request, CancellationToken.None);
        var wpfPublished = await wpf.PublishCompleteReleaseAsync(
            request, confirmed: true, CancellationToken.None);
        var cliPublished = await cli.PublishCompleteReleaseAsync(
            request, confirmed: true, CancellationToken.None);

        Assert.Equal(wpfPackage.Validation.Errors, cliPackage.Validation.Errors);
        Assert.True(wpfPackage.Validation.IsValid);
        Assert.Equal(Serialize(wpfPreview.Value!.Candidate), Serialize(cliPreview.Value!.Candidate));
        Assert.Equal(Serialize(wpfPreview.Value.Release), Serialize(cliPreview.Value.Release));
        Assert.Equal(Serialize(wpfPreview.Value.Index), Serialize(cliPreview.Value.Index));
        Assert.Equal(Serialize(wpfPublished.Value!.Release), Serialize(cliPublished.Value!.Release));
        Assert.Equal(wpfPublished.Value.IndexDestination, cliPublished.Value.IndexDestination);
        Assert.Equal(wpfPublished.Value.CompletedPhases, cliPublished.Value.CompletedPhases);
    }

    [Fact]
    public async Task Facade_carries_explicit_adoption_confirmation_into_the_shared_index_workflow()
    {
        Directory.CreateDirectory(_root);
        var logger = TestLogger.Create();
        var index = new ConfirmationRecordingIndexWorkflow();
        var facade = new AuthoringWorkflowFacade(
            new AuthorProjectContext(
                new AuthorConfigService(logger, Path.Combine(_root, "confirmation-config")),
                new IndexFileService(logger)),
            new PackageWorkflow(new ManifestBuilderService(logger), new Sha256HashService(), logger),
            new UnusedReleaseWorkflow(),
            index,
            new FixtureCompleteWorkflow());

        await facade.ReconcileIndexAsync(
            _root,
            dryRun: false,
            confirmAdoption: true,
            CancellationToken.None);

        Assert.True(index.ConfirmedAdoption);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private AuthoringWorkflowFacade CreateSurface()
    {
        var logger = TestLogger.Create();
        var config = new AuthorConfigService(logger, Path.Combine(_root, Guid.NewGuid().ToString("N")));
        var indexFiles = new IndexFileService(logger);
        var packages = new PackageWorkflow(
            new ManifestBuilderService(logger),
            new Sha256HashService(),
            logger);
        return new AuthoringWorkflowFacade(
            new AuthorProjectContext(config, indexFiles),
            packages,
            new UnusedReleaseWorkflow(),
            new UnusedIndexWorkflow(),
            new FixtureCompleteWorkflow());
    }

    private async Task<string> BuildPackageAsync()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "reader.dll"), "accessible");
        var output = Path.Combine(_root, "sample.zip");
        var logger = TestLogger.Create();
        await new PackageWorkflow(
                new ManifestBuilderService(logger),
                new Sha256HashService(),
                logger)
            .BuildAsync(
                new PackageBuildRequest(
                    source,
                    output,
                    CatalogWorkflowTests.CatalogFixture.PluginId,
                    CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                    "4.0.0",
                    [],
                    new LifecycleScriptInputs()),
                CancellationToken.None);
        return output;
    }

    private CompleteReleasePublishRequest CreateRequest(string package) =>
        new(
            new ReleasePublishRequest(
                Path.Combine(_root, "project"),
                CatalogWorkflowTests.CatalogFixture.PluginId,
                CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "4.0.0",
                "stable",
                "owner/repo",
                package,
                "sample.zip",
                "Release notes",
                "https://example.invalid/changelog",
                null),
            PublishDestination.GitHub,
            "Publish test release",
            DryRun: false);

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });

    private sealed class FixtureCompleteWorkflow : ICompleteReleasePublishWorkflow
    {
        private bool _previewed;

        public Task<WorkflowResult<CompleteReleasePublishPreview>> PreviewAsync(
            CompleteReleasePublishRequest request,
            CancellationToken ct)
        {
            _previewed = true;
            var candidate = CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex();
            var release = new ReleasePublishPreview(
                request.Release.SourceRepo,
                "v" + request.Release.Version,
                request.Release.AssetFileName!,
                new string('a', 64),
                CreatesRelease: true,
                ReplacesAsset: false);
            var index = new IndexPublishPreview(
                request.Release.PluginId,
                request.IndexDestination,
                "GitHub repository owner/repo, branch main",
                request.IndexCommitMessage,
                ["Add sample-game 4.0.0 (stable)."]);
            return Task.FromResult(new WorkflowResult<CompleteReleasePublishPreview>(
                "completeReleasePreviewed",
                new CompleteReleasePublishPreview(release, index, candidate),
                []));
        }

        public Task<WorkflowResult<CompleteReleasePublishResult>> PublishAsync(
            CompleteReleasePublishRequest request,
            bool confirmed,
            CancellationToken ct)
        {
            Assert.True(_previewed);
            Assert.True(confirmed);
            string[] phases =
            [
                "projectLocked", "catalogReconciled", "packageValidated", "assetUploaded",
                "releaseRecorded", "indexValidated", "indexSaved", "indexPublished", "liveVerified"
            ];
            var release = new ModRelease
            {
                PluginId = request.Release.PluginId,
                GameId = request.Release.GameId,
                Version = request.Release.Version,
                Channel = request.Release.Channel,
                PackageUrl = new Uri("https://example.invalid/sample.zip"),
                Sha256 = new string('a', 64),
                Notes = request.Release.Notes,
                ChangelogUrl = request.Release.ChangelogUrl
            };
            return Task.FromResult(new WorkflowResult<CompleteReleasePublishResult>(
                "completeReleasePublished",
                new CompleteReleasePublishResult(release, new string('b', 64), "GitHub repository owner/repo, branch main", phases),
                [],
                completedPhases: phases));
        }
    }

    private sealed class UnusedReleaseWorkflow : IReleaseWorkflow
    {
        public Task<WorkflowResult<PreparedRelease>> StagePackageAsync(PackageStageRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowResult<ReleasePublishPreview>> PreviewAsync(ReleasePublishRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowResult<PreparedRelease>> PrepareAsync(ReleasePublishRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowResult<ReleasePublishResult>> PublishAsync(PreparedRelease prepared, ReleasePublishRequest request, bool confirmed, CancellationToken ct) => throw new NotSupportedException();
    }

    private class UnusedIndexWorkflow : IIndexWorkflow
    {
        public IndexValidationReport Validate(PluginRepoIndex candidate) => throw new NotSupportedException();
        public Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(string projectPath, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(string projectPath, bool dryRun, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(string projectPath, bool dryRun, bool confirmAdoption, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowResult<string>> SaveAsync(string projectPath, PluginRepoIndex candidate, bool dryRun, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowResult<IndexPublishPreview>> PreviewPublishAsync(IndexPublishRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowResult<IndexPublishResult>> PublishAsync(IndexPublishRequest request, bool confirmed, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowResult<ServerUploadService.RemoteLock>> InspectLockAsync(string pluginId, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkflowResult<bool>> BreakLockAsync(string pluginId, string expectedFingerprint, bool confirmed, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ConfirmationRecordingIndexWorkflow : UnusedIndexWorkflow
    {
        public bool ConfirmedAdoption { get; private set; }

        public override Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(
            string projectPath,
            bool dryRun,
            bool confirmAdoption,
            CancellationToken ct)
        {
            ConfirmedAdoption = confirmAdoption;
            return Task.FromResult(new WorkflowResult<PluginRepoIndex>(
                "catalogAlreadyCurrent",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex(),
                []));
        }
    }
}
