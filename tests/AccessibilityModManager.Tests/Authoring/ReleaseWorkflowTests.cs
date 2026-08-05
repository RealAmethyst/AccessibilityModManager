using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Authoring;

public sealed class ReleaseWorkflowTests : IDisposable
{
    private readonly string _root;
    private readonly FakeGitHubService _github = new();
    private readonly FakePublishedAssetProbe _assets = new();
    private readonly ReleaseWorkflow _workflow;

    public ReleaseWorkflowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "amm-release-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workflow = new ReleaseWorkflow(_github, _assets, TestLogger.Create());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Prepare_and_publish_hash_and_upload_the_same_staged_bytes()
    {
        var package = await BuildPackageAsync("1.0.0");
        var request = Request(package, "1.0.0");

        var preparedResult = await _workflow.PrepareAsync(request, CancellationToken.None);
        Assert.Equal(WorkflowErrorKind.None, preparedResult.ErrorKind);
        await using var prepared = Assert.IsType<PreparedRelease>(preparedResult.Value);
        Assert.True(prepared.Preview.CreatesRelease);
        Assert.False(prepared.Preview.ReplacesAsset);

        var published = await _workflow.PublishAsync(prepared, request, confirmed: false, CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.None, published.ErrorKind);
        Assert.NotNull(published.Value);
        Assert.Equal(prepared.Sha256, published.Value!.Sha256);
        Assert.Equal(prepared.Sha256, _github.UploadedSha256);
        Assert.Equal("1.0.0", published.Value.Release.Version);
        Assert.Equal("stable", published.Value.Release.Channel);
        Assert.Equal(new[] { "packageStaged", "packageValidated", "githubReleaseCreated" }, published.Value.CompletedPhases);
    }

    [Fact]
    public async Task Existing_tag_updates_notes_without_creating_a_second_release_or_reuploading_matching_bytes()
    {
        var package = await BuildPackageAsync("1.0.0");
        var request = Request(package, "1.0.0") with { Notes = "Updated notes" };
        _github.Releases.Add(new GitHubRelease("v1.0.0", "v1.0.0", false, false));
        var assetUrl = GitHubService.BuildAssetUrl(request.SourceRepo, "v1.0.0", Path.GetFileName(package));
        _assets.Results[assetUrl.AbsoluteUri] = new PublishedAssetState(
            PublishedAssetStatus.Found,
            await new Sha256HashService().ComputeAsync(package));

        var preparedResult = await _workflow.PrepareAsync(request, CancellationToken.None);
        await using var prepared = Assert.IsType<PreparedRelease>(preparedResult.Value);
        var published = await _workflow.PublishAsync(prepared, request, confirmed: false, CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.None, published.ErrorKind);
        Assert.Equal(0, _github.CreateCalls);
        Assert.Equal(0, _github.UploadCalls);
        Assert.Equal(1, _github.EditNotesCalls);
        Assert.Equal(new[] { "packageStaged", "packageValidated", "githubAssetAlreadyMatched", "githubNotesUpdated" }, published.Value!.CompletedPhases);
    }

    [Fact]
    public async Task Existing_different_asset_requires_confirmation_before_clobber()
    {
        var package = await BuildPackageAsync("1.0.0");
        var request = Request(package, "1.0.0");
        _github.Releases.Add(new GitHubRelease("v1.0.0", "v1.0.0", false, false));
        var assetUrl = GitHubService.BuildAssetUrl(request.SourceRepo, "v1.0.0", Path.GetFileName(package));
        _assets.Results[assetUrl.AbsoluteUri] = new PublishedAssetState(PublishedAssetStatus.Found, new string('f', 64));

        var preparedResult = await _workflow.PrepareAsync(request, CancellationToken.None);
        await using var prepared = Assert.IsType<PreparedRelease>(preparedResult.Value);
        Assert.True(prepared.Preview.ReplacesAsset);

        var refused = await _workflow.PublishAsync(prepared, request, confirmed: false, CancellationToken.None);
        Assert.Equal(WorkflowErrorKind.Conflict, refused.ErrorKind);
        Assert.Equal(0, _github.UploadCalls);

        var published = await _workflow.PublishAsync(prepared, request, confirmed: true, CancellationToken.None);
        Assert.Equal(WorkflowErrorKind.None, published.ErrorKind);
        Assert.Equal(1, _github.UploadCalls);
        Assert.True(_github.LastClobber);
        Assert.Equal(prepared.Sha256, _github.UploadedSha256);
    }

    [Fact]
    public async Task Prepare_refuses_private_repositories_and_unreadable_existing_assets()
    {
        var package = await BuildPackageAsync("1.0.0");
        var request = Request(package, "1.0.0");
        _github.IsPrivate = true;

        var privateResult = await _workflow.PrepareAsync(request, CancellationToken.None);
        Assert.Equal(WorkflowErrorKind.Validation, privateResult.ErrorKind);
        Assert.Null(privateResult.Value);

        _github.IsPrivate = false;
        _github.Releases.Add(new GitHubRelease("v1.0.0", "v1.0.0", false, false));
        var assetUrl = GitHubService.BuildAssetUrl(request.SourceRepo, "v1.0.0", Path.GetFileName(package));
        _assets.Results[assetUrl.AbsoluteUri] = new PublishedAssetState(PublishedAssetStatus.Unreadable, null);

        var unreadable = await _workflow.PrepareAsync(request, CancellationToken.None);
        Assert.Equal(WorkflowErrorKind.Conflict, unreadable.ErrorKind);
        Assert.Null(unreadable.Value);
    }

    [Fact]
    public async Task Prepared_release_is_a_private_copy_and_disposal_removes_it()
    {
        var package = await BuildPackageAsync("1.0.0");
        var sourceBytes = await File.ReadAllBytesAsync(package);
        var request = Request(package, "1.0.0") with { AssetFileName = "renamed.zip" };

        var result = await _workflow.PrepareAsync(request, CancellationToken.None);
        var prepared = Assert.IsType<PreparedRelease>(result.Value);
        var stagedPath = prepared.StagedPath;
        Assert.NotEqual(Path.GetFullPath(package), stagedPath);
        Assert.Equal("renamed.zip", Path.GetFileName(stagedPath));
        Assert.True(File.Exists(stagedPath));

        await prepared.DisposeAsync();

        Assert.False(File.Exists(stagedPath));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(package));
    }

    [Fact]
    public void Catalog_release_mutations_use_version_and_channel_identity_losslessly()
    {
        var catalog = new CatalogWorkflow();
        var original = CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogWorkflowTests.CatalogFixture.Clone(original)!;
        var added = CopyRelease(
            CatalogWorkflowTests.CatalogFixture.CompleteRelease(CatalogWorkflowTests.CatalogFixture.PrimaryGameId),
            version: "2.0.0",
            channel: "beta");

        var withAdded = catalog.AddRelease(original, CatalogWorkflowTests.CatalogFixture.PrimaryGameId, added);
        Assert.Equal(2, CatalogCommandSupportForTests.Releases(withAdded).Count);
        CatalogWorkflowTests.CatalogFixture.AssertJsonEquivalent(snapshot, original);

        var edited = CopyRelease(added, version: "2.0.1", notes: "Edited");
        var withEdited = catalog.EditRelease(
            withAdded,
            CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
            "2.0.0",
            "beta",
            edited);
        Assert.DoesNotContain(CatalogCommandSupportForTests.Releases(withEdited), r => r.Version == "2.0.0" && r.Channel == "beta");
        Assert.Contains(CatalogCommandSupportForTests.Releases(withEdited), r => r.Version == "2.0.1" && r.Channel == "beta");

        var removed = catalog.RemoveRelease(
            withEdited,
            CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
            "2.0.1",
            "beta");
        Assert.Single(CatalogCommandSupportForTests.Releases(removed));
    }

    private async Task<string> BuildPackageAsync(string version)
    {
        var source = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "reader.dll"), "accessible");
        var output = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        var logger = TestLogger.Create();
        var packages = new PackageWorkflow(
            new ManifestBuilderService(logger),
            new Sha256HashService(),
            logger);
        var result = await packages.BuildAsync(
            new PackageBuildRequest(
                source,
                output,
                PluginId,
                GameId,
                version,
                Array.Empty<Dependency>(),
                new LifecycleScriptInputs()),
            CancellationToken.None);
        return result.ZipPath;
    }

    private static ReleasePublishRequest Request(string package, string version) =>
        new(
            ProjectPath: "C:\\author-project",
            PluginId,
            GameId,
            Version: version,
            Channel: "stable",
            SourceRepo: "owner/repo",
            LocalZipPath: package,
            AssetFileName: null,
            Notes: "Release notes",
            ChangelogUrl: "https://example.com/changelog",
            Patreon: null);

    internal static ModRelease CopyRelease(
        ModRelease release,
        string? version = null,
        string? channel = null,
        string? notes = null) =>
        new()
        {
            GameId = release.GameId,
            PluginId = release.PluginId,
            Version = version ?? release.Version,
            Channel = channel ?? release.Channel,
            PackageUrl = release.PackageUrl,
            Sha256 = release.Sha256,
            ChangelogUrl = release.ChangelogUrl,
            Notes = notes ?? release.Notes,
            Compatibility = CatalogWorkflowTests.CatalogFixture.Clone(release.Compatibility),
            Patreon = CatalogWorkflowTests.CatalogFixture.Clone(release.Patreon)
        };

    private const string PluginId = "sample-plugin";
    private const string GameId = "sample-game";

    private static class CatalogCommandSupportForTests
    {
        public static List<ModRelease> Releases(PluginRepoIndex index) =>
            index.ReleasesByGameId[CatalogWorkflowTests.CatalogFixture.PrimaryGameId];
    }

    internal sealed class FakeGitHubService : IGitHubService
    {
        public bool Available { get; set; } = true;
        public bool Authenticated { get; set; } = true;
        public bool? IsPrivate { get; set; } = false;
        public List<GitHubRelease> Releases { get; } = [];
        public int CreateCalls { get; private set; }
        public int UploadCalls { get; private set; }
        public int EditNotesCalls { get; private set; }
        public bool LastClobber { get; private set; }
        public string? UploadedSha256 { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);
        public Task<bool> IsAuthenticatedAsync(CancellationToken ct = default) => Task.FromResult(Authenticated);
        public Task<bool?> IsRepoPrivateAsync(string nameWithOwner, CancellationToken ct = default) => Task.FromResult(IsPrivate);
        public Task<List<GitHubRepo>> ListReposAsync(int limit = 100, CancellationToken ct = default) => Task.FromResult(new List<GitHubRepo>());
        public Task<List<GitHubRelease>> ListReleasesAsync(string repo, int limit = 30, CancellationToken ct = default) =>
            Task.FromResult(Releases.ToList());

        public async Task<ProcessResult> CreateReleaseAsync(
            string repo,
            string tagName,
            string title,
            string? notes,
            IEnumerable<string> assetPaths,
            CancellationToken ct = default)
        {
            CreateCalls++;
            UploadedSha256 = await HashAssetAsync(assetPaths.Single(), ct);
            return new ProcessResult(0, "created", "");
        }

        public Task<ProcessResult> EditReleaseNotesAsync(
            string repo,
            string tagName,
            string notes,
            CancellationToken ct = default)
        {
            EditNotesCalls++;
            return Task.FromResult(new ProcessResult(0, "edited", ""));
        }

        public async Task<ProcessResult> UploadReleaseAssetAsync(
            string repo,
            string tagName,
            string assetPath,
            bool clobber,
            CancellationToken ct = default)
        {
            UploadCalls++;
            LastClobber = clobber;
            UploadedSha256 = await HashAssetAsync(assetPath, ct);
            return new ProcessResult(0, "uploaded", "");
        }

        private static async Task<string> HashAssetAsync(string path, CancellationToken ct) =>
            await new Sha256HashService().ComputeAsync(path, ct);
    }

    internal sealed class FakePublishedAssetProbe : IPublishedAssetProbe
    {
        public Dictionary<string, PublishedAssetState> Results { get; } = new(StringComparer.Ordinal);

        public Task<PublishedAssetState> ProbeAsync(Uri url, CancellationToken ct = default) =>
            Task.FromResult(
                Results.TryGetValue(url.AbsoluteUri, out var state)
                    ? state
                    : new PublishedAssetState(PublishedAssetStatus.Absent, null));
    }
}
