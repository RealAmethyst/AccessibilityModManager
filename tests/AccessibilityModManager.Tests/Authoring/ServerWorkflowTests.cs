using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Authoring;

public sealed class ServerWorkflowTests : IDisposable
{
    private readonly string _root;
    private readonly string _key;
    private readonly AuthorConfigService _config;
    private readonly FakeTransport _transport = new();
    private readonly ServerWorkflow _workflow;

    public ServerWorkflowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "amm-server-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _key = Path.Combine(_root, "id_test");
        File.WriteAllText(_key, "test key boundary");
        var logger = TestLogger.Create();
        _config = new AuthorConfigService(logger, Path.Combine(_root, "config"));
        _workflow = new ServerWorkflow(
            _config,
            _transport,
            new ReleaseWorkflow(
                new ReleaseWorkflowTests.FakeGitHubService(),
                new ReleaseWorkflowTests.FakePublishedAssetProbe(),
                logger));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Configure_validates_the_key_and_never_serializes_the_plain_passphrase()
    {
        var missing = _workflow.Configure(
            new ServerConfigurationInput(Config(Path.Combine(_root, "missing")), "secret"),
            dryRun: false);
        Assert.Equal(WorkflowErrorKind.Validation, missing.ErrorKind);

        var saved = _workflow.Configure(
            new ServerConfigurationInput(Config(_key), "very private phrase"),
            dryRun: false);
        Assert.Equal(WorkflowErrorKind.None, saved.ErrorKind);
        Assert.True(saved.Value!.HasKeyPassphrase);
        var configText = File.ReadAllText(Path.Combine(_root, "config", "config.json"));
        Assert.DoesNotContain("very private phrase", configText, StringComparison.Ordinal);
        Assert.Equal("very private phrase", _config.GetServerUploadConfig()!.KeyPassphrase);
    }

    [Fact]
    public async Task Connection_and_self_test_failures_keep_their_named_steps()
    {
        SaveConfig();
        _transport.Connection = new ServerConnectionReport(
            false,
            new[] { new ServerCheckStep("Verify host key", false, "Host key mismatch.") });
        _transport.SelfTestSteps =
        [
            new ServerCheckStep("Take the publish lock", true, "Taken."),
            new ServerCheckStep("Read the published index over SFTP", false, "Read failed.")
        ];

        var connection = await _workflow.TestAsync(CancellationToken.None);
        var selfTest = await _workflow.SelfTestAsync("blind-soldier", CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.Authentication, connection.ErrorKind);
        Assert.Contains("Host key mismatch", string.Join(" ", connection.Messages), StringComparison.Ordinal);
        Assert.Equal(WorkflowErrorKind.Conflict, selfTest.ErrorKind);
        Assert.Equal(2, selfTest.Value!.Steps.Count);
    }

    [Fact]
    public async Task Upload_publishes_the_same_validated_sha_and_preserves_an_immutable_version()
    {
        SaveConfig();
        var package = await BuildPackageAsync();
        var request = new ServerReleaseRequest(
            CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
            "3.0.0",
            "blind-soldier.zip",
            package,
            null);

        _transport.Remote = new ServerUploadService.RemoteReleaseState(false, 0, false, false, []);
        var published = await _workflow.UploadReleaseAsync(
            CatalogWorkflowTests.CatalogFixture.PluginId,
            request,
            confirmed: true,
            dryRun: false,
            CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.None, published.ErrorKind);
        Assert.Equal(published.Value!.Sha256, _transport.PublishedSha);
        Assert.Equal(1, _transport.PublishCalls);

        _transport.Remote = new ServerUploadService.RemoteReleaseState(true, 999, false, false, []);
        var refused = await _workflow.UploadReleaseAsync(
            CatalogWorkflowTests.CatalogFixture.PluginId,
            request,
            confirmed: true,
            dryRun: false,
            CancellationToken.None);
        Assert.Equal("serverReleaseImmutable", refused.Status);
        Assert.Equal(1, _transport.PublishCalls);
    }

    [Fact]
    public async Task Prepared_upload_keeps_inspection_and_publication_bound_to_one_staged_package()
    {
        SaveConfig();
        var packagePath = await BuildPackageAsync();
        var logger = TestLogger.Create();
        var releaseWorkflow = new ReleaseWorkflow(
            new ReleaseWorkflowTests.FakeGitHubService(),
            new ReleaseWorkflowTests.FakePublishedAssetProbe(),
            logger);
        var stagedResult = await releaseWorkflow.StagePackageAsync(
            new PackageStageRequest(
                CatalogWorkflowTests.CatalogFixture.PluginId,
                CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "3.0.0",
                packagePath,
                "blind-soldier.zip"),
            CancellationToken.None);
        await using var staged = stagedResult.Value!;
        await File.WriteAllTextAsync(packagePath, "the selected source changed after staging");
        var request = new ServerReleaseRequest(
            CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
            "3.0.0",
            "blind-soldier.zip",
            packagePath,
            null);

        var result = await _workflow.UploadPreparedReleaseAsync(
            CatalogWorkflowTests.CatalogFixture.PluginId,
            request,
            staged,
            confirmed: true,
            dryRun: false,
            CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.None, result.ErrorKind);
        Assert.Equal(staged.Sha256, result.Value!.Sha256);
        Assert.Same(staged.Stream, _transport.InspectedStream);
        Assert.Same(staged.Stream, _transport.PublishedStream);
        Assert.Equal(staged.Sha256, _transport.PublishedSha);
    }

    [Fact]
    public async Task Gate_and_lock_operations_use_compare_before_change()
    {
        SaveConfig();
        var gate = new PatreonGate
        {
            CampaignId = "campaign",
            TierIds = new List<string> { "tier" }
        };

        var set = await _workflow.SetGateAsync(
            "ff7", "1.0.0", gate, confirmed: true, dryRun: false, CancellationToken.None);
        _transport.GateExists = true;
        var removed = await _workflow.RemoveGateAsync(
            "ff7", "1.0.0", confirmed: true, dryRun: false, CancellationToken.None);
        var mismatch = await _workflow.BreakLockAsync(
            "blind-soldier", "wrong", confirmed: true, dryRun: false, CancellationToken.None);
        var match = await _workflow.BreakLockAsync(
            "blind-soldier", _transport.LockFingerprint, confirmed: true, dryRun: false, CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.None, set.ErrorKind);
        Assert.Equal(WorkflowErrorKind.None, removed.ErrorKind);
        Assert.Equal(1, _transport.SetGateCalls);
        Assert.Equal(1, _transport.RemoveGateCalls);
        Assert.Equal("serverLockChanged", mismatch.Status);
        Assert.Equal(WorkflowErrorKind.None, match.ErrorKind);
        Assert.Equal(1, _transport.BreakLockCalls);
    }

    private void SaveConfig()
    {
        var result = _workflow.Configure(
            new ServerConfigurationInput(Config(_key), string.Empty),
            dryRun: false);
        Assert.Equal(WorkflowErrorKind.None, result.ErrorKind);
    }

    private ServerUploadConfig Config(string keyPath) => new()
    {
        Host = "mods.example.invalid",
        HostKeyFingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        User = "publisher",
        PrivateKeyPath = keyPath,
        RemoteBasePath = "/srv/releases",
        RemoteCatalogRoot = "/srv/catalog",
        RemoteLockRoot = "/srv/locks",
        PublicBaseUrl = "https://mods.example.invalid/releases",
        Port = 22
    };

    private async Task<string> BuildPackageAsync()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "reader.dll"), "accessible");
        var output = Path.Combine(_root, "server-release.zip");
        var logger = TestLogger.Create();
        var packages = new PackageWorkflow(
            new ManifestBuilderService(logger),
            new Sha256HashService(),
            logger);
        return (await packages.BuildAsync(
            new PackageBuildRequest(
                source,
                output,
                CatalogWorkflowTests.CatalogFixture.PluginId,
                CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "3.0.0",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex().Games[0].Dependencies,
                new LifecycleScriptInputs()),
            CancellationToken.None)).ZipPath;
    }

    private sealed class FakeTransport : IServerAuthorTransport
    {
        public string LockFingerprint { get; } = new string('d', 64);
        public ServerConnectionReport Connection { get; set; } =
            new(true, new[] { new ServerCheckStep("Connect", true, "Connected.") });
        public IReadOnlyList<ServerCheckStep> SelfTestSteps { get; set; } =
            new[] { new ServerCheckStep("Self test", true, "Passed.") };
        public ServerUploadService.RemoteReleaseState Remote { get; set; } =
            new(false, 0, false, false, []);
        public bool GateExists { get; set; }
        public int PublishCalls { get; private set; }
        public int SetGateCalls { get; private set; }
        public int RemoveGateCalls { get; private set; }
        public int BreakLockCalls { get; private set; }
        public string? PublishedSha { get; private set; }
        public Stream? InspectedStream { get; private set; }
        public Stream? PublishedStream { get; private set; }

        public Task<ServerConnectionReport> TestAsync(ServerUploadConfig config, CancellationToken ct) =>
            Task.FromResult(Connection);

        public Task<IReadOnlyList<ServerCheckStep>> SelfTestAsync(
            ServerUploadConfig config,
            string pluginId,
            CancellationToken ct) =>
            Task.FromResult(SelfTestSteps);

        public Task<ServerUploadService.RemoteReleaseState> InspectReleaseAsync(
            ServerUploadConfig config,
            ServerReleaseRequest request,
            Stream package,
            string sha256,
            CancellationToken ct)
        {
            InspectedStream = package;
            return Task.FromResult(Remote);
        }

        public Task<ServerUploadService.ReleasePublishOutcome> PublishReleaseAsync(
            ServerUploadConfig config,
            ServerReleaseRequest request,
            Stream package,
            string sha256,
            CancellationToken ct)
        {
            PublishCalls++;
            PublishedStream = package;
            PublishedSha = sha256;
            return Task.FromResult(new ServerUploadService.ReleasePublishOutcome(
                true,
                request.Gate is not null,
                false,
                false,
                $"https://mods.example.invalid/releases/{request.GameId}/{request.Version}/{request.AssetFileName}"));
        }

        public Task<bool> GateExistsAsync(
            ServerUploadConfig config,
            string gameId,
            string version,
            CancellationToken ct) =>
            Task.FromResult(GateExists);

        public Task PublishGateAsync(
            ServerUploadConfig config,
            string gameId,
            string version,
            PatreonGate gate,
            CancellationToken ct)
        {
            SetGateCalls++;
            GateExists = true;
            return Task.CompletedTask;
        }

        public Task RemoveGateAsync(
            ServerUploadConfig config,
            string gameId,
            string version,
            CancellationToken ct)
        {
            RemoveGateCalls++;
            GateExists = false;
            return Task.CompletedTask;
        }

        public Task<ServerUploadService.RemoteLock> InspectLockAsync(
            ServerUploadConfig config,
            string pluginId,
            CancellationToken ct) =>
            Task.FromResult(new ServerUploadService.RemoteLock(true, null, LockFingerprint));

        public Task<bool> BreakLockAsync(
            ServerUploadConfig config,
            string pluginId,
            string expectedFingerprint,
            CancellationToken ct)
        {
            BreakLockCalls++;
            return Task.FromResult(string.Equals(expectedFingerprint, LockFingerprint, StringComparison.Ordinal));
        }
    }
}
