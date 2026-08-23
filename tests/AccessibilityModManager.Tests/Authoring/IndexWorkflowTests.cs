using AccessibilityModManager.AuthorCli;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.Tests.Authoring;

public sealed class IndexWorkflowTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly IndexFileService _indexFiles = new(TestLogger.Create());
    private readonly ServiceProvider _services;
    private readonly IIndexWorkflow _workflow;

    public IndexWorkflowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "amm-index-workflow-" + Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(_root);
        _indexFiles.Save(_project, CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex());
        _services = CliServices.Create(new CliServiceOverrides(
            Logger: TestLogger.Create(),
            AuthorConfigDirectory: Path.Combine(_root, "config"),
            LogDirectory: Path.Combine(_root, "logs"),
            GitHubService: new ReleaseWorkflowTests.FakeGitHubService(),
            PublishedAssetProbe: new ReleaseWorkflowTests.FakePublishedAssetProbe(),
            HttpClient: new HttpClient(new OfflineHandler())));
        _workflow = _services.GetRequiredService<IIndexWorkflow>();
    }

    public void Dispose()
    {
        _services.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Validate_uses_the_manager_publish_blockers()
    {
        var valid = CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex();
        var invalid = CopyIndex(valid, pluginId: "different-plugin");

        Assert.Empty(_workflow.Validate(valid).PublishBlockers);
        Assert.NotEmpty(_workflow.Validate(invalid).PublishBlockers);
    }

    [Fact]
    public async Task Save_dry_run_is_byte_identical_and_real_save_is_durable()
    {
        var before = await File.ReadAllBytesAsync(Path.Combine(_project, "index.json"));
        var candidate = new CatalogWorkflow().SetAuthor(
            _indexFiles.Load(_project),
            new PluginAuthorInfo { DisplayName = "CLI Author" });

        var preview = await _workflow.SaveAsync(_project, candidate, dryRun: true, CancellationToken.None);
        Assert.Equal(WorkflowErrorKind.None, preview.ErrorKind);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(_project, "index.json")));

        var saved = await _workflow.SaveAsync(_project, candidate, dryRun: false, CancellationToken.None);
        Assert.Equal(WorkflowErrorKind.None, saved.ErrorKind);
        Assert.Equal("CLI Author", _indexFiles.Load(_project).Author?.DisplayName);
    }

    [Fact]
    public async Task Publish_refuses_an_unset_destination_before_any_network_or_git_write()
    {
        var request = new IndexPublishRequest(
            _project,
            _indexFiles.Load(_project),
            PublishDestination.Unset,
            "Update index",
            DryRun: true);

        var result = await _workflow.PreviewPublishAsync(request, CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.Validation, result.ErrorKind);
        Assert.Equal("publishDestinationMissing", result.Status);
    }

    [Fact]
    public async Task Reconcile_dry_run_never_changes_local_bytes_when_the_registry_is_offline()
    {
        var before = await File.ReadAllBytesAsync(Path.Combine(_project, "index.json"));

        var result = await _workflow.ReconcileAsync(_project, dryRun: true, CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.None, result.ErrorKind);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(_project, "index.json")));
    }

    private static PluginRepoIndex CopyIndex(PluginRepoIndex source, string pluginId) =>
        new()
        {
            PluginId = pluginId,
            RepoVersion = source.RepoVersion,
            GeneratedAt = source.GeneratedAt,
            Games = source.Games,
            ReleasesByGameId = source.ReleasesByGameId,
            Author = source.Author,
            DependencyPresets = source.DependencyPresets
        };

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Offline test boundary.");
    }
}
