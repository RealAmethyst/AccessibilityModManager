using System.Security.Cryptography;
using System.Text.Json;
using AccessibilityModManager.AuthorCli;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class PackageCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;

    public PackageCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "amm-package-cli-" + Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(_root);
        new IndexFileService(TestLogger.Create()).Save(_project, CreateIndex());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Package_build_and_validate_use_project_identity_and_finished_bytes()
    {
        var source = CreateSource();
        File.WriteAllText(Path.Combine(source, "reader.dll"), "accessible");
        var output = Path.Combine(_root, "release.zip");

        var build = await InvokeAsync(
            "--project", _project,
            "--json", "--quiet",
            "package", "build",
            "--source", source,
            "--game", GameId,
            "--version", Version,
            "--output", output);

        Assert.Equal((int)CliExitCode.Success, build.ExitCode);
        Assert.True(File.Exists(output));
        Assert.Equal(Hash(output), ReadValue(build.Stdout, "sha256"));

        var validate = await InvokeAsync(
            "--json", "--quiet",
            "package", "validate",
            "--zip", output,
            "--plugin", PluginId,
            "--game", GameId,
            "--version", Version);

        Assert.Equal((int)CliExitCode.Success, validate.ExitCode);
        Assert.Equal(Hash(output), ReadValue(validate.Stdout, "sha256"));
    }

    [Fact]
    public async Task Package_build_dry_run_validates_without_creating_a_zip()
    {
        var source = CreateSource();
        File.WriteAllText(Path.Combine(source, "reader.dll"), "accessible");
        var output = Path.Combine(_root, "dry-run.zip");

        var run = await InvokeAsync(
            "--project", _project,
            "--json", "--quiet", "--dry-run",
            "package", "build",
            "--source", source,
            "--game", GameId,
            "--version", Version,
            "--output", output);

        Assert.Equal((int)CliExitCode.Success, run.ExitCode);
        Assert.False(File.Exists(output));
        using var document = JsonDocument.Parse(run.Stdout);
        Assert.True(document.RootElement.GetProperty("value").GetProperty("dryRun").GetBoolean());
    }

    [Fact]
    public async Task Package_hash_has_digest_only_human_output_and_named_json_output()
    {
        var file = Path.Combine(_root, "bytes.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        var expected = Hash(file);

        var human = await InvokeAsync("package", "hash", "--file", file);
        Assert.Equal((int)CliExitCode.Success, human.ExitCode);
        Assert.Equal(expected + Environment.NewLine, human.Stdout);

        var json = await InvokeAsync("--json", "--quiet", "package", "hash", "--file", file);
        Assert.Equal((int)CliExitCode.Success, json.ExitCode);
        Assert.Equal(expected, ReadValue(json.Stdout, "sha256"));
    }

    [Fact]
    public async Task Package_validate_identity_failure_returns_validation_exit_code()
    {
        var source = CreateSource();
        File.WriteAllText(Path.Combine(source, "reader.dll"), "accessible");
        var output = Path.Combine(_root, "identity.zip");
        var built = await InvokeAsync(
            "--project", _project,
            "--json", "--quiet",
            "package", "build",
            "--source", source,
            "--game", GameId,
            "--version", Version,
            "--output", output);
        Assert.Equal((int)CliExitCode.Success, built.ExitCode);

        var validate = await InvokeAsync(
            "--json", "--quiet",
            "package", "validate",
            "--zip", output,
            "--plugin", "wrong-plugin",
            "--game", GameId,
            "--version", Version);

        Assert.Equal((int)CliExitCode.Validation, validate.ExitCode);
        Assert.Contains("mismatch", validate.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    private const string PluginId = "sample-plugin";
    private const string GameId = "sample-game";
    private const string Version = "1.2.3";

    private string CreateSource()
    {
        var source = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        return source;
    }

    private static PluginRepoIndex CreateIndex() =>
        new()
        {
            PluginId = PluginId,
            RepoVersion = "1",
            GeneratedAt = DateTime.UtcNow,
            Games =
            [
                new GameDefinition
                {
                    GameId = GameId,
                    DisplayName = "Sample Game",
                    Dependencies = [],
                    ProbeRules = [],
                    Tags = ["screen-reader"],
                    Languages = ["en"]
                }
            ],
            ReleasesByGameId = new Dictionary<string, List<ModRelease>> { [GameId] = [] },
            DependencyPresets = []
        };

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
            LogDirectory: Path.Combine(_root, "logs")));

        var exitCode = await Program.RunAsync(args, services);
        return new CliRunResult(exitCode, output.ToString(), error.ToString());
    }

    private static string ReadValue(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("value").GetProperty(property).GetString()!;
    }

    private static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

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
