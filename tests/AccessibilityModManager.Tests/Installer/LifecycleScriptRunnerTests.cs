using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Installer;

/// <summary>
/// Tests for <see cref="LifecycleScriptRunner"/>: extension allowlist, path-traversal rejection,
/// description requirements, runner-by-extension command shape, and an end-to-end .cmd run that
/// exercises stdout streaming and exit-code handling.
/// </summary>
public class LifecycleScriptRunnerTests : IDisposable
{
    private readonly string _stagingDir;
    private readonly LifecycleScriptRunner _runner;

    public LifecycleScriptRunnerTests()
    {
        _stagingDir = Path.Combine(Path.GetTempPath(), "ammtest_script_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stagingDir);
        _runner = new LifecycleScriptRunner(TestLogger.Create());
    }

    public void Dispose()
    {
        if (Directory.Exists(_stagingDir))
        {
            try { Directory.Delete(_stagingDir, true); } catch { }
        }
    }

    [Fact]
    public void ValidateScriptInStaging_RejectsUnsupportedExtension()
    {
        var script = MakeScript("payload.dll");
        File.WriteAllText(Path.Combine(_stagingDir, "payload.dll"), "x");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LifecycleScriptRunner.ValidateScriptInStaging(script, _stagingDir));
        Assert.Contains(".exe, .ps1, .cmd, or .bat", ex.Message);
    }

    [Fact]
    public void ValidateScriptInStaging_RejectsPathTraversal()
    {
        var script = MakeScript(@"..\escape.exe");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LifecycleScriptRunner.ValidateScriptInStaging(script, _stagingDir));
        Assert.Contains("escapes the package staging dir", ex.Message);
    }

    [Fact]
    public void ValidateScriptInStaging_RejectsAbsolutePathOutsideStaging()
    {
        var outsideAbsolute = Path.Combine(Path.GetTempPath(), "outside.exe");
        var script = MakeScript(outsideAbsolute);

        Assert.Throws<InvalidOperationException>(() =>
            LifecycleScriptRunner.ValidateScriptInStaging(script, _stagingDir));
    }

    [Fact]
    public void ValidateScriptInStaging_ThrowsWhenFileMissing()
    {
        var script = MakeScript("declared-but-missing.ps1");

        Assert.Throws<FileNotFoundException>(() =>
            LifecycleScriptRunner.ValidateScriptInStaging(script, _stagingDir));
    }

    [Fact]
    public void ValidateScriptInStaging_RequiresDescriptionFields()
    {
        File.WriteAllText(Path.Combine(_stagingDir, "ok.cmd"), "@echo ok");
        var script = new LifecycleScript
        {
            Executable = "ok.cmd",
            What = "",   // empty
            Why = "why",
            Modifies = "modifies"
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LifecycleScriptRunner.ValidateScriptInStaging(script, _stagingDir));
        Assert.Contains("description fields", ex.Message);
    }

    [Fact]
    public void ValidateScriptInStaging_AcceptsValidScript()
    {
        File.WriteAllText(Path.Combine(_stagingDir, "good.ps1"), "Write-Host hi");
        var script = new LifecycleScript
        {
            Executable = "good.ps1",
            What = "what",
            Why = "why",
            Modifies = "modifies"
        };

        // Should not throw.
        LifecycleScriptRunner.ValidateScriptInStaging(script, _stagingDir);
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".ps1")]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    public void BuildCommand_ProducesEntryForEveryAllowedExtension(string ext)
    {
        var (file, args) = LifecycleScriptRunner.BuildCommand(
            ext, $@"C:\stage\script{ext}", @"C:\game", @"C:\stage");

        Assert.False(string.IsNullOrEmpty(file));
        Assert.NotEmpty(args);
        Assert.Contains(@"C:\game", args);
        Assert.Contains(@"C:\stage", args);
    }

    [Fact]
    public void BuildCommand_Ps1_PassesNoProfileAndBypass()
    {
        var (file, args) = LifecycleScriptRunner.BuildCommand(
            ".ps1", @"C:\stage\go.ps1", @"C:\game", @"C:\stage");

        Assert.Equal("powershell.exe", file);
        Assert.Contains("-NoProfile", args);
        Assert.Contains("-ExecutionPolicy", args);
        Assert.Contains("Bypass", args);
        Assert.Contains("-File", args);
    }

    [Fact]
    public void BuildCommand_Throws_OnUnknownExtension()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LifecycleScriptRunner.BuildCommand(".sh", @"C:\stage\go.sh", @"C:\game", @"C:\stage"));
    }

    [Fact]
    public async Task RunAsync_CmdScript_StreamsOutputAndReportsExitCode()
    {
        // Echo something so we can prove streaming works, then exit 0.
        var scriptPath = Path.Combine(_stagingDir, "say-hi.cmd");
        File.WriteAllText(scriptPath, "@echo off\r\necho hello-from-script\r\nexit /b 0\r\n");

        var lines = new List<string>();
        var script = new LifecycleScript
        {
            Executable = "say-hi.cmd",
            What = "demo",
            Why = "demo",
            Modifies = "nothing"
        };

        var result = await _runner.RunAsync(
            script,
            scriptPath,
            gameFolder: _stagingDir,
            modFolder: _stagingDir,
            onOutputLine: line => lines.Add(line));

        Assert.True(result.Succeeded, $"expected success but got exit {result.ExitCode}");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(lines, l => l.Contains("hello-from-script"));
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ReportsFailure()
    {
        var scriptPath = Path.Combine(_stagingDir, "fail.cmd");
        File.WriteAllText(scriptPath, "@exit /b 7\r\n");

        var script = new LifecycleScript
        {
            Executable = "fail.cmd",
            What = "demo",
            Why = "demo",
            Modifies = "nothing"
        };

        var result = await _runner.RunAsync(
            script,
            scriptPath,
            gameFolder: _stagingDir,
            modFolder: _stagingDir,
            onOutputLine: null);

        Assert.False(result.Succeeded);
        Assert.Equal(7, result.ExitCode);
    }

    private static LifecycleScript MakeScript(string executable) => new()
    {
        Executable = executable,
        What = "what",
        Why = "why",
        Modifies = "modifies"
    };
}
