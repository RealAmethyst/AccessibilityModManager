using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Authoring;

public sealed class PackageWorkflowTests : IDisposable
{
    private readonly string _root;
    private readonly PackageWorkflow _workflow;

    public PackageWorkflowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "amm-package-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var logger = TestLogger.Create();
        _workflow = new PackageWorkflow(
            new ManifestBuilderService(logger),
            new Sha256HashService(),
            logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task BuildAsync_wraps_files_and_folders_then_hashes_the_valid_finished_zip()
    {
        var source = CreateSource();
        File.WriteAllText(Path.Combine(source, "version.dll"), "loader");
        var mods = Directory.CreateDirectory(Path.Combine(source, "Mods")).FullName;
        File.WriteAllText(Path.Combine(mods, "reader.dll"), "reader");
        var output = Path.Combine(_root, "out", "sample.zip");

        var result = await _workflow.BuildAsync(Request(source, output), CancellationToken.None);

        Assert.True(result.Validation.IsValid, string.Join(Environment.NewLine, result.Validation.Errors));
        Assert.Equal(output, result.ZipPath);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(output))),
            result.Sha256);

        using var archive = ZipFile.OpenRead(output);
        Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "files/version.dll");
        Assert.Contains(archive.Entries, entry => entry.FullName == "files/Mods/reader.dll");
    }

    [Fact]
    public async Task BuildAsync_bundles_an_external_lifecycle_script_at_its_declared_path()
    {
        var source = CreateSource();
        File.WriteAllText(Path.Combine(source, "mod.dll"), "mod");
        var external = Path.Combine(_root, "external.ps1");
        File.WriteAllText(external, "Write-Output accessible");
        var script = Script("files/scripts/install.ps1");
        var output = Path.Combine(_root, "external-script.zip");
        var request = Request(
            source,
            output,
            new LifecycleScriptInputs(PreInstall: script, PreInstallSourcePath: external));

        var result = await _workflow.BuildAsync(request, CancellationToken.None);

        Assert.True(result.Validation.IsValid, string.Join(Environment.NewLine, result.Validation.Errors));
        using var archive = ZipFile.OpenRead(output);
        Assert.Contains(archive.Entries, entry => entry.FullName == "files/scripts/install.ps1");
        var manifestEntry = archive.GetEntry("manifest.json")!;
        using var reader = new StreamReader(manifestEntry.Open());
        using var manifest = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal(
            "files/scripts/install.ps1",
            manifest.RootElement.GetProperty("preInstall").GetProperty("executable").GetString());
    }

    [Fact]
    public async Task BuildAsync_accepts_a_script_only_mod()
    {
        var source = CreateSource();
        var scriptFile = Path.Combine(_root, "toggle.cmd");
        File.WriteAllText(scriptFile, "@echo off");
        var output = Path.Combine(_root, "script-only.zip");
        var request = Request(
            source,
            output,
            new LifecycleScriptInputs(
                PostInstall: Script("files/scripts/toggle.cmd"),
                PostInstallSourcePath: scriptFile));

        var result = await _workflow.BuildAsync(request, CancellationToken.None);

        Assert.True(result.Validation.IsValid, string.Join(Environment.NewLine, result.Validation.Errors));
        Assert.Equal(2, result.FileCount);
    }

    [Theory]
    [InlineData("wrong-plugin", GameId, Version)]
    [InlineData(PluginId, "wrong-game", Version)]
    [InlineData(PluginId, GameId, "9.9.9")]
    public async Task ValidateAsync_reports_identity_mismatches(
        string expectedPlugin,
        string expectedGame,
        string expectedVersion)
    {
        var source = CreateSource();
        File.WriteAllText(Path.Combine(source, "mod.dll"), "mod");
        var built = await _workflow.BuildAsync(
            Request(source, Path.Combine(_root, "identity.zip")),
            CancellationToken.None);

        var inspected = await _workflow.ValidateAsync(
            built.ZipPath,
            expectedPlugin,
            expectedGame,
            expectedVersion,
            CancellationToken.None);

        Assert.False(inspected.Validation.IsValid);
        Assert.NotEmpty(inspected.Validation.Errors);
    }

    [Fact]
    public async Task BuildAsync_rejects_missing_and_unsafe_script_sources_without_leaving_output()
    {
        var source = CreateSource();
        var missingOutput = Path.Combine(_root, "missing.zip");
        var missing = Request(
            source,
            missingOutput,
            new LifecycleScriptInputs(
                PreInstall: Script("files/scripts/missing.ps1"),
                PreInstallSourcePath: Path.Combine(_root, "missing.ps1")));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _workflow.BuildAsync(missing, CancellationToken.None));
        Assert.False(File.Exists(missingOutput));

        var existing = Path.Combine(_root, "existing.ps1");
        File.WriteAllText(existing, "Write-Output test");
        var unsafeOutput = Path.Combine(_root, "unsafe.zip");
        var unsafeRequest = Request(
            source,
            unsafeOutput,
            new LifecycleScriptInputs(
                PreInstall: Script("files/../outside.ps1"),
                PreInstallSourcePath: existing));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.BuildAsync(unsafeRequest, CancellationToken.None));
        Assert.False(File.Exists(unsafeOutput));
    }

    [Fact]
    public async Task BuildAsync_rejects_output_inside_source_and_cancellation_removes_partial_output()
    {
        var source = CreateSource();
        File.WriteAllText(Path.Combine(source, "mod.dll"), "mod");
        var nestedOutput = Path.Combine(source, "package.zip");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _workflow.BuildAsync(Request(source, nestedOutput), CancellationToken.None));
        Assert.False(File.Exists(nestedOutput));

        var cancelledOutput = Path.Combine(_root, "cancelled.zip");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _workflow.BuildAsync(Request(source, cancelledOutput), cts.Token));
        Assert.False(File.Exists(cancelledOutput));
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

    private static PackageBuildRequest Request(
        string source,
        string output,
        LifecycleScriptInputs? scripts = null) =>
        new(
            source,
            output,
            PluginId,
            GameId,
            Version,
            Array.Empty<Dependency>(),
            scripts ?? new LifecycleScriptInputs());

    private static LifecycleScript Script(string executable) =>
        new()
        {
            Executable = executable,
            What = "Applies accessibility settings.",
            Why = "The mod needs those settings.",
            Modifies = "The game configuration."
        };
}
