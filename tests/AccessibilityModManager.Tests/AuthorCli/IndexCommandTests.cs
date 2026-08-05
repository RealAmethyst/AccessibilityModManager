using System.Text.Json;
using AccessibilityModManager.AuthorCli;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Authoring;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class IndexCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly string _config;
    private readonly IndexFileService _indexFiles = new(TestLogger.Create());
    private readonly FakeIndexWorkflow _workflow = new();

    public IndexCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "amm-index-cli-" + Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "project");
        _config = Path.Combine(_root, "config");
        Directory.CreateDirectory(_root);
        _indexFiles.Save(_project, CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Show_and_validate_return_the_complete_current_index()
    {
        var shown = await InvokeAsync(ProjectArgs("index", "show"));
        var validated = await InvokeAsync(ProjectArgs("index", "validate"));

        Assert.Equal((int)CliExitCode.Success, shown.ExitCode);
        Assert.Equal((int)CliExitCode.Success, validated.ExitCode);
        using var document = JsonDocument.Parse(shown.Stdout);
        Assert.Equal(
            CatalogWorkflowTests.CatalogFixture.PluginId,
            document.RootElement.GetProperty("value").GetProperty("index").GetProperty("pluginId").GetString());
    }

    [Fact]
    public async Task Destination_set_is_bound_to_the_explicit_project_and_plugin()
    {
        var set = await InvokeAsync(ProjectArgs("index", "destination", "set", "github"));
        var get = await InvokeAsync(ProjectArgs("index", "destination", "get"));

        Assert.Equal((int)CliExitCode.Success, set.ExitCode);
        Assert.Equal((int)CliExitCode.Success, get.ExitCode);
        Assert.Contains("github", get.Stdout, StringComparison.OrdinalIgnoreCase);

        var config = new AuthorConfigService(TestLogger.Create(), _config);
        Assert.Equal(
            PublishDestination.GitHub,
            config.GetPublishDestination(_project, CatalogWorkflowTests.CatalogFixture.PluginId));
    }

    [Fact]
    public async Task Publish_dry_run_only_previews_and_never_creates_a_project_lock()
    {
        await InvokeAsync(ProjectArgs("index", "destination", "set", "github"));
        File.Delete(Path.Combine(_project, ".amm-author.lock"));
        var before = await File.ReadAllBytesAsync(Path.Combine(_project, "index.json"));

        var run = await InvokeAsync(ProjectArgs("--dry-run", "index", "publish"));

        Assert.Equal((int)CliExitCode.Success, run.ExitCode);
        Assert.Equal(1, _workflow.PreviewPublishCalls);
        Assert.Equal(0, _workflow.PublishCalls);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(_project, "index.json")));
        Assert.False(File.Exists(Path.Combine(_project, ".amm-author.lock")));
    }

    [Fact]
    public async Task Lock_break_dry_run_requires_the_exact_displayed_fingerprint()
    {
        var mismatch = await InvokeAsync(ProjectArgs(
            "--dry-run", "index", "lock", "break", "--fingerprint", "different"));
        var match = await InvokeAsync(ProjectArgs(
            "--dry-run", "index", "lock", "break", "--fingerprint", _workflow.LockFingerprint));

        Assert.Equal((int)CliExitCode.Conflict, mismatch.ExitCode);
        Assert.Equal((int)CliExitCode.Success, match.ExitCode);
        Assert.Equal(0, _workflow.BreakLockCalls);
    }

    private string[] ProjectArgs(params string[] tail) =>
        ["--project", _project, "--json", "--quiet", .. tail];

    private async Task<CliRunResult> InvokeAsync(params string[] args)
    {
        using var input = new StringReader(string.Empty);
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
            IndexWorkflow: _workflow));

        var exitCode = await Program.RunAsync(args, services);
        return new CliRunResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed class FakeIndexWorkflow : IIndexWorkflow
    {
        public string LockFingerprint { get; } = new string('c', 64);
        public int PreviewPublishCalls { get; private set; }
        public int PublishCalls { get; private set; }
        public int BreakLockCalls { get; private set; }

        public IndexValidationReport Validate(PluginRepoIndex candidate) =>
            PluginIndexValidation.Validate(
                candidate.PluginId,
                CatalogWorkflowTests.CatalogFixture.Serialize(candidate));

        public Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(string projectPath, CancellationToken ct) =>
            ReconcileAsync(projectPath, dryRun: false, ct);

        public Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(
            string projectPath,
            bool dryRun,
            CancellationToken ct)
        {
            var index = new IndexFileService(TestLogger.Create()).Load(projectPath);
            return Task.FromResult(new WorkflowResult<PluginRepoIndex>(
                dryRun ? "catalogReconcilePreviewed" : "catalogReconciled",
                index,
                new[] { "Current." }));
        }

        public Task<WorkflowResult<string>> SaveAsync(
            string projectPath,
            PluginRepoIndex candidate,
            bool dryRun,
            CancellationToken ct) =>
            Task.FromResult(new WorkflowResult<string>(
                dryRun ? "indexSavePreviewed" : "indexSaved",
                new string('a', 64),
                new[] { "Saved." }));

        public Task<WorkflowResult<IndexPublishPreview>> PreviewPublishAsync(
            IndexPublishRequest request,
            CancellationToken ct)
        {
            PreviewPublishCalls++;
            return Task.FromResult(new WorkflowResult<IndexPublishPreview>(
                "indexPublishPreviewed",
                new IndexPublishPreview(
                    request.Candidate.PluginId,
                    request.Destination,
                    "test destination",
                    request.CommitMessage,
                    new[] { "One change." }),
                new[] { "Previewed." }));
        }

        public Task<WorkflowResult<IndexPublishResult>> PublishAsync(
            IndexPublishRequest request,
            bool confirmed,
            CancellationToken ct)
        {
            PublishCalls++;
            var phases = new[] { "indexPublished", "liveVerified" };
            return Task.FromResult(new WorkflowResult<IndexPublishResult>(
                "indexPublished",
                new IndexPublishResult(request.Candidate.PluginId, new string('b', 64), "test destination", phases),
                new[] { "Published." },
                completedPhases: phases));
        }

        public Task<WorkflowResult<ServerUploadService.RemoteLock>> InspectLockAsync(
            string pluginId,
            CancellationToken ct) =>
            Task.FromResult(new WorkflowResult<ServerUploadService.RemoteLock>(
                "publishLockInspected",
                new ServerUploadService.RemoteLock(true, null, LockFingerprint),
                new[] { "Lock found." }));

        public Task<WorkflowResult<bool>> BreakLockAsync(
            string pluginId,
            string expectedFingerprint,
            bool confirmed,
            CancellationToken ct)
        {
            BreakLockCalls++;
            return Task.FromResult(new WorkflowResult<bool>(
                "publishLockBroken",
                true,
                new[] { "Broken." }));
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
