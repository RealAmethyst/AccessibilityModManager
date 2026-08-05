using System.Text.Json;
using AccessibilityModManager.AuthorCli;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Authoring;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class ReleaseCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly IndexFileService _indexFiles = new(TestLogger.Create());
    private readonly ReleaseWorkflowTests.FakeGitHubService _github = new();
    private readonly ReleaseWorkflowTests.FakePublishedAssetProbe _assets = new();

    public ReleaseCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "amm-release-cli-" + Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(_root);
        _indexFiles.Save(_project, CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Release_add_edit_and_remove_use_input_models_and_original_identity()
    {
        var added = ReleaseWorkflowTests.CopyRelease(
            CatalogWorkflowTests.CatalogFixture.CompleteRelease(CatalogWorkflowTests.CatalogFixture.PrimaryGameId),
            version: "2.0.0",
            channel: "beta");
        var addInput = WriteJson(added);
        var add = await InvokeAsync(
            ProjectArgs("release", "add", CatalogWorkflowTests.CatalogFixture.PrimaryGameId, "--input", addInput));
        Assert.Equal((int)CliExitCode.Success, add.ExitCode);

        var edited = ReleaseWorkflowTests.CopyRelease(added, version: "2.0.1", notes: "Edited notes");
        var editInput = WriteJson(edited);
        var edit = await InvokeAsync(
            ProjectArgs(
                "release", "edit",
                CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "2.0.0", "beta",
                "--input", editInput));
        Assert.Equal((int)CliExitCode.Success, edit.ExitCode);
        var releases = _indexFiles.Load(_project).ReleasesByGameId[CatalogWorkflowTests.CatalogFixture.PrimaryGameId];
        Assert.Contains(releases, r => r.Version == "2.0.1" && r.Channel == "beta");
        Assert.DoesNotContain(releases, r => r.Version == "2.0.0" && r.Channel == "beta");

        var remove = await InvokeAsync(
            ProjectArgs(
                "--yes",
                "release", "remove",
                CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "2.0.1", "beta"));
        Assert.Equal((int)CliExitCode.Success, remove.ExitCode);
        Assert.DoesNotContain(
            _indexFiles.Load(_project).ReleasesByGameId[CatalogWorkflowTests.CatalogFixture.PrimaryGameId],
            r => r.Version == "2.0.1" && r.Channel == "beta");
    }

    [Fact]
    public async Task Release_upload_uses_fake_GitHub_and_does_not_save_the_catalog()
    {
        var before = await File.ReadAllBytesAsync(Path.Combine(_project, "index.json"));
        var package = await BuildPackageAsync();

        var run = await InvokeAsync(
            ProjectArgs(
                "release", "upload",
                "--game", CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "--version", "1.0.0",
                "--channel", "stable",
                "--repo", "owner/repo",
                "--zip", package,
                "--asset-name", "ff7-accessibility.zip",
                "--notes", "Release notes"));

        Assert.Equal((int)CliExitCode.Success, run.ExitCode);
        Assert.Equal(1, _github.CreateCalls);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(_project, "index.json")));
        using var document = JsonDocument.Parse(run.Stdout);
        Assert.Equal(
            _github.UploadedSha256,
            document.RootElement.GetProperty("value").GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Release_publish_is_registered_but_reports_the_missing_index_phase()
    {
        var run = await InvokeAsync(
            ProjectArgs(
                "release", "publish",
                "--game", CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "--version", "1.0.0",
                "--channel", "stable",
                "--repo", "owner/repo",
                "--zip", "missing.zip"));

        Assert.Equal((int)CliExitCode.Conflict, run.ExitCode);
        Assert.Contains("index publication", run.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _github.CreateCalls);
    }

    [Fact]
    public async Task Github_status_repos_and_releases_are_available_without_touching_real_GitHub()
    {
        _github.Releases.Add(new GitHubRelease("v1.0.0", "Release", false, false));

        var status = await InvokeAsync("--json", "--quiet", "github", "status");
        var repos = await InvokeAsync("--json", "--quiet", "github", "repos");
        var releases = await InvokeAsync("--json", "--quiet", "github", "releases", "--repo", "owner/repo");

        Assert.Equal((int)CliExitCode.Success, status.ExitCode);
        Assert.Equal((int)CliExitCode.Success, repos.ExitCode);
        Assert.Equal((int)CliExitCode.Success, releases.ExitCode);
        Assert.Contains("v1.0.0", releases.Stdout, StringComparison.Ordinal);
    }

    private async Task<string> BuildPackageAsync()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "reader.dll"), "accessible");
        var output = Path.Combine(_root, "release.zip");
        var logger = TestLogger.Create();
        var packages = new PackageWorkflow(new ManifestBuilderService(logger), new Sha256HashService(), logger);
        var built = await packages.BuildAsync(
            new PackageBuildRequest(
                source,
                output,
                CatalogWorkflowTests.CatalogFixture.PluginId,
                CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "1.0.0",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex().Games[0].Dependencies,
                new LifecycleScriptInputs()),
            CancellationToken.None);
        return built.ZipPath;
    }

    private string WriteJson(object value)
    {
        var path = Path.Combine(_root, "input-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, CatalogWorkflowTests.CatalogFixture.Serialize(value));
        return path;
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
            AuthorConfigDirectory: Path.Combine(_root, "config"),
            LogDirectory: Path.Combine(_root, "logs"),
            GitHubService: _github,
            PublishedAssetProbe: _assets));

        var exitCode = await Program.RunAsync(args, services);
        return new CliRunResult(exitCode, output.ToString(), error.ToString());
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
