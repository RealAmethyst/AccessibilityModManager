using AccessibilityModManager.AuthorCli;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Authoring;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class PatreonServerCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly string _config;
    private readonly string _key;
    private readonly FakePatreonWorkflow _patreon = new();
    private readonly FakeServerTransport _server = new();
    private readonly IndexFileService _indexFiles = new(TestLogger.Create());

    public PatreonServerCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "amm-patreon-server-cli-" + Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "project");
        _config = Path.Combine(_root, "config");
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
    public async Task Patreon_commands_return_workflow_data_without_real_OAuth()
    {
        var status = await InvokeAsync(string.Empty, "--json", "--quiet", "patreon", "status");
        var tiers = await InvokeAsync(string.Empty, "--json", "--quiet", "patreon", "tiers");
        var post = await InvokeAsync(
            string.Empty,
            "--json", "--quiet", "patreon", "post", "validate",
            "--url", "https://www.patreon.com/posts/test-12345");

        Assert.Equal((int)CliExitCode.Success, status.ExitCode);
        Assert.Equal((int)CliExitCode.Success, tiers.ExitCode);
        Assert.Equal((int)CliExitCode.Success, post.ExitCode);
        Assert.Contains("tier-1", tiers.Stdout, StringComparison.Ordinal);
        Assert.Contains("selection-1", post.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_configure_reads_the_passphrase_only_from_stdin_and_redacts_it()
    {
        var inputPath = Path.Combine(_root, "server.json");
        File.WriteAllText(inputPath, CatalogWorkflowTests.CatalogFixture.Serialize(Config()));

        var configured = await InvokeAsync(
            "private passphrase\n",
            "--json", "--quiet", "server", "configure",
            "--input", inputPath,
            "--passphrase-stdin");
        var status = await InvokeAsync(string.Empty, "--json", "--quiet", "server", "status");

        Assert.Equal((int)CliExitCode.Success, configured.ExitCode);
        Assert.Equal((int)CliExitCode.Success, status.ExitCode);
        Assert.DoesNotContain("private passphrase", configured.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("private passphrase", configured.Stderr, StringComparison.Ordinal);
        Assert.Contains("hasKeyPassphrase\":true", status.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "private passphrase",
            File.ReadAllText(Path.Combine(_config, "config.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_self_test_and_compare_before_break_are_accessible_commands()
    {
        await SaveConfigAsync();
        var selfTest = await InvokeAsync(
            string.Empty,
            "--project", _project, "--json", "--quiet", "server", "self-test");
        var mismatch = await InvokeAsync(
            string.Empty,
            "--project", _project, "--json", "--quiet", "--yes",
            "server", "lock", "break", "--fingerprint", "wrong");
        var match = await InvokeAsync(
            string.Empty,
            "--project", _project, "--json", "--quiet", "--yes",
            "server", "lock", "break", "--fingerprint", _server.Fingerprint);

        Assert.Equal((int)CliExitCode.Success, selfTest.ExitCode);
        Assert.Equal((int)CliExitCode.Conflict, mismatch.ExitCode);
        Assert.True(match.ExitCode == (int)CliExitCode.Success, match.Stderr + match.Stdout);
        Assert.Equal(1, _server.BreakCalls);
    }

    private async Task SaveConfigAsync()
    {
        var path = Path.Combine(_root, "server-save.json");
        File.WriteAllText(path, CatalogWorkflowTests.CatalogFixture.Serialize(Config()));
        var result = await InvokeAsync(
            string.Empty,
            "--json", "--quiet", "server", "configure", "--input", path);
        Assert.Equal((int)CliExitCode.Success, result.ExitCode);
    }

    private ServerUploadConfig Config() => new()
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
    };

    private async Task<CliRunResult> InvokeAsync(string stdin, params string[] args)
    {
        using var input = new StringReader(stdin);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var console = new TestCliConsole(input, output, error);
        using var services = CliServices.Create(new CliServiceOverrides(
            Console: console,
            Logger: TestLogger.Create(),
            AuthorConfigDirectory: _config,
            LogDirectory: Path.Combine(_root, "logs"),
            GitHubService: new ReleaseWorkflowTests.FakeGitHubService(),
            PublishedAssetProbe: new ReleaseWorkflowTests.FakePublishedAssetProbe(),
            PatreonWorkflow: _patreon,
            ServerAuthorTransport: _server));

        var exitCode = await Program.RunAsync(args, services);
        return new CliRunResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed class FakePatreonWorkflow : IPatreonWorkflow
    {
        public Task<WorkflowResult<PatreonSessionStatus>> GetStatusAsync(CancellationToken ct) =>
            Task.FromResult(new WorkflowResult<PatreonSessionStatus>(
                "patreonStatus",
                new PatreonSessionStatus(true, "Author", "campaign"),
                new[] { "Signed in." }));

        public Task<WorkflowResult<PatreonSessionStatus>> SignInAsync(CancellationToken ct) =>
            GetStatusAsync(ct);

        public Task<WorkflowResult<bool>> SignOutAsync(CancellationToken ct) =>
            Task.FromResult(new WorkflowResult<bool>("patreonSignedOut", true, new[] { "Signed out." }));

        public Task<WorkflowResult<IReadOnlyList<PatreonTierInfo>>> GetTiersAsync(CancellationToken ct) =>
            Task.FromResult(new WorkflowResult<IReadOnlyList<PatreonTierInfo>>(
                "patreonTiersListed",
                new[] { new PatreonTierInfo("tier-1", "Supporter") },
                new[] { "One tier." }));

        public Task<WorkflowResult<PatreonPostInspection>> InspectPostAsync(
            string postUrl,
            CancellationToken ct) =>
            Task.FromResult(new WorkflowResult<PatreonPostInspection>(
                "patreonPostInspected",
                new PatreonPostInspection(
                    "12345",
                    new[] { new PatreonAttachmentInfo("selection-1", "mod.zip", null) }),
                new[] { "One attachment." }));
    }

    private sealed class FakeServerTransport : IServerAuthorTransport
    {
        public string Fingerprint { get; } = new string('e', 64);
        public int BreakCalls { get; private set; }

        public Task<ServerConnectionReport> TestAsync(ServerUploadConfig config, CancellationToken ct) =>
            Task.FromResult(new ServerConnectionReport(
                true,
                new[] { new ServerCheckStep("Connect", true, "Connected.") }));

        public Task<IReadOnlyList<ServerCheckStep>> SelfTestAsync(
            ServerUploadConfig config,
            string pluginId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ServerCheckStep>>(
                new[] { new ServerCheckStep("Take the publish lock", true, "Passed.") });

        public Task<ServerUploadService.RemoteReleaseState> InspectReleaseAsync(
            ServerUploadConfig config,
            ServerReleaseRequest request,
            Stream package,
            string sha256,
            CancellationToken ct) =>
            Task.FromResult(new ServerUploadService.RemoteReleaseState(false, 0, false, false, []));

        public Task<ServerUploadService.ReleasePublishOutcome> PublishReleaseAsync(
            ServerUploadConfig config,
            ServerReleaseRequest request,
            Stream package,
            string sha256,
            CancellationToken ct) =>
            Task.FromResult(new ServerUploadService.ReleasePublishOutcome(
                true, request.Gate is not null, false, false, "https://mods.example.invalid/release.zip"));

        public Task<bool> GateExistsAsync(
            ServerUploadConfig config,
            string gameId,
            string version,
            CancellationToken ct) => Task.FromResult(false);

        public Task PublishGateAsync(
            ServerUploadConfig config,
            string gameId,
            string version,
            PatreonGate gate,
            CancellationToken ct) => Task.CompletedTask;

        public Task RemoveGateAsync(
            ServerUploadConfig config,
            string gameId,
            string version,
            CancellationToken ct) => Task.CompletedTask;

        public Task<ServerUploadService.RemoteLock> InspectLockAsync(
            ServerUploadConfig config,
            string pluginId,
            CancellationToken ct) =>
            Task.FromResult(new ServerUploadService.RemoteLock(true, null, Fingerprint));

        public Task<bool> BreakLockAsync(
            ServerUploadConfig config,
            string pluginId,
            string expectedFingerprint,
            CancellationToken ct)
        {
            BreakCalls++;
            return Task.FromResult(string.Equals(expectedFingerprint, Fingerprint, StringComparison.Ordinal));
        }
    }

    private sealed class TestCliConsole(TextReader input, TextWriter output, TextWriter error) : ICliConsole
    {
        public TextReader In { get; } = input;
        public TextWriter Out { get; } = output;
        public TextWriter Error { get; } = error;
        public bool IsInputRedirected => true;
        public void WriteStatus(string message) => Error.WriteLine(message);
    }

    private sealed record CliRunResult(int ExitCode, string Stdout, string Stderr);
}
