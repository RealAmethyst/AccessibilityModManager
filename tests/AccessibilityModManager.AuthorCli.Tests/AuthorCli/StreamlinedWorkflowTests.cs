using System.Text.Json;
using AccessibilityModManager.AuthorCli;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Services;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Authoring;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class StreamlinedWorkflowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-streamlined-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(root, "catalog");

    [Fact]
    public async Task Help_alias_lists_commands_and_has_no_server_or_admin_workflows()
    {
        var help = await Run("help");
        Assert.Equal(0, help.Exit);
        Assert.Contains("project", help.Output);
        Assert.DoesNotContain("server", help.Output);
        Assert.DoesNotContain("signing", help.Output);
        Assert.DoesNotContain("registry", help.Output);
        var alias = await Run("help", "package", "build");
        var option = await Run("package", "build", "--help");
        Assert.Equal(option.Output, alias.Output);
        Assert.Contains("--target-platform", alias.Output);
        Assert.Contains("--proton-config", alias.Output);
    }

    [Fact]
    public async Task Creating_a_repository_requires_confirmation_and_dry_run_leaves_no_project()
    {
        var preview = await Run("project", "create", "example/catalog", "--plugin-id", "example", "--project", Project, "--dry-run");
        Assert.Equal(0, preview.Exit);
        Assert.False(Directory.Exists(Project));
        var refusal = await Run("project", "create", "example/catalog", "--plugin-id", "example", "--project", Project);
        Assert.Equal((int)CliExitCode.Conflict, refusal.Exit);
        Assert.False(Directory.Exists(Project));
    }

    [Theory]
    [InlineData("http://github.com/example/repo")]
    [InlineData("https://github.com/example/repo/releases")]
    [InlineData("https://user:password@github.com/example/repo")]
    [InlineData("--template/repo")]
    [InlineData("example/../repo")]
    public void Invalid_repository_inputs_are_rejected(string value) =>
        Assert.Throws<InvalidOperationException>(() => GitHubService.NormalizeRepository(value));

    [Theory]
    [InlineData("example/repo")]
    [InlineData("https://github.com/example/repo.git")]
    [InlineData("git@github.com:example/repo.git")]
    public void Repository_forms_normalize_to_the_same_identity(string value) =>
        Assert.Equal("example/repo", GitHubService.NormalizeRepository(value));

    [Fact]
    public async Task Per_game_repository_is_saved_without_changing_catalog_bytes()
    {
        var init = await Run("project", "init", "example", "--project", Project);
        Assert.Equal(0, init.Exit);
        Assert.Equal(0, (await Run("game", "add", "--id", "game", "--display-name", "Game", "--project", Project)).Exit);
        var before = File.ReadAllBytes(Path.Combine(Project, "index.json"));
        Assert.Equal(0, (await Run("game", "repo", "game", "--repo", "example/game", "--project", Project, "--dry-run")).Exit);
        Assert.Null(new AuthorConfigService(TestLogger.Create(), Path.Combine(root, "config")).GetGameSourceRepo(Project, "game"));
        Assert.Equal(0, (await Run("game", "repo", "game", "--repo", "example/game", "--project", Project)).Exit);
        var shown = await Run("game", "repo", "game", "--project", Project);
        Assert.Contains("example/game", shown.Output);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Project, "index.json")));
    }

    [Fact]
    public async Task Package_build_preserves_an_existing_output_file()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "reader.dll"), "payload");
        var output = Path.Combine(root, "existing.zip");
        File.WriteAllText(output, "previous build");
        var logger = TestLogger.Create();
        var workflow = new PackageWorkflow(new ManifestBuilderService(logger), new Sha256HashService(), logger);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.BuildAsync(
            new PackageBuildRequest(source, output, "example", "game", "1.0", [], new LifecycleScriptInputs()), CancellationToken.None));
        Assert.Equal("previous build", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("xivlauncher")]
    public async Task Linux_package_target_survives_validation_and_release_preparation(string target)
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "reader.dll"), "payload");
        if (target == "xivlauncher")
            File.WriteAllText(Path.Combine(source, "reader.json"), "{\"InternalName\":\"Reader\"}");
        var logger = TestLogger.Create();
        var workflow = new PackageWorkflow(new ManifestBuilderService(logger), new Sha256HashService(), logger);
        var package = await workflow.BuildAsync(new PackageBuildRequest(source, Path.Combine(root, "linux.zip"),
            "example", "game", "1.0", [], new LifecycleScriptInputs(), TargetPlatform: target,
            XivLauncher: target == "xivlauncher" ? new XivLauncherConfig
            {
                PluginAssembly = "reader.dll", InternalName = "Reader",
                WorkingPluginId = "507b48de-3362-4471-86f4-baa7e56d9387"
            } : null), CancellationToken.None);
        Assert.True(package.Validation.IsValid);
        var release = new ReleaseWorkflow(new ReleaseWorkflowTests.FakeGitHubService(), new ReleaseWorkflowTests.FakePublishedAssetProbe(), logger);
        var request = new ReleasePublishRequest(Project, "example", "game", "1.0", "stable", "example/game", package.ZipPath, null, null, null, null);
        var preview = await release.PreviewAsync(request, CancellationToken.None);
        Assert.Equal(target, preview.Value!.TargetPlatform);
        var prepared = await release.PrepareAsync(request, CancellationToken.None);
        await using var staged = prepared.Value!;
        Assert.Equal(target, staged.Preview.TargetPlatform);
        var published = await release.PublishAsync(staged, request, true, CancellationToken.None);
        Assert.Equal(target, published.Value!.Release.TargetPlatform);
    }

    [Theory]
    [InlineData("placeholder", "https://example.com/loader.zip")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "http://example.com/loader.zip")]
    public async Task Incomplete_dependency_downloads_are_blocked_before_publication(string hash, string url)
    {
        await Run("project", "init", "example", "--project", Project);
        await Run("game", "add", "--id", "game", "--display-name", "Game", "--project", Project);
        var dependency = new Dependency
        {
            Id = "loader", Type = "framework", Check = new DependencyCheck { FilePath = "loader.dll" },
            Fix = new DependencyFix { DownloadUrl = url, AutoInstall = new ExtractZipAutoInstall { Sha256 = hash } }
        };
        var input = Path.Combine(root, "dependency.json");
        File.WriteAllText(input, JsonSerializer.Serialize(dependency,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal(0, (await Run("dependency", "set", "game", "--input", input, "--project", Project)).Exit);
        Assert.Equal((int)CliExitCode.Validation, (await Run("index", "validate", "--project", Project)).Exit);

        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "reader.dll"), "payload");
        var output = Path.Combine(root, "invalid.zip");
        var logger = TestLogger.Create();
        var workflow = new PackageWorkflow(new ManifestBuilderService(logger), new Sha256HashService(), logger);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.BuildAsync(
            new PackageBuildRequest(source, output, "example", "game", "1.0", [dependency], new LifecycleScriptInputs()),
            CancellationToken.None));
        Assert.Contains("loader", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Editing_a_script_preserves_Linux_detection_metadata()
    {
        var game = new GameDefinition
        {
            GameId = "game", DisplayName = "Game", LinuxSteamAppId = "42", LinuxExeName = "game-linux",
            LinuxProbeRules = [new PathProbeRule { Type = "fileExists", RelativePath = "game-linux" }]
        };
        var index = new PluginRepoIndex { PluginId = "example", RepoVersion = "1", GeneratedAt = DateTime.UtcNow, Games = [game], ReleasesByGameId = [] };
        var result = new CatalogWorkflow().SetLifecycleScript(index, "game", LifecycleSlot.PostInstall,
            new LifecycleScript { Executable = "setup.ps1", What = "Configure", Why = "Needed", Modifies = "config" });
        Assert.Equal("42", result.Games[0].LinuxSteamAppId);
        Assert.Equal("game-linux", result.Games[0].LinuxExeName);
        Assert.Equal("game-linux", Assert.Single(result.Games[0].LinuxProbeRules).RelativePath);
    }

    [Fact]
    public async Task Misspelled_JSON_properties_are_reported_instead_of_silently_ignored()
    {
        using var input = new StringReader("""{"displayNmae":"Example"}""");
        await Assert.ThrowsAsync<JsonException>(() => new JsonPayloadService().ReadAsync<PluginAuthorInfo>("-", input, CancellationToken.None));
    }

    private async Task<(int Exit, string Output, string Error)> Run(params string[] args)
    {
        using var input = new StringReader("");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var services = CliServices.Create(new CliServiceOverrides(
            Console: new CliConsole(input, output, error, isInputRedirected: true), Logger: TestLogger.Create(),
            AuthorConfigDirectory: Path.Combine(root, "config"), GitHubService: new ReleaseWorkflowTests.FakeGitHubService()));
        var exit = await Program.RunAsync(args, services);
        return (exit, output.ToString(), error.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
