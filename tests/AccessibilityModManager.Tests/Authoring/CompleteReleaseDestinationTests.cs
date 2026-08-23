using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Authoring;

public sealed class CompleteReleaseDestinationTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly string _key;
    private readonly IndexFileService _indexFiles = new(TestLogger.Create());

    public CompleteReleaseDestinationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "amm-release-destinations-" + Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "project");
        _key = Path.Combine(_root, "id_test");
        Directory.CreateDirectory(_root);
        File.WriteAllText(_key, "test key");
        _indexFiles.Save(_project, CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Changed_server_gate_is_applied_only_after_the_live_catalog_matches()
    {
        var package = await BuildPackageAsync("4.0.0");
        var events = new List<string>();
        var transport = new RecordingServerTransport(events)
        {
            Remote = new ServerUploadService.RemoteReleaseState(true, 1, true, true, []),
            Outcome = new ServerUploadService.ReleasePublishOutcome(
                false, false, false, true,
                "https://mods.example.invalid/releases/ff7/4.0.0/blind-soldier.zip")
        };
        var indexes = new RecordingIndexWorkflow(_indexFiles, events);
        var workflow = CreateWorkflow(transport, indexes, new RecordingPatreonWorkflow(events), new RecordingProbe(events));
        var gate = Gate(postId: null);

        var result = await workflow.PublishAsync(
            Request(package, gate, ReleaseAssetDestination.Server),
            confirmed: true,
            CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.None, result.ErrorKind);
        Assert.True(events.IndexOf("serverPublished") < events.IndexOf("indexPublished"));
        Assert.True(events.IndexOf("liveVerified") < events.IndexOf("gateSet"));
        Assert.Null(result.Value!.Release.PackageUrl);
        Assert.Equal(transport.Outcome.PublicUrl, result.Value.Release.Patreon!.ServerUrl);
        Assert.Contains("gateUpdated", result.CompletedPhases!);
    }

    [Fact]
    public async Task Failed_live_catalog_never_applies_a_deferred_gate_change()
    {
        var package = await BuildPackageAsync("4.0.0");
        var events = new List<string>();
        var transport = new RecordingServerTransport(events)
        {
            Remote = new ServerUploadService.RemoteReleaseState(true, 1, true, true, []),
            Outcome = new ServerUploadService.ReleasePublishOutcome(
                false, false, false, true,
                "https://mods.example.invalid/releases/ff7/4.0.0/blind-soldier.zip")
        };
        var indexes = new RecordingIndexWorkflow(_indexFiles, events) { FailLiveVerification = true };
        var workflow = CreateWorkflow(transport, indexes, new RecordingPatreonWorkflow(events), new RecordingProbe(events));

        var result = await workflow.PublishAsync(
            Request(package, Gate(postId: null), ReleaseAssetDestination.Server),
            confirmed: true,
            CancellationToken.None);

        Assert.NotEqual(WorkflowErrorKind.None, result.ErrorKind);
        Assert.DoesNotContain("gateSet", events);
        Assert.DoesNotContain("gateUpdated", result.CompletedPhases ?? []);
    }

    [Fact]
    public async Task Removing_a_server_gate_happens_after_live_verification_and_checks_the_public_bytes()
    {
        var package = await BuildPackageAsync("4.0.0");
        var events = new List<string>();
        var transport = new RecordingServerTransport(events)
        {
            Remote = new ServerUploadService.RemoteReleaseState(true, 1, true, true, []),
            Outcome = new ServerUploadService.ReleasePublishOutcome(
                false, false, true, false,
                "https://mods.example.invalid/releases/ff7/4.0.0/blind-soldier.zip")
        };
        var probe = new RecordingProbe(events) { ShaProvider = () => transport.PublishedSha };
        var workflow = CreateWorkflow(
            transport,
            new RecordingIndexWorkflow(_indexFiles, events),
            new RecordingPatreonWorkflow(events),
            probe);

        var result = await workflow.PublishAsync(
            Request(package, gate: null, ReleaseAssetDestination.Server),
            confirmed: true,
            CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.None, result.ErrorKind);
        Assert.True(events.IndexOf("liveVerified") < events.IndexOf("gateRemoved"));
        Assert.True(events.IndexOf("gateRemoved") < events.IndexOf("publicAssetVerified"));
        Assert.Equal(transport.Outcome.PublicUrl, result.Value!.Release.PackageUrl!.AbsoluteUri);
        Assert.Contains("gateRemoved", result.CompletedPhases!);
        Assert.Contains("publicAssetVerified", result.CompletedPhases!);
    }

    [Fact]
    public async Task New_public_server_release_is_verified_before_the_catalog_can_reference_it()
    {
        var package = await BuildPackageAsync("4.0.0");
        var events = new List<string>();
        var transport = new RecordingServerTransport(events);
        var probe = new RecordingProbe(events) { ShaProvider = () => transport.PublishedSha };
        var workflow = CreateWorkflow(
            transport,
            new RecordingIndexWorkflow(_indexFiles, events),
            new RecordingPatreonWorkflow(events),
            probe);

        var result = await workflow.PublishAsync(
            Request(package, gate: null, ReleaseAssetDestination.Server),
            confirmed: true,
            CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.None, result.ErrorKind);
        Assert.True(events.IndexOf("serverPublished") < events.IndexOf("publicAssetVerified"));
        Assert.True(events.IndexOf("publicAssetVerified") < events.IndexOf("indexSaved"));
    }

    [Fact]
    public async Task Public_server_byte_mismatch_stops_before_catalog_save()
    {
        var package = await BuildPackageAsync("4.0.0");
        var events = new List<string>();
        var workflow = CreateWorkflow(
            new RecordingServerTransport(events),
            new RecordingIndexWorkflow(_indexFiles, events),
            new RecordingPatreonWorkflow(events),
            new RecordingProbe(events) { ShaProvider = () => new string('0', 64) });

        var result = await workflow.PublishAsync(
            Request(package, gate: null, ReleaseAssetDestination.Server),
            confirmed: true,
            CancellationToken.None);

        Assert.Equal("publicAssetMismatch", result.Status);
        Assert.DoesNotContain("indexSaved", events);
    }

    [Fact]
    public async Task Patreon_post_destination_records_the_explicit_attachment_without_server_or_GitHub_upload()
    {
        var package = await BuildPackageAsync("4.0.0");
        var events = new List<string>();
        var transport = new RecordingServerTransport(events);
        var patreon = new RecordingPatreonWorkflow(events)
        {
            Inspection = new PatreonPostInspection(
                "12345",
                new[]
                {
                    new PatreonAttachmentInfo("selected-attachment", "blind-soldier.zip", null)
                    {
                        RequiredTierIds = new[] { "tier-1" }
                    }
                })
        };
        var workflow = CreateWorkflow(
            transport,
            new RecordingIndexWorkflow(_indexFiles, events),
            patreon,
            new RecordingProbe(events));

        var result = await workflow.PublishAsync(
            Request(
                package,
                Gate("12345"),
                ReleaseAssetDestination.PatreonPost,
                patreonAttachmentSelectionId: "selected-attachment"),
            confirmed: true,
            CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.None, result.ErrorKind);
        Assert.Contains("patreonInspected", events);
        Assert.DoesNotContain("serverPublished", events);
        Assert.Null(result.Value!.Release.PackageUrl);
        Assert.Equal("12345", result.Value.Release.Patreon!.PostId);
        Assert.Equal("blind-soldier.zip", result.Value.Release.Patreon.AttachmentFileName);
        Assert.Null(result.Value.Release.Patreon.ServerUrl);
        Assert.Contains("assetValidated", result.CompletedPhases!);
    }

    [Fact]
    public async Task Patreon_post_destination_refuses_an_attachment_selection_that_is_not_on_the_post()
    {
        var package = await BuildPackageAsync("4.0.0");
        var events = new List<string>();
        var patreon = new RecordingPatreonWorkflow(events)
        {
            Inspection = new PatreonPostInspection(
                "12345",
                new[] { new PatreonAttachmentInfo("actual", "blind-soldier.zip", null) })
        };
        var workflow = CreateWorkflow(
            new RecordingServerTransport(events),
            new RecordingIndexWorkflow(_indexFiles, events),
            patreon,
            new RecordingProbe(events));

        var result = await workflow.PublishAsync(
            Request(package, Gate("12345"), ReleaseAssetDestination.PatreonPost, "missing"),
            confirmed: true,
            CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.Validation, result.ErrorKind);
        Assert.Equal("patreonAttachmentNotFound", result.Status);
        Assert.DoesNotContain("indexSaved", result.CompletedPhases ?? []);
    }

    [Fact]
    public async Task Patreon_post_destination_rejects_missing_campaign_and_tiers_before_reading_the_post()
    {
        var package = await BuildPackageAsync("4.0.0");
        var events = new List<string>();
        var workflow = CreateWorkflow(
            new RecordingServerTransport(events),
            new RecordingIndexWorkflow(_indexFiles, events),
            new RecordingPatreonWorkflow(events),
            new RecordingProbe(events));
        var invalidGate = new PatreonGate
        {
            CampaignId = string.Empty,
            TierIds = [],
            PostId = "12345"
        };

        var result = await workflow.PublishAsync(
            Request(package, invalidGate, ReleaseAssetDestination.PatreonPost, "selection"),
            confirmed: true,
            CancellationToken.None);

        Assert.Equal("patreonGateInvalid", result.Status);
        Assert.DoesNotContain("patreonInspected", events);
    }

    private CompleteReleasePublishWorkflow CreateWorkflow(
        RecordingServerTransport transport,
        IIndexWorkflow indexes,
        IPatreonWorkflow patreon,
        IPublishedAssetProbe probe)
    {
        var logger = TestLogger.Create();
        var config = new AuthorConfigService(logger, Path.Combine(_root, "config-" + Guid.NewGuid().ToString("N")));
        var releases = new ReleaseWorkflow(
            new ReleaseWorkflowTests.FakeGitHubService(),
            probe,
            logger);
        var server = new ServerWorkflow(config, transport, releases);
        var configured = server.Configure(
            new ServerConfigurationInput(
                new ServerUploadConfig
                {
                    Host = "mods.example.invalid",
                    HostKeyFingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    User = "publisher",
                    PrivateKeyPath = _key,
                    RemoteBasePath = "/srv/releases",
                    RemoteCatalogRoot = "/srv/catalog",
                    RemoteLockRoot = "/srv/locks",
                    PublicBaseUrl = "https://mods.example.invalid/releases",
                    Port = 22
                },
                string.Empty),
            dryRun: false);
        Assert.Equal(WorkflowErrorKind.None, configured.ErrorKind);

        return new CompleteReleasePublishWorkflow(
            new AuthorProjectContext(config, _indexFiles),
            new CatalogWorkflow(),
            releases,
            indexes,
            server,
            patreon,
            probe);
    }

    private CompleteReleasePublishRequest Request(
        string package,
        PatreonGate? gate,
        ReleaseAssetDestination destination,
        string? patreonAttachmentSelectionId = null) =>
        new(
            new ReleasePublishRequest(
                _project,
                CatalogWorkflowTests.CatalogFixture.PluginId,
                CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "4.0.0",
                "stable",
                destination == ReleaseAssetDestination.GitHub ? "owner/repo" : string.Empty,
                package,
                "blind-soldier.zip",
                "Release notes",
                null,
                gate),
            PublishDestination.Server,
            "Publish test release",
            DryRun: false,
            destination,
            patreonAttachmentSelectionId);

    private static PatreonGate Gate(string? postId) => new()
    {
        CampaignId = "campaign-1",
        TierIds = new List<string> { "tier-1" },
        PostId = postId
    };

    private async Task<string> BuildPackageAsync(string version)
    {
        var source = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "reader.dll"), "accessible");
        var output = Path.Combine(_root, "release-" + Guid.NewGuid().ToString("N") + ".zip");
        var logger = TestLogger.Create();
        return (await new PackageWorkflow(
                new ManifestBuilderService(logger),
                new Sha256HashService(),
                logger)
            .BuildAsync(
                new PackageBuildRequest(
                    source,
                    output,
                    CatalogWorkflowTests.CatalogFixture.PluginId,
                    CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                    version,
                    CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex().Games[0].Dependencies,
                    new LifecycleScriptInputs()),
                CancellationToken.None)).ZipPath;
    }

    private sealed class RecordingIndexWorkflow(
        IndexFileService files,
        List<string> events) : IIndexWorkflow
    {
        public bool FailLiveVerification { get; init; }

        public IndexValidationReport Validate(PluginRepoIndex candidate) =>
            PluginIndexValidation.Validate(candidate.PluginId, CatalogWorkflowTests.CatalogFixture.Serialize(candidate));

        public Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(string projectPath, CancellationToken ct) =>
            ReconcileAsync(projectPath, dryRun: false, ct);

        public Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(
            string projectPath,
            bool dryRun,
            CancellationToken ct) =>
            Task.FromResult(new WorkflowResult<PluginRepoIndex>(
                "catalogReconciled",
                files.Load(projectPath),
                new[] { "Reconciled." }));

        public Task<WorkflowResult<string>> SaveAsync(
            string projectPath,
            PluginRepoIndex candidate,
            bool dryRun,
            CancellationToken ct)
        {
            if (!dryRun) files.Save(projectPath, candidate);
            events.Add("indexSaved");
            return Task.FromResult(new WorkflowResult<string>("indexSaved", new string('a', 64), new[] { "Saved." }));
        }

        public Task<WorkflowResult<IndexPublishPreview>> PreviewPublishAsync(
            IndexPublishRequest request,
            CancellationToken ct) =>
            Task.FromResult(new WorkflowResult<IndexPublishPreview>(
                "indexPublishPreviewed",
                new IndexPublishPreview(
                    request.Candidate.PluginId,
                    request.Destination,
                    "controlled destination",
                    request.CommitMessage,
                    []),
                new[] { "Previewed." }));

        public Task<WorkflowResult<IndexPublishResult>> PublishAsync(
            IndexPublishRequest request,
            bool confirmed,
            CancellationToken ct)
        {
            events.Add("indexPublished");
            if (FailLiveVerification)
            {
                return Task.FromResult(new WorkflowResult<IndexPublishResult>(
                    "liveReadBackFailed",
                    null,
                    new[] { "Live verification failed." },
                    WorkflowErrorKind.Conflict,
                    new[] { "indexPublished" }));
            }

            events.Add("liveVerified");
            var phases = new[] { "indexPublished", "liveVerified" };
            return Task.FromResult(new WorkflowResult<IndexPublishResult>(
                "indexPublished",
                new IndexPublishResult(request.Candidate.PluginId, new string('b', 64), "controlled destination", phases),
                new[] { "Published." },
                completedPhases: phases));
        }

        public Task<WorkflowResult<ServerUploadService.RemoteLock>> InspectLockAsync(
            string pluginId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<WorkflowResult<bool>> BreakLockAsync(
            string pluginId,
            string expectedFingerprint,
            bool confirmed,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingServerTransport(List<string> events) : IServerAuthorTransport
    {
        public ServerUploadService.RemoteReleaseState Remote { get; init; } = new(false, 0, false, false, []);
        public ServerUploadService.ReleasePublishOutcome Outcome { get; init; } = new(
            true, false, false, false,
            "https://mods.example.invalid/releases/ff7/4.0.0/blind-soldier.zip");
        public string? PublishedSha { get; private set; }

        public Task<ServerConnectionReport> TestAsync(ServerUploadConfig config, CancellationToken ct) =>
            Task.FromResult(new ServerConnectionReport(true, []));

        public Task<IReadOnlyList<ServerCheckStep>> SelfTestAsync(
            ServerUploadConfig config,
            string pluginId,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<ServerCheckStep>>([]);

        public Task<ServerUploadService.RemoteReleaseState> InspectReleaseAsync(
            ServerUploadConfig config,
            ServerReleaseRequest request,
            Stream package,
            string sha256,
            CancellationToken ct)
        {
            events.Add("serverInspected");
            return Task.FromResult(Remote);
        }

        public Task<ServerUploadService.ReleasePublishOutcome> PublishReleaseAsync(
            ServerUploadConfig config,
            ServerReleaseRequest request,
            Stream package,
            string sha256,
            CancellationToken ct)
        {
            PublishedSha = sha256;
            events.Add("serverPublished");
            return Task.FromResult(Outcome);
        }

        public Task<bool> GateExistsAsync(
            ServerUploadConfig config,
            string gameId,
            string version,
            CancellationToken ct) => Task.FromResult(Remote.GateExists);

        public Task PublishGateAsync(
            ServerUploadConfig config,
            string gameId,
            string version,
            PatreonGate gate,
            CancellationToken ct)
        {
            events.Add("gateSet");
            return Task.CompletedTask;
        }

        public Task RemoveGateAsync(
            ServerUploadConfig config,
            string gameId,
            string version,
            CancellationToken ct)
        {
            events.Add("gateRemoved");
            return Task.CompletedTask;
        }

        public Task<ServerUploadService.RemoteLock> InspectLockAsync(
            ServerUploadConfig config,
            string pluginId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> BreakLockAsync(
            ServerUploadConfig config,
            string pluginId,
            string expectedFingerprint,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingPatreonWorkflow(List<string> events) : IPatreonWorkflow
    {
        public PatreonPostInspection Inspection { get; init; } = new("12345", []);

        public Task<WorkflowResult<PatreonSessionStatus>> GetStatusAsync(CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<WorkflowResult<PatreonSessionStatus>> SignInAsync(CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<WorkflowResult<bool>> SignOutAsync(CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<WorkflowResult<IReadOnlyList<PatreonTierInfo>>> GetTiersAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkflowResult<PatreonPostInspection>> InspectPostAsync(string postUrl, CancellationToken ct)
        {
            events.Add("patreonInspected");
            return Task.FromResult(new WorkflowResult<PatreonPostInspection>(
                "patreonPostInspected",
                Inspection,
                new[] { "Inspected." }));
        }
    }

    private sealed class RecordingProbe(List<string> events) : IPublishedAssetProbe
    {
        public Func<string?> ShaProvider { get; init; } = () => null;

        public Task<PublishedAssetState> ProbeAsync(Uri url, CancellationToken ct = default)
        {
            events.Add("publicAssetVerified");
            return Task.FromResult(new PublishedAssetState(PublishedAssetStatus.Found, ShaProvider()));
        }
    }
}
