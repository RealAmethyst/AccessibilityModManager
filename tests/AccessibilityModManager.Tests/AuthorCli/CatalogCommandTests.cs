using System.Text.Json;
using AccessibilityModManager.AuthorCli;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Authoring;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class CatalogCommandTests : IDisposable
{
    private readonly IndexFileService _indexFiles = new(TestLogger.Create());
    private readonly string _root;

    public CatalogCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "amm-catalog-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Project_init_creates_a_starter_index_and_dry_run_is_non_durable()
    {
        var dryRunProject = Path.Combine(_root, "dry-run-project");
        var dryRun = await InvokeAsync(
            _root,
            string.Empty,
            WithProject(dryRunProject, "--dry-run", "project", "init", "sample-plugin"));

        Assert.Equal((int)CliExitCode.Success, dryRun.ExitCode);
        AssertSuccessOutcome(dryRun.Stdout);
        Assert.False(File.Exists(IndexPath(dryRunProject)));
        AssertNoLock(dryRunProject);

        var projectPath = Path.Combine(_root, "real-project");
        var before = DateTime.UtcNow;
        var created = await InvokeAsync(
            _root,
            string.Empty,
            WithProject(projectPath, "project", "init", "sample-plugin"));
        var after = DateTime.UtcNow;

        Assert.Equal((int)CliExitCode.Success, created.ExitCode);
        AssertSuccessOutcome(created.Stdout);
        var saved = LoadIndex(projectPath);
        AssertSavedIndexMatches(
            CatalogWorkflowTests.CatalogFixture.CreateStarterIndex("sample-plugin"),
            saved,
            before,
            after);
    }

    [Fact]
    public async Task Project_status_reports_the_resolved_project_without_creating_a_lock()
    {
        var index = CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex();
        var projectPath = CreateProject(index);
        var beforeBytes = File.ReadAllBytes(IndexPath(projectPath));

        var run = await InvokeAsync(projectPath, string.Empty, WithProject(projectPath, "project", "status"));

        Assert.Equal((int)CliExitCode.Success, run.ExitCode);
        AssertSuccessOutcome(run.Stdout);
        Assert.True(
            run.Stdout.Contains(index.PluginId, StringComparison.Ordinal) ||
            run.Stdout.Contains(projectPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(beforeBytes, File.ReadAllBytes(IndexPath(projectPath)));
        AssertNoLock(projectPath);
    }

    [Fact]
    public async Task Project_dry_run_commands_do_not_touch_config_git_or_the_project()
    {
        var projectPath = CreateProject(CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex());
        var beforeBytes = File.ReadAllBytes(IndexPath(projectPath));
        var configPath = Path.Combine(_root, "author-config", "config.json");

        var open = await InvokeAsync(
            projectPath,
            string.Empty,
            WithProject(projectPath, "--dry-run", "project", "open"));

        Assert.Equal((int)CliExitCode.Success, open.ExitCode);
        AssertSuccessOutcome(open.Stdout);
        Assert.False(File.Exists(configPath));

        var cloneTarget = Path.Combine(_root, "clone-preview");
        var clone = await InvokeAsync(
            _root,
            string.Empty,
            WithProject(cloneTarget, "--dry-run", "project", "clone", "owner/repo"));

        Assert.Equal((int)CliExitCode.Success, clone.ExitCode);
        AssertSuccessOutcome(clone.Stdout);
        Assert.False(Directory.Exists(cloneTarget));

        var pull = await InvokeAsync(
            projectPath,
            string.Empty,
            WithProject(projectPath, "--dry-run", "project", "pull"));

        Assert.Equal((int)CliExitCode.Success, pull.ExitCode);
        AssertSuccessOutcome(pull.Stdout);
        Assert.Equal(beforeBytes, File.ReadAllBytes(IndexPath(projectPath)));
        AssertNoLock(projectPath);
    }

    [Theory]
    [MemberData(nameof(InputCommandCases))]
    public async Task Input_backed_commands_accept_file_and_stdin(InputCommandCase testCase)
    {
        var initial = testCase.CreateInitialIndex();
        var projectPath = CreateProject(initial);
        var (sourceToken, stdin) = PreparePayload(testCase.PayloadSource, testCase.Payload);
        var before = DateTime.UtcNow;

        var run = await InvokeAsync(projectPath, stdin, testCase.BuildArgs(projectPath, sourceToken!));

        var after = DateTime.UtcNow;
        Assert.Equal((int)CliExitCode.Success, run.ExitCode);
        AssertSuccessOutcome(run.Stdout);
        AssertSavedIndexMatches(testCase.BuildExpected(initial), LoadIndex(projectPath), before, after);
    }

    [Fact]
    public async Task Game_add_accepts_field_flags_and_rejects_mixing_them_with_input()
    {
        var initial = CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex();
        var projectPath = CreateProject(initial);
        var before = DateTime.UtcNow;

        var success = await InvokeAsync(
            projectPath,
            string.Empty,
            WithProject(
                projectPath,
                "game",
                "add",
                "--id", CatalogWorkflowTests.CatalogFixture.FlagDrivenGame().GameId,
                "--display-name", CatalogWorkflowTests.CatalogFixture.FlagDrivenGame().DisplayName,
                "--mod-name", CatalogWorkflowTests.CatalogFixture.FlagDrivenGame().ModName!,
                "--description", CatalogWorkflowTests.CatalogFixture.FlagDrivenGame().Description!,
                "--steam-app-id", CatalogWorkflowTests.CatalogFixture.FlagDrivenGame().SteamAppId!,
                "--exe-name", CatalogWorkflowTests.CatalogFixture.FlagDrivenGame().ExeName!,
                "--tag", "screen-reader",
                "--tag", "completable",
                "--language", "en",
                "--language", "fr"));

        var after = DateTime.UtcNow;
        Assert.Equal((int)CliExitCode.Success, success.ExitCode);
        AssertSuccessOutcome(success.Stdout);
        AssertSavedIndexMatches(
            CatalogWorkflowTests.CatalogFixture.WithGameAdded(initial, CatalogWorkflowTests.CatalogFixture.FlagDrivenGame()),
            LoadIndex(projectPath),
            before,
            after);

        var conflictProject = CreateProject(initial, "conflict-project");
        var beforeBytes = File.ReadAllBytes(IndexPath(conflictProject));
        var inputGame = CatalogWorkflowTests.CatalogFixture.CompleteGame("mixing-input", "Mixing Input");
        var (sourceToken, stdin) = PreparePayload(PayloadSource.File, inputGame);

        var usage = await InvokeAsync(
            conflictProject,
            stdin,
            WithProject(
                conflictProject,
                "game",
                "add",
                "--input", sourceToken!,
                "--id", "illegal-extra-flag"));

        Assert.Equal((int)CliExitCode.Usage, usage.ExitCode);
        AssertErrorOutcome(usage.Stderr, WorkflowErrorKind.Usage);
        Assert.Equal(beforeBytes, File.ReadAllBytes(IndexPath(conflictProject)));
        AssertNoLock(conflictProject);
    }

    [Fact]
    public async Task Game_update_refuses_a_safe_rename_that_would_make_published_releases_mismatch()
    {
        var initial = CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex();
        var projectPath = CreateProject(initial);
        var beforeBytes = File.ReadAllBytes(IndexPath(projectPath));
        var renamed = CatalogWorkflowTests.CatalogFixture.CompleteGame(
            CatalogWorkflowTests.CatalogFixture.RenamedPrimaryGameId,
            "Final Fantasy VII Reborn");
        var (sourceToken, stdin) = PreparePayload(PayloadSource.File, renamed);

        var run = await InvokeAsync(
            projectPath,
            stdin,
            WithProject(
                projectPath,
                "--yes",
                "game",
                "update",
                CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "--input", sourceToken!));

        Assert.Equal((int)CliExitCode.Conflict, run.ExitCode);
        AssertErrorOutcome(run.Stderr, WorkflowErrorKind.Conflict);
        Assert.Equal(beforeBytes, File.ReadAllBytes(IndexPath(projectPath)));
    }

    [Fact]
    public async Task Game_update_can_explicitly_rewrite_release_game_ids()
    {
        var initial = CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex();
        var projectPath = CreateProject(initial);
        var renamed = CatalogWorkflowTests.CatalogFixture.CompleteGame(
            CatalogWorkflowTests.CatalogFixture.RenamedPrimaryGameId,
            "Final Fantasy VII Reborn");
        var (sourceToken, stdin) = PreparePayload(PayloadSource.File, renamed);
        var before = DateTime.UtcNow;

        var run = await InvokeAsync(
            projectPath,
            stdin,
            WithProject(
                projectPath,
                "--yes",
                "game",
                "update",
                CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                "--rewrite-release-game-id",
                "--input", sourceToken!));

        var after = DateTime.UtcNow;
        Assert.Equal((int)CliExitCode.Success, run.ExitCode);
        AssertSuccessOutcome(run.Stdout);
        AssertSavedIndexMatches(
            CatalogWorkflowTests.CatalogFixture.WithGameUpdated(
                initial,
                CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                renamed,
                rewriteReleaseGameIds: true),
            LoadIndex(projectPath),
            before,
            after);
    }

    [Theory]
    [MemberData(nameof(RemovalCommandCases))]
    public async Task Remove_and_clear_commands_apply_expected_mutations(MutationCommandCase testCase)
    {
        var initial = testCase.CreateInitialIndex();
        var projectPath = CreateProject(initial);
        var before = DateTime.UtcNow;

        var run = await InvokeAsync(projectPath, string.Empty, testCase.BuildArgs(projectPath, null));

        var after = DateTime.UtcNow;
        Assert.Equal((int)CliExitCode.Success, run.ExitCode);
        AssertSuccessOutcome(run.Stdout);
        AssertSavedIndexMatches(testCase.BuildExpected(initial), LoadIndex(projectPath), before, after);
    }

    [Theory]
    [MemberData(nameof(DryRunMutationCases))]
    public async Task Mutating_commands_honour_dry_run_without_rewriting_index_or_creating_lock(DryRunCommandCase testCase)
    {
        var initial = testCase.CreateInitialIndex();
        var projectPath = CreateProject(initial);
        var beforeBytes = File.ReadAllBytes(IndexPath(projectPath));
        var (sourceToken, stdin) = PreparePayload(PayloadSource.File, testCase.Payload);

        var run = await InvokeAsync(projectPath, stdin, testCase.BuildArgs(projectPath, sourceToken));

        Assert.Equal((int)CliExitCode.Success, run.ExitCode);
        AssertSuccessOutcome(run.Stdout);
        Assert.Equal(beforeBytes, File.ReadAllBytes(IndexPath(projectPath)));
        AssertNoLock(projectPath);
    }

    public static IEnumerable<object[]> InputCommandCases()
    {
        foreach (var source in new[] { PayloadSource.File, PayloadSource.Stdin })
        {
            var author = CatalogWorkflowTests.CatalogFixture.AlternateAuthor();
            yield return new object[]
            {
                new InputCommandCase(
                    $"author set ({source})",
                    source,
                    CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                    author,
                    (project, input) => WithProject(project, "author", "set", "--input", input),
                    index => CatalogWorkflowTests.CatalogFixture.WithAuthor(index, author))
            };

            var addedGame = CatalogWorkflowTests.CatalogFixture.CompleteGame(
                CatalogWorkflowTests.CatalogFixture.AddedGameId,
                "Knights of the Old Republic");
            yield return new object[]
            {
                new InputCommandCase(
                    $"game add ({source})",
                    source,
                    CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                    addedGame,
                    (project, input) => WithProject(project, "game", "add", "--input", input),
                    index => CatalogWorkflowTests.CatalogFixture.WithGameAdded(index, addedGame))
            };

            var updatedGame = CatalogWorkflowTests.CatalogFixture.CompleteGame(
                CatalogWorkflowTests.CatalogFixture.SecondaryGameId,
                "Resident Evil 4 HD");
            yield return new object[]
            {
                new InputCommandCase(
                    $"game update ({source})",
                    source,
                    CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                    updatedGame,
                    (project, input) => WithProject(
                        project,
                        "game",
                        "update",
                        CatalogWorkflowTests.CatalogFixture.SecondaryGameId,
                        "--input",
                        input),
                    index => CatalogWorkflowTests.CatalogFixture.WithGameUpdated(
                        index,
                        CatalogWorkflowTests.CatalogFixture.SecondaryGameId,
                        updatedGame))
            };

            var dependency = CatalogWorkflowTests.CatalogFixture.CompleteDependency(
                "RUNTIME-INSTALLER",
                CatalogWorkflowTests.CatalogFixture.CompleteRunInstallerAutoInstall(),
                CatalogWorkflowTests.CatalogFixture.CompleteGitHubReleaseAssetVersionDiscovery());
            yield return new object[]
            {
                new InputCommandCase(
                    $"dependency set ({source})",
                    source,
                    CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                    dependency,
                    (project, input) => WithProject(
                        project,
                        "dependency",
                        "set",
                        CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                        "--input",
                        input),
                    index => CatalogWorkflowTests.CatalogFixture.WithDependencyUpserted(
                        index,
                        CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                        dependency))
            };

            var script = CatalogWorkflowTests.CatalogFixture.ReplacementLifecycleScript();
            yield return new object[]
            {
                new InputCommandCase(
                    $"script set ({source})",
                    source,
                    CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                    script,
                    (project, input) => WithProject(
                        project,
                        "script",
                        "set",
                        CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                        SlotToken(LifecycleSlot.PreInstall),
                        "--input",
                        input),
                    index => CatalogWorkflowTests.CatalogFixture.WithLifecycleScript(
                        index,
                        CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                        LifecycleSlot.PreInstall,
                        script))
            };
        }
    }

    public static IEnumerable<object[]> RemovalCommandCases()
    {
        yield return new object[]
        {
            new MutationCommandCase(
                "game remove",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                null,
                (project, _) => WithProject(project, "--yes", "game", "remove", CatalogWorkflowTests.CatalogFixture.PrimaryGameId),
                index => CatalogWorkflowTests.CatalogFixture.WithGameRemoved(index, CatalogWorkflowTests.CatalogFixture.PrimaryGameId))
        };

        yield return new object[]
        {
            new MutationCommandCase(
                "dependency remove",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                null,
                (project, _) => WithProject(
                    project,
                    "dependency",
                    "remove",
                    CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                    "copy-helper"),
                index => CatalogWorkflowTests.CatalogFixture.WithDependencyRemoved(
                    index,
                    CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                    "copy-helper"))
        };

        yield return new object[]
        {
            new MutationCommandCase(
                "script clear",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                null,
                (project, _) => WithProject(
                    project,
                    "script",
                    "clear",
                    CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                    SlotToken(LifecycleSlot.PreInstall)),
                index => CatalogWorkflowTests.CatalogFixture.WithLifecycleScript(
                    index,
                    CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                    LifecycleSlot.PreInstall,
                    null))
        };
    }

    public static IEnumerable<object[]> DryRunMutationCases()
    {
        var author = CatalogWorkflowTests.CatalogFixture.AlternateAuthor();
        yield return new object[]
        {
            new DryRunCommandCase(
                "author set",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                author,
                (project, input) => WithProject(project, "--dry-run", "author", "set", "--input", input!))
        };

        var addedGame = CatalogWorkflowTests.CatalogFixture.CompleteGame(
            CatalogWorkflowTests.CatalogFixture.AddedGameId,
            "Knights of the Old Republic");
        yield return new object[]
        {
            new DryRunCommandCase(
                "game add",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                addedGame,
                (project, input) => WithProject(project, "--dry-run", "game", "add", "--input", input!))
        };

        var updatedGame = CatalogWorkflowTests.CatalogFixture.CompleteGame(
            CatalogWorkflowTests.CatalogFixture.SecondaryGameId,
            "Resident Evil 4 HD");
        yield return new object[]
        {
            new DryRunCommandCase(
                "game update",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                updatedGame,
                (project, input) => WithProject(
                    project,
                    "--dry-run",
                    "game",
                    "update",
                    CatalogWorkflowTests.CatalogFixture.SecondaryGameId,
                    "--input",
                    input!))
        };

        yield return new object[]
        {
            new DryRunCommandCase(
                "game remove",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                null,
                (project, _) => WithProject(project, "--dry-run", "--yes", "game", "remove", CatalogWorkflowTests.CatalogFixture.PrimaryGameId))
        };

        var dependency = CatalogWorkflowTests.CatalogFixture.CompleteDependency(
            "RUNTIME-INSTALLER",
            CatalogWorkflowTests.CatalogFixture.CompleteRunInstallerAutoInstall(),
            CatalogWorkflowTests.CatalogFixture.CompleteGitHubReleaseAssetVersionDiscovery());
        yield return new object[]
        {
            new DryRunCommandCase(
                "dependency set",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                dependency,
                (project, input) => WithProject(
                    project,
                    "--dry-run",
                    "dependency",
                    "set",
                    CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                    "--input",
                    input!))
        };

        yield return new object[]
        {
            new DryRunCommandCase(
                "dependency remove",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                null,
                (project, _) => WithProject(
                    project,
                    "--dry-run",
                    "dependency",
                    "remove",
                    CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                    "copy-helper"))
        };

        var script = CatalogWorkflowTests.CatalogFixture.ReplacementLifecycleScript();
        yield return new object[]
        {
            new DryRunCommandCase(
                "script set",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                script,
                (project, input) => WithProject(
                    project,
                    "--dry-run",
                    "script",
                    "set",
                    CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                    SlotToken(LifecycleSlot.PreInstall),
                    "--input",
                    input!))
        };

        yield return new object[]
        {
            new DryRunCommandCase(
                "script clear",
                CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex,
                null,
                (project, _) => WithProject(
                    project,
                    "--dry-run",
                    "script",
                    "clear",
                    CatalogWorkflowTests.CatalogFixture.PrimaryGameId,
                    SlotToken(LifecycleSlot.PreInstall)))
        };
    }

    private string CreateProject(PluginRepoIndex index, string? folderName = null)
    {
        var projectPath = Path.Combine(_root, folderName ?? Guid.NewGuid().ToString("N"));
        _indexFiles.Save(projectPath, CatalogWorkflowTests.CatalogFixture.Clone(index)!);
        return projectPath;
    }

    private PluginRepoIndex LoadIndex(string projectPath) => _indexFiles.Load(projectPath);

    private static string IndexPath(string projectPath) => Path.Combine(projectPath, "index.json");

    private static string LockPath(string projectPath) => Path.Combine(projectPath, ".amm-author.lock");

    private static string[] WithProject(string projectPath, params string[] tail) =>
        ["--project", projectPath, "--json", "--quiet", .. tail];

    private (string? SourceToken, string Stdin) PreparePayload(PayloadSource source, object? payload)
    {
        if (payload is null)
            return (null, string.Empty);

        var json = CatalogWorkflowTests.CatalogFixture.Serialize(payload);
        if (source == PayloadSource.Stdin)
            return ("-", json);

        var file = Path.Combine(_root, "payload-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, json);
        return (file, string.Empty);
    }

    private static string SlotToken(LifecycleSlot slot) => slot switch
    {
        LifecycleSlot.PreInstall => "pre-install",
        LifecycleSlot.PostInstall => "post-install",
        LifecycleSlot.PostUninstall => "post-uninstall",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null)
    };

    private static void AssertSavedIndexMatches(
        PluginRepoIndex expected,
        PluginRepoIndex actual,
        DateTime startedUtc,
        DateTime finishedUtc)
    {
        Assert.InRange(actual.GeneratedAt, startedUtc.AddSeconds(-1), finishedUtc.AddSeconds(1));
        CatalogWorkflowTests.CatalogFixture.AssertJsonEquivalent(
            CatalogWorkflowTests.CatalogFixture.WithGeneratedAt(expected, actual.GeneratedAt),
            actual);
    }

    private static void AssertSuccessOutcome(string stdout)
    {
        Assert.False(string.IsNullOrWhiteSpace(stdout));
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.True(document.RootElement.TryGetProperty("status", out _));
        Assert.False(document.RootElement.TryGetProperty("errorKind", out _));
    }

    private static void AssertErrorOutcome(string stderr, WorkflowErrorKind expectedKind)
    {
        Assert.False(string.IsNullOrWhiteSpace(stderr));
        using var document = JsonDocument.Parse(stderr);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal(ToCamelCase(expectedKind), document.RootElement.GetProperty("errorKind").GetString());
    }

    private static string ToCamelCase(WorkflowErrorKind value)
    {
        var text = value.ToString();
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    private static void AssertNoLock(string projectPath) => Assert.False(File.Exists(LockPath(projectPath)));

    private async Task<CliRunResult> InvokeAsync(string _, string stdin, string[] args)
    {
        using var stdinReader = new StringReader(stdin);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var console = new TestCliConsole(stdinReader, stdout, stderr);
        using var services = CliServices.Create(new CliServiceOverrides(
            Console: console,
            Logger: TestLogger.Create(),
            AuthorConfigDirectory: Path.Combine(_root, "author-config"),
            LogDirectory: Path.Combine(_root, "logs")));

        var exitCode = await Program.RunAsync(args, services);
        return new CliRunResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed class TestCliConsole : ICliConsole
    {
        public TestCliConsole(TextReader input, TextWriter output, TextWriter error)
        {
            In = input ?? throw new ArgumentNullException(nameof(input));
            Out = output ?? throw new ArgumentNullException(nameof(output));
            Error = error ?? throw new ArgumentNullException(nameof(error));
        }

        public TextReader In { get; }
        public TextWriter Out { get; }
        public TextWriter Error { get; }
        public bool IsInputRedirected => true;

        public void WriteStatus(string message)
        {
            ArgumentNullException.ThrowIfNull(message);
            Error.WriteLine(message);
            Error.Flush();
        }
    }

    public enum PayloadSource
    {
        File,
        Stdin
    }

    public sealed record InputCommandCase(
        string Name,
        PayloadSource PayloadSource,
        Func<PluginRepoIndex> CreateInitialIndex,
        object Payload,
        Func<string, string, string[]> BuildArgs,
        Func<PluginRepoIndex, PluginRepoIndex> BuildExpected)
    {
        public override string ToString() => Name;
    }

    public sealed record MutationCommandCase(
        string Name,
        Func<PluginRepoIndex> CreateInitialIndex,
        object? Payload,
        Func<string, string?, string[]> BuildArgs,
        Func<PluginRepoIndex, PluginRepoIndex> BuildExpected)
    {
        public override string ToString() => Name;
    }

    public sealed record DryRunCommandCase(
        string Name,
        Func<PluginRepoIndex> CreateInitialIndex,
        object? Payload,
        Func<string, string?, string[]> BuildArgs)
    {
        public override string ToString() => Name;
    }

    private sealed record CliRunResult(int ExitCode, string Stdout, string Stderr)
    {
        public string AllOutput => Stdout + Stderr;
    }
}
