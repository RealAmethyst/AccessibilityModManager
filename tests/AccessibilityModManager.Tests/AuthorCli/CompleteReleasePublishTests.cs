using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Authoring;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class CompleteReleasePublishTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly IndexFileService _indexFiles = new(TestLogger.Create());

    public CompleteReleasePublishTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "amm-complete-release-" + Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(_root);
        _indexFiles.Save(_project, CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Complete_release_reports_the_exact_success_phase_order()
    {
        var package = await BuildPackageAsync();
        var workflow = CreateWorkflow(failBefore: null);

        var result = await workflow.PublishAsync(CreateRequest(package), confirmed: true, CancellationToken.None);

        var expected = new[]
        {
            "projectLocked", "catalogReconciled", "packageValidated", "assetUploaded",
            "releaseRecorded", "indexValidated", "indexSaved", "indexPublished", "liveVerified"
        };
        Assert.Equal(WorkflowErrorKind.None, result.ErrorKind);
        Assert.Equal(expected, result.CompletedPhases);
        Assert.Equal(expected, result.Value!.CompletedPhases);
        Assert.Contains(
            _indexFiles.Load(_project).ReleasesByGameId[CatalogWorkflowTests.CatalogFixture.PrimaryGameId],
            release => release.Version == "9.9.9" && release.Channel == "stable");
    }

    public static TheoryData<string, string[]> FailureCases => new()
    {
        {
            "catalogReconciled",
            new[] { "projectLocked" }
        },
        {
            "packageValidated",
            new[] { "projectLocked", "catalogReconciled" }
        },
        {
            "assetUploaded",
            new[] { "projectLocked", "catalogReconciled", "packageValidated" }
        },
        {
            "indexValidated",
            new[]
            {
                "projectLocked", "catalogReconciled", "packageValidated", "assetUploaded", "releaseRecorded"
            }
        },
        {
            "indexSaved",
            new[]
            {
                "projectLocked", "catalogReconciled", "packageValidated", "assetUploaded",
                "releaseRecorded", "indexValidated"
            }
        },
        {
            "indexPublished",
            new[]
            {
                "projectLocked", "catalogReconciled", "packageValidated", "assetUploaded",
                "releaseRecorded", "indexValidated", "indexSaved"
            }
        },
        {
            "liveVerified",
            new[]
            {
                "projectLocked", "catalogReconciled", "packageValidated", "assetUploaded",
                "releaseRecorded", "indexValidated", "indexSaved", "indexPublished"
            }
        }
    };

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task Failure_reports_only_phases_that_really_completed(
        string failBefore,
        string[] expectedPhases)
    {
        var package = await BuildPackageAsync();
        var workflow = CreateWorkflow(failBefore);

        var result = await workflow.PublishAsync(CreateRequest(package), confirmed: true, CancellationToken.None);

        Assert.NotEqual(WorkflowErrorKind.None, result.ErrorKind);
        Assert.Equal(expectedPhases, result.CompletedPhases);
    }

    [Fact]
    public async Task Dry_run_does_not_lock_write_or_upload()
    {
        var package = await BuildPackageAsync();
        var before = await File.ReadAllBytesAsync(Path.Combine(_project, "index.json"));
        var release = new ControlledReleaseWorkflow(failBefore: null, TestLogger.Create());
        var indexes = new ControlledIndexWorkflow(_indexFiles, failBefore: null);
        var workflow = CreateWorkflow(release, indexes);
        var request = CreateRequest(package) with { DryRun = true };

        var result = await workflow.PublishAsync(request, confirmed: false, CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.None, result.ErrorKind);
        Assert.Equal(0, release.PublishCalls);
        Assert.Equal(0, indexes.SaveCalls);
        Assert.Equal(0, indexes.PublishCalls);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(_project, "index.json")));
        Assert.False(File.Exists(Path.Combine(_project, ".amm-author.lock")));
    }

    private CompleteReleasePublishWorkflow CreateWorkflow(string? failBefore)
    {
        var logger = TestLogger.Create();
        return CreateWorkflow(
            new ControlledReleaseWorkflow(failBefore, logger),
            new ControlledIndexWorkflow(_indexFiles, failBefore));
    }

    private CompleteReleasePublishWorkflow CreateWorkflow(
        IReleaseWorkflow release,
        IIndexWorkflow index)
    {
        var logger = TestLogger.Create();
        var config = new AuthorConfigService(logger, Path.Combine(_root, "config"));
        var projects = new AuthorProjectContext(config, _indexFiles);
        return new CompleteReleasePublishWorkflow(
            projects,
            new CatalogWorkflow(),
            release,
            index);
    }

    private CompleteReleasePublishRequest CreateRequest(string package) =>
        new(
            new ReleasePublishRequest(
                _project,
                CatalogWorkflowTests.CatalogFixture.PluginId,
                CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "9.9.9",
                "stable",
                "owner/repo",
                package,
                "blind-soldier.zip",
                "Release notes",
                null,
                null),
            PublishDestination.GitHub,
            "Publish test release",
            DryRun: false);

    private async Task<string> BuildPackageAsync()
    {
        var source = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "reader.dll"), "accessible");
        var output = Path.Combine(_root, "release-" + Guid.NewGuid().ToString("N") + ".zip");
        var logger = TestLogger.Create();
        var packages = new PackageWorkflow(
            new ManifestBuilderService(logger),
            new Sha256HashService(),
            logger);
        var built = await packages.BuildAsync(
            new PackageBuildRequest(
                source,
                output,
                CatalogWorkflowTests.CatalogFixture.PluginId,
                CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "9.9.9",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex().Games[0].Dependencies,
                new LifecycleScriptInputs()),
            CancellationToken.None);
        return built.ZipPath;
    }

    private sealed class ControlledReleaseWorkflow : IReleaseWorkflow
    {
        private readonly string? _failBefore;
        private readonly ReleaseWorkflow _real;

        public ControlledReleaseWorkflow(string? failBefore, Serilog.ILogger logger)
        {
            _failBefore = failBefore;
            _real = new ReleaseWorkflow(
                new ReleaseWorkflowTests.FakeGitHubService(),
                new ReleaseWorkflowTests.FakePublishedAssetProbe(),
                logger);
        }

        public int PublishCalls { get; private set; }

        public Task<WorkflowResult<PreparedRelease>> StagePackageAsync(
            PackageStageRequest request,
            CancellationToken ct) =>
            _real.StagePackageAsync(request, ct);

        public Task<WorkflowResult<ReleasePublishPreview>> PreviewAsync(
            ReleasePublishRequest request,
            CancellationToken ct) =>
            _real.PreviewAsync(request, ct);

        public Task<WorkflowResult<PreparedRelease>> PrepareAsync(
            ReleasePublishRequest request,
            CancellationToken ct)
        {
            if (_failBefore == "packageValidated")
            {
                return Task.FromResult(new WorkflowResult<PreparedRelease>(
                    "packageFailed",
                    null,
                    new[] { "Package failed." },
                    WorkflowErrorKind.Validation));
            }

            return _real.PrepareAsync(request, ct);
        }

        public Task<WorkflowResult<ReleasePublishResult>> PublishAsync(
            PreparedRelease prepared,
            ReleasePublishRequest request,
            bool confirmed,
            CancellationToken ct)
        {
            PublishCalls++;
            if (_failBefore == "assetUploaded")
            {
                return Task.FromResult(new WorkflowResult<ReleasePublishResult>(
                    "assetUploadFailed",
                    null,
                    new[] { "Asset failed." },
                    WorkflowErrorKind.Conflict));
            }

            var release = new ModRelease
            {
                GameId = request.GameId,
                PluginId = request.PluginId,
                Version = request.Version,
                Channel = request.Channel,
                PackageUrl = GitHubService.BuildAssetUrl(
                    prepared.Preview.Repository,
                    prepared.Preview.Tag,
                    prepared.Preview.AssetFileName),
                Sha256 = prepared.Sha256,
                Notes = request.Notes
            };
            return Task.FromResult(new WorkflowResult<ReleasePublishResult>(
                "releaseUploaded",
                new ReleasePublishResult(
                    release,
                    release.PackageUrl!.AbsoluteUri,
                    prepared.Sha256,
                    new[] { "githubReleaseCreated" }),
                new[] { "Uploaded." },
                completedPhases: new[] { "githubReleaseCreated" }));
        }
    }

    private sealed class ControlledIndexWorkflow(
        IndexFileService indexFiles,
        string? failBefore) : IIndexWorkflow
    {
        public int SaveCalls { get; private set; }
        public int PublishCalls { get; private set; }

        public IndexValidationReport Validate(PluginRepoIndex candidate)
        {
            if (failBefore == "indexValidated")
                return new IndexValidationReport(candidate, new[] { "Index blocked." }, []);

            return PluginIndexValidation.Validate(
                candidate.PluginId,
                CatalogWorkflowTests.CatalogFixture.Serialize(candidate));
        }

        public Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(
            string projectPath,
            CancellationToken ct) =>
            ReconcileAsync(projectPath, dryRun: false, ct);

        public Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(
            string projectPath,
            bool dryRun,
            CancellationToken ct)
        {
            if (failBefore == "catalogReconciled")
            {
                return Task.FromResult(new WorkflowResult<PluginRepoIndex>(
                    "reconcileFailed",
                    null,
                    new[] { "Reconcile failed." },
                    WorkflowErrorKind.Conflict));
            }

            return Task.FromResult(new WorkflowResult<PluginRepoIndex>(
                dryRun ? "catalogReconcilePreviewed" : "catalogReconciled",
                indexFiles.Load(projectPath),
                new[] { "Reconciled." }));
        }

        public Task<WorkflowResult<string>> SaveAsync(
            string projectPath,
            PluginRepoIndex candidate,
            bool dryRun,
            CancellationToken ct)
        {
            SaveCalls++;
            if (failBefore == "indexSaved")
            {
                return Task.FromResult(new WorkflowResult<string>(
                    "indexSaveFailed",
                    null,
                    new[] { "Save failed." },
                    WorkflowErrorKind.Conflict));
            }

            if (!dryRun)
                indexFiles.Save(projectPath, candidate);
            return Task.FromResult(new WorkflowResult<string>(
                dryRun ? "indexSavePreviewed" : "indexSaved",
                new string('a', 64),
                new[] { "Saved." }));
        }

        public Task<WorkflowResult<IndexPublishPreview>> PreviewPublishAsync(
            IndexPublishRequest request,
            CancellationToken ct) =>
            Task.FromResult(new WorkflowResult<IndexPublishPreview>(
                "indexPublishPreviewed",
                new IndexPublishPreview(
                    request.Candidate.PluginId,
                    request.Destination,
                    "test destination",
                    request.CommitMessage,
                    new[] { "Release added." }),
                new[] { "Previewed." }));

        public Task<WorkflowResult<IndexPublishResult>> PublishAsync(
            IndexPublishRequest request,
            bool confirmed,
            CancellationToken ct)
        {
            PublishCalls++;
            if (failBefore == "indexPublished")
            {
                return Task.FromResult(new WorkflowResult<IndexPublishResult>(
                    "indexPublishFailed",
                    null,
                    new[] { "Publish failed." },
                    WorkflowErrorKind.Conflict));
            }

            if (failBefore == "liveVerified")
            {
                return Task.FromResult(new WorkflowResult<IndexPublishResult>(
                    "liveReadBackFailed",
                    null,
                    new[] { "Read-back failed." },
                    WorkflowErrorKind.Conflict,
                    new[] { "indexPublished" }));
            }

            var phases = new[] { "indexPublished", "liveVerified" };
            return Task.FromResult(new WorkflowResult<IndexPublishResult>(
                "indexPublished",
                new IndexPublishResult(
                    request.Candidate.PluginId,
                    new string('b', 64),
                    "test destination",
                    phases),
                new[] { "Published." },
                completedPhases: phases));
        }

        public Task<WorkflowResult<ServerUploadService.RemoteLock>> InspectLockAsync(
            string pluginId,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkflowResult<bool>> BreakLockAsync(
            string pluginId,
            string expectedFingerprint,
            bool confirmed,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
