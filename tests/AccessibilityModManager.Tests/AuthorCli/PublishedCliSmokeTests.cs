using System.Diagnostics;
using System.Text.Json;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Tests.Authoring;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class PublishedCliSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "amm-published-cli-" + Guid.NewGuid().ToString("N"));

    [PublishedCliFact]
    public async Task Published_cli_runs_without_the_WPF_application()
    {
        var configuredPath = Environment.GetEnvironmentVariable("AMM_AUTHOR_CLI_EXE")!;
        var executable = Path.GetFullPath(configuredPath);
        Assert.True(File.Exists(executable), $"Published CLI not found: {executable}");
        Directory.CreateDirectory(_root);
        var project = Path.Combine(_root, "project");
        new IndexFileService(TestLogger.Create()).Save(
            project,
            CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex());
        var wpfBefore = WpfProcessIds();

        var version = await RunAsync(executable, "--version");
        var help = await RunAsync(executable, "--help");
        var status = await RunAsync(
            executable,
            "project", "status", "--project", project, "--json", "--quiet");

        Assert.Equal(0, version.ExitCode);
        Assert.Equal("0.28.0", version.Stdout.Trim());
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("Accessibility Mod Manager authoring CLI", help.Stdout, StringComparison.Ordinal);
        Assert.Contains("release", help.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, status.ExitCode);
        using var json = JsonDocument.Parse(status.Stdout);
        Assert.Equal("projectStatus", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            CatalogWorkflowTests.CatalogFixture.PluginId,
            json.RootElement.GetProperty("value").GetProperty("pluginId").GetString());
        Assert.Empty(WpfProcessIds().Except(wpfBefore));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<ProcessResult> RunAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The published CLI did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static HashSet<int> WpfProcessIds() =>
        Process.GetProcesses()
            .Where(process =>
                process.ProcessName.Contains("PluginIndexAuthor", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Contains("AccessibilityModManager.AuthorTool", StringComparison.OrdinalIgnoreCase))
            .Select(process => process.Id)
            .ToHashSet();

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}

public sealed class PublishedCliFactAttribute : FactAttribute
{
    public PublishedCliFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AMM_AUTHOR_CLI_EXE")))
            Skip = "AMM_AUTHOR_CLI_EXE is not set; no published executable was guessed.";
    }
}
