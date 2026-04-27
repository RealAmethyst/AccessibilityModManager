using System.IO;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

public sealed class GitService
{
    private readonly ILogger _logger;

    public GitService(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("git", new[] { "--version" }, ct: ct);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsRepoAsync(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return false;
        var result = await ProcessRunner.RunAsync("git",
            new[] { "rev-parse", "--is-inside-work-tree" }, folder, ct);
        return result.Success && result.Stdout.Trim() == "true";
    }

    public async Task<ProcessResult> CloneAsync(string repoUrl, string targetFolder, CancellationToken ct = default)
    {
        var parent = Path.GetDirectoryName(targetFolder);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        _logger.Information("git clone {Repo} {Target}", repoUrl, targetFolder);
        return await ProcessRunner.RunAsync("git",
            new[] { "clone", repoUrl, targetFolder }, ct: ct);
    }

    public async Task<ProcessResult> PullAsync(string folder, CancellationToken ct = default)
    {
        _logger.Information("git pull in {Folder}", folder);
        return await ProcessRunner.RunAsync("git", new[] { "pull", "--ff-only" }, folder, ct);
    }

    public async Task<ProcessResult> StatusPorcelainAsync(string folder, CancellationToken ct = default)
    {
        return await ProcessRunner.RunAsync("git", new[] { "status", "--porcelain" }, folder, ct);
    }

    public async Task<bool> HasUncommittedChangesAsync(string folder, CancellationToken ct = default)
    {
        var result = await StatusPorcelainAsync(folder, ct);
        return result.Success && !string.IsNullOrWhiteSpace(result.Stdout);
    }

    public async Task<ProcessResult> AddAsync(string folder, string pathSpec, CancellationToken ct = default)
    {
        return await ProcessRunner.RunAsync("git", new[] { "add", "--", pathSpec }, folder, ct);
    }

    public async Task<ProcessResult> CommitAsync(string folder, string message, CancellationToken ct = default)
    {
        _logger.Information("git commit -m \"{Message}\" in {Folder}", message, folder);
        return await ProcessRunner.RunAsync("git",
            new[] { "commit", "-m", message }, folder, ct);
    }

    public async Task<ProcessResult> PushAsync(string folder, CancellationToken ct = default)
    {
        _logger.Information("git push in {Folder}", folder);
        return await ProcessRunner.RunAsync("git", new[] { "push" }, folder, ct);
    }

    public async Task<ProcessResult> DiffAsync(string folder, string? pathSpec = null, CancellationToken ct = default)
    {
        var args = new List<string> { "diff" };
        if (!string.IsNullOrEmpty(pathSpec))
        {
            args.Add("--");
            args.Add(pathSpec);
        }
        return await ProcessRunner.RunAsync("git", args, folder, ct);
    }

    public async Task<string?> GetRemoteUrlAsync(string folder, string remoteName = "origin", CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync("git",
            new[] { "remote", "get-url", remoteName }, folder, ct);
        return result.Success ? result.Stdout.Trim() : null;
    }

    public async Task<string?> GetCurrentBranchAsync(string folder, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync("git",
            new[] { "rev-parse", "--abbrev-ref", "HEAD" }, folder, ct);
        return result.Success ? result.Stdout.Trim() : null;
    }

    public async Task<ProcessResult> CheckoutNewBranchAsync(string folder, string branchName, CancellationToken ct = default)
    {
        _logger.Information("git checkout -b {Branch} in {Folder}", branchName, folder);
        return await ProcessRunner.RunAsync("git",
            new[] { "checkout", "-b", branchName }, folder, ct);
    }

    public async Task<ProcessResult> CheckoutAsync(string folder, string branchName, CancellationToken ct = default)
    {
        _logger.Information("git checkout {Branch} in {Folder}", branchName, folder);
        return await ProcessRunner.RunAsync("git",
            new[] { "checkout", branchName }, folder, ct);
    }

    public async Task<ProcessResult> PushNewBranchAsync(string folder, string branchName, CancellationToken ct = default)
    {
        _logger.Information("git push -u origin {Branch} in {Folder}", branchName, folder);
        return await ProcessRunner.RunAsync("git",
            new[] { "push", "-u", "origin", branchName }, folder, ct);
    }

    public async Task<ProcessResult> DeleteLocalBranchAsync(string folder, string branchName, bool force = false, CancellationToken ct = default)
    {
        _logger.Information("git branch -D {Branch} in {Folder}", branchName, folder);
        return await ProcessRunner.RunAsync("git",
            new[] { "branch", force ? "-D" : "-d", branchName }, folder, ct);
    }
}
