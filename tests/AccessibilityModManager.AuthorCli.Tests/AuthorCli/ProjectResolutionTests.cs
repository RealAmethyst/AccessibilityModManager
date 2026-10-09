using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class ProjectResolutionTests : IDisposable
{
    private readonly string _root;
    private readonly string _configDirectory;

    public ProjectResolutionTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "amm-authorcli-projects-" + Guid.NewGuid().ToString("N"));
        _configDirectory = Path.Combine(_root, "config");

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_configDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task Explicit_path_wins_over_current_directory_and_saved_project()
    {
        var explicitProject = CreateProject("explicit-project", "sample");
        var currentProject = CreateProject("current-project", "current");
        var savedProject = CreateProject("saved-project", "saved");

        CreateConfigService().RecordRecent(savedProject);

        var context = CreateContext();

        var resolved = await context.ResolveAsync(
            explicitProject,
            currentProject,
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(explicitProject), resolved.ProjectPath);
        Assert.Equal("sample", resolved.Index.PluginId);
    }

    [Fact]
    public async Task Current_directory_wins_when_no_explicit_path_is_given()
    {
        var currentProject = CreateProject("current-project", "sample");
        var savedProject = CreateProject("saved-project", "saved");

        CreateConfigService().RecordRecent(savedProject);

        var context = CreateContext();

        var resolved = await context.ResolveAsync(
            explicitPath: null,
            currentDirectory: currentProject,
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(currentProject), resolved.ProjectPath);
        Assert.Equal("sample", resolved.Index.PluginId);
    }

    [Fact]
    public async Task Last_opened_project_is_used_when_current_directory_is_not_a_project()
    {
        var savedProject = CreateProject("saved-project", "sample");
        var notAProject = Path.Combine(_root, "not-a-project");
        Directory.CreateDirectory(notAProject);

        CreateConfigService().RecordRecent(savedProject);

        var context = CreateContext();

        var resolved = await context.ResolveAsync(
            explicitPath: null,
            currentDirectory: notAProject,
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(savedProject), resolved.ProjectPath);
        Assert.Equal("sample", resolved.Index.PluginId);
    }

    [Fact]
    public async Task Resolving_without_any_project_source_fails()
    {
        var notAProject = Path.Combine(_root, "not-a-project");
        Directory.CreateDirectory(notAProject);

        var context = CreateContext();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.ResolveAsync(
                explicitPath: null,
                currentDirectory: notAProject,
                CancellationToken.None));

        Assert.Contains("project", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_explicit_project_folder_without_index_json_fails()
    {
        var projectFolder = Path.Combine(_root, "missing-index");
        var currentDirectory = Path.Combine(_root, "unused");
        Directory.CreateDirectory(projectFolder);
        Directory.CreateDirectory(currentDirectory);

        var context = CreateContext();

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            context.ResolveAsync(
                explicitPath: projectFolder,
                currentDirectory: currentDirectory,
                CancellationToken.None));

        Assert.Contains("index.json", ex.Message, StringComparison.Ordinal);
    }

    private AuthorProjectContext CreateContext() =>
        new(CreateConfigService(), new IndexFileService(TestLogger.Create()));

    private AuthorConfigService CreateConfigService() =>
        new(TestLogger.Create(), _configDirectory);

    private string CreateProject(string folderName, string pluginId)
    {
        var projectPath = Path.Combine(_root, folderName);
        Directory.CreateDirectory(projectPath);

        var indexFiles = new IndexFileService(TestLogger.Create());
        indexFiles.Save(projectPath, indexFiles.CreateStarter(pluginId));

        return projectPath;
    }
}
