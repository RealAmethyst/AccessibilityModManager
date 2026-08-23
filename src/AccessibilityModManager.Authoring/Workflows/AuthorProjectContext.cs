using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Authoring.Workflows;

public sealed record ResolvedAuthorProject(string ProjectPath, PluginRepoIndex Index);

public sealed class AuthorProjectContext
{
    private const string LockFileName = ".amm-author.lock";

    private readonly AuthorConfigService _configService;
    private readonly IndexFileService _indexFiles;

    public AuthorProjectContext(
        AuthorConfigService configService,
        IndexFileService indexFiles)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _indexFiles = indexFiles ?? throw new ArgumentNullException(nameof(indexFiles));
    }

    public Task<ResolvedAuthorProject> ResolveAsync(
        string? explicitPath,
        string currentDirectory,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Task.FromResult(LoadResolvedProject(Path.GetFullPath(explicitPath)));
        }

        if (IsProjectDirectory(currentDirectory))
        {
            return Task.FromResult(LoadResolvedProject(Path.GetFullPath(currentDirectory)));
        }

        var savedPath = _configService.Load().LastOpenedProjectPath;
        if (string.IsNullOrWhiteSpace(savedPath))
        {
            throw new InvalidOperationException(
                "No author project could be resolved from --project, the current directory, or the saved last-opened project.");
        }

        return Task.FromResult(LoadResolvedProject(Path.GetFullPath(savedPath)));
    }

    public async Task<FileStream> AcquireWriteLeaseAsync(string projectPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        ct.ThrowIfCancellationRequested();

        var fullProjectPath = Path.GetFullPath(projectPath);
        var lockPath = Path.Combine(fullProjectPath, LockFileName);
        var lease = await CrossProcessFileLock.AcquireAsync(lockPath, "author project");

        if (ct.IsCancellationRequested)
        {
            await lease.DisposeAsync();
            ct.ThrowIfCancellationRequested();
        }

        return lease;
    }

    private bool IsProjectDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return _indexFiles.Exists(Path.GetFullPath(path));
    }

    private ResolvedAuthorProject LoadResolvedProject(string fullProjectPath)
    {
        var index = _indexFiles.Load(fullProjectPath);
        return new ResolvedAuthorProject(fullProjectPath, index);
    }
}
