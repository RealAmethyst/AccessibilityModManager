using System.Collections.ObjectModel;
using System.IO;
using AccessibilityModManager.AuthorTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed partial class ProjectPickerViewModel : ObservableObject
{
    private readonly AuthorConfigService _configService;
    private readonly GitHubService _gitHubService;
    private readonly GitService _gitService;
    private readonly IndexFileService _indexFileService;
    private readonly ILogger _logger;
    private readonly Action<string> _openProject;
    private readonly Action<string, string> _showInfoDialog;
    private readonly Func<string, string, bool> _confirmDialog;
    private readonly Func<string?, string?> _browseForFolder;
    private readonly Func<string, string, string?, string?> _promptForString;
    private readonly Action _openRegistryAdmin;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isShowingGitHubRepos;

    [ObservableProperty]
    private RecentProjectItem? _selectedRecent;

    [ObservableProperty]
    private GitHubRepoItem? _selectedGitHubRepo;

    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = [];
    public ObservableCollection<GitHubRepoItem> GitHubRepos { get; } = [];

    public ProjectPickerViewModel(
        AuthorConfigService configService,
        GitHubService gitHubService,
        GitService gitService,
        IndexFileService indexFileService,
        ILogger logger,
        Action<string> openProject,
        Action<string, string> showInfoDialog,
        Func<string, string, bool> confirmDialog,
        Func<string?, string?> browseForFolder,
        Func<string, string, string?, string?> promptForString,
        Action openRegistryAdmin)
    {
        _configService = configService;
        _gitHubService = gitHubService;
        _gitService = gitService;
        _indexFileService = indexFileService;
        _logger = logger;
        _openProject = openProject;
        _showInfoDialog = showInfoDialog;
        _confirmDialog = confirmDialog;
        _browseForFolder = browseForFolder;
        _promptForString = promptForString;
        _openRegistryAdmin = openRegistryAdmin;

        LoadRecentProjects();
    }

    private void LoadRecentProjects()
    {
        RecentProjects.Clear();
        var config = _configService.Load();
        foreach (var p in config.RecentProjects.OrderByDescending(p => p.LastOpenedAt))
        {
            RecentProjects.Add(new RecentProjectItem
            {
                Path = p.Path,
                DisplayName = p.DisplayName ?? new DirectoryInfo(p.Path).Name,
                Subtitle = p.GitHubRepo ?? p.Path,
                Exists = Directory.Exists(p.Path)
            });
        }
    }

    [RelayCommand]
    private void OpenRecent()
    {
        if (SelectedRecent == null) return;
        if (!Directory.Exists(SelectedRecent.Path))
        {
            var keep = _confirmDialog("Folder missing",
                $"The folder no longer exists:\n{SelectedRecent.Path}\n\nRemove it from the recent list?");
            if (keep)
            {
                _configService.RemoveRecent(SelectedRecent.Path);
                LoadRecentProjects();
            }
            return;
        }
        _openProject(SelectedRecent.Path);
    }

    [RelayCommand]
    private void OpenAdmin() => _openRegistryAdmin();

    [RelayCommand]
    private void OpenLocalFolder()
    {
        var folder = _browseForFolder(null);
        if (string.IsNullOrEmpty(folder)) return;

        EnsureIndexAndOpen(folder, githubRepo: null);
    }

    [RelayCommand]
    private async Task ListGitHubReposAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading repos from GitHub...";
        try
        {
            if (!await _gitHubService.IsAvailableAsync())
            {
                _showInfoDialog("GitHub CLI missing",
                    "The GitHub CLI ('gh') is not installed or not on your PATH.\n\n" +
                    "Install it from https://cli.github.com/ then run 'gh auth login', or use 'Open local folder' instead.");
                return;
            }
            if (!await _gitHubService.IsAuthenticatedAsync())
            {
                _showInfoDialog("Not signed in to GitHub",
                    "You're not signed in to the GitHub CLI yet. Open a terminal and run:\n\n    gh auth login\n\nThen try again.");
                return;
            }

            var repos = await _gitHubService.ListReposAsync();
            GitHubRepos.Clear();
            foreach (var r in repos)
            {
                GitHubRepos.Add(new GitHubRepoItem
                {
                    NameWithOwner = r.NameWithOwner,
                    Description = string.IsNullOrEmpty(r.Description) ? r.NameWithOwner : r.Description
                });
            }
            IsShowingGitHubRepos = true;
            StatusMessage = $"Found {repos.Count} repos. Pick one to use as your plugin index project.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to list GitHub repos");
            _showInfoDialog("Could not list repos", ex.Message);
            StatusMessage = null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task UseGitHubRepoAsync()
    {
        if (SelectedGitHubRepo == null) return;

        var repo = SelectedGitHubRepo.NameWithOwner;
        var localPath = Path.Combine(AuthorConfigService.GetReposDirectory(),
            repo.Replace('/', '-'));

        IsLoading = true;
        StatusMessage = $"Cloning {repo}...";
        try
        {
            if (!await _gitService.IsAvailableAsync())
            {
                _showInfoDialog("Git missing",
                    "Git is not installed or not on your PATH. Install Git for Windows from https://git-scm.com/.");
                return;
            }

            if (Directory.Exists(localPath))
            {
                StatusMessage = $"Updating {repo}...";
                var pull = await _gitService.PullAsync(localPath);
                if (!pull.Success)
                    _logger.Warning("git pull failed: {Output}", pull.Combined);
            }
            else
            {
                var clone = await _gitService.CloneAsync($"https://github.com/{repo}.git", localPath);
                if (!clone.Success)
                {
                    _showInfoDialog("Clone failed",
                        $"Could not clone {repo}:\n\n{clone.Combined}");
                    return;
                }
            }

            _configService.RecordRecent(localPath, displayName: repo, gitHubRepo: repo);
            EnsureIndexAndOpen(localPath, githubRepo: repo);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to use GitHub repo {Repo}", repo);
            _showInfoDialog("Could not use repo", ex.Message);
        }
        finally
        {
            IsLoading = false;
            StatusMessage = null;
        }
    }

    [RelayCommand]
    private void BackFromGitHubList()
    {
        IsShowingGitHubRepos = false;
        SelectedGitHubRepo = null;
    }

    private void EnsureIndexAndOpen(string folder, string? githubRepo)
    {
        var indexPath = IndexFileService.GetIndexPath(folder);

        if (!File.Exists(indexPath))
        {
            var defaultId = SuggestPluginId(folder, githubRepo);
            var pluginId = _promptForString(
                "Set a plugin ID",
                "This folder has no index.json yet. Pick a plugin ID — the unique " +
                "identifier for this plugin author across the registry. Lowercase, no " +
                "spaces. Examples: amethyst, awesomedev, modder123.",
                defaultId);

            if (string.IsNullOrEmpty(pluginId)) return;

            var starter = _indexFileService.CreateStarter(pluginId);
            _indexFileService.Save(folder, starter);
        }

        _configService.RecordRecent(folder,
            displayName: Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            gitHubRepo: githubRepo);
        _openProject(folder);
    }

    /// <summary>
    /// Reasonable default plugin id derived from the GitHub repo name (preferred) or the
    /// folder name. Sanitized to lowercase alphanumerics so it's a valid identifier.
    /// </summary>
    private static string SuggestPluginId(string folder, string? githubRepo)
    {
        var raw = !string.IsNullOrWhiteSpace(githubRepo)
            ? githubRepo!.Split('/').Last()
            : Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var sanitized = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return sanitized;
    }
}

public sealed class RecentProjectItem
{
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
    public required string Subtitle { get; init; }
    public bool Exists { get; init; }
    public override string ToString() => DisplayName;
}

public sealed class GitHubRepoItem
{
    public required string NameWithOwner { get; init; }
    public required string Description { get; init; }
    public override string ToString() => NameWithOwner;
}
