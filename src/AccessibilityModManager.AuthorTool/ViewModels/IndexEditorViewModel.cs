using System.Collections.ObjectModel;
using System.IO;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed partial class IndexEditorViewModel : ObservableObject
{
    private readonly string _projectPath;
    private readonly AuthorConfigService _configService;
    private readonly IndexFileService _indexFileService;
    private readonly Sha256HashService _hashService;
    private readonly GitService _gitService;
    private readonly GitHubService _gitHubService;
    private readonly ILogger _logger;
    private readonly Action<string, string> _showInfoDialog;
    private readonly Func<string, string, bool> _confirmDialog;
    private readonly Func<string, string, string?, string?> _browseForFile;
    private readonly Action _closeProject;
    private readonly Func<string, string, string, string, string?, ObservableCollection<string>, IList<Dependency>, LifecycleScriptInputs, ModRelease?, ModRelease?> _showReleaseDialog;
    private readonly Func<ISet<string>, ObservableCollection<string>, AddGameDialogViewModel?> _showAddGameDialog;
    private readonly Func<string, PluginAuthorInfo?, PluginAuthorInfo?> _showAuthorInfoDialog;
    private readonly Action _showServerUploadSettingsDialog;
    private readonly RegistryMembershipChecker _registryChecker;

    private PluginRepoIndex _index;
    private bool _suppressDirty;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private GameItemViewModel? _selectedGame;

    [ObservableProperty]
    private bool _isLoadingGitHubRepos;

    /// <summary>
    /// Three-way state for the public-registry banner:
    /// null = checking or unknown, true = listed, false = not listed (and registry was reachable).
    /// </summary>
    [ObservableProperty]
    private bool? _isListedInRegistry;

    [ObservableProperty]
    private string? _registryStatusText;

    [ObservableProperty]
    private bool _registryCheckCompleted;

    /// <summary>
    /// True when at least one game has zero tags AND zero languages set. Drives a soft
    /// warning banner near the Save button so authors notice — Save still works.
    /// </summary>
    public bool HasGamesWithoutFilters =>
        Games.Any(g => !g.HasAnyFilters);

    public string? FilterWarningText =>
        HasGamesWithoutFilters
            ? $"{Games.Count(g => !g.HasAnyFilters)} game(s) have no filter tags or languages set yet — users won't find them via filters."
            : null;

    public string PluginId => _index.PluginId;
    public string ProjectPath => _projectPath;
    public string DisplayProjectPath => _projectPath;

    public ObservableCollection<GameItemViewModel> Games { get; } = [];

    /// <summary>
    /// User's GitHub repos, fetched lazily via 'gh repo list'. Shared with the new-release
    /// dialog and the game form so both can use a dropdown picker. Empty if gh is missing
    /// or the user isn't authenticated — the editable ComboBox falls back to free text.
    /// </summary>
    public ObservableCollection<string> AvailableGitHubRepos { get; } = [];

    public IndexEditorViewModel(
        string projectPath,
        AuthorConfigService configService,
        IndexFileService indexFileService,
        Sha256HashService hashService,
        GitService gitService,
        GitHubService gitHubService,
        PatreonAuthorService patreon,
        ILogger logger,
        Action<string, string> showInfoDialog,
        Func<string, string, bool> confirmDialog,
        Func<string, string, string?, string?> browseForFile,
        Action closeProject,
        Func<string, string, string, string, string?, ObservableCollection<string>, IList<Dependency>, LifecycleScriptInputs, ModRelease?, ModRelease?> showReleaseDialog,
        Func<ISet<string>, ObservableCollection<string>, AddGameDialogViewModel?> showAddGameDialog,
        Func<string, PluginAuthorInfo?, PluginAuthorInfo?> showAuthorInfoDialog,
        Action showServerUploadSettingsDialog,
        RegistryMembershipChecker registryChecker)
    {
        _projectPath = projectPath;
        _configService = configService;
        _indexFileService = indexFileService;
        _hashService = hashService;
        _gitService = gitService;
        _gitHubService = gitHubService;
        _patreon = patreon;
        _logger = logger;
        _showInfoDialog = showInfoDialog;
        _confirmDialog = confirmDialog;
        _browseForFile = browseForFile;
        _closeProject = closeProject;
        _showReleaseDialog = showReleaseDialog;
        _showAddGameDialog = showAddGameDialog;
        _showAuthorInfoDialog = showAuthorInfoDialog;
        _showServerUploadSettingsDialog = showServerUploadSettingsDialog;
        _registryChecker = registryChecker;

        _patreon.StateChanged += OnPatreonStateChanged;

        _index = LoadOrThrow();
        RebuildGameList();
        if (Games.Count > 0) SelectedGame = Games[0];

        _ = LoadGitHubReposAsync();
        _ = CheckRegistryMembershipAsync();
    }

    private readonly PatreonAuthorService _patreon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatreonButtonText))]
    [NotifyPropertyChangedFor(nameof(PatreonStatusText))]
    private bool _patreonStateBumper;

    public string PatreonButtonText => _patreon.IsSignedIn ? "Sign out of Patreon" : "Sign in to Patreon...";

    public string PatreonStatusText
    {
        get
        {
            if (!_patreon.IsSignedIn) return "Not signed in to Patreon — sign in to mark releases as Patron-only.";
            var name = _patreon.CurrentAccount?.FullName ?? _patreon.CurrentAccount?.Email ?? "your account";
            var camp = _patreon.OwnCampaign?.DisplayName;
            return camp != null
                ? $"Signed in as {name}. Campaign: {camp} ({_patreon.OwnCampaign!.Tiers.Count} tier(s))."
                : $"Signed in as {name}. Couldn't load campaign — try refresh.";
        }
    }

    private void OnPatreonStateChanged() => PatreonStateBumper = !PatreonStateBumper;

    [RelayCommand]
    private async Task SignInOrOutOfPatreonAsync()
    {
        try
        {
            if (_patreon.IsSignedIn)
            {
                await _patreon.SignOutAsync(CancellationToken.None);
                StatusMessage = "Signed out of Patreon.";
            }
            else
            {
                await _patreon.SignInAsync(CancellationToken.None);
                StatusMessage = "Signed in to Patreon.";
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Patreon sign-in/out failed");
            _showInfoDialog("Patreon sign-in failed", ex.Message);
        }
    }

    private async Task CheckRegistryMembershipAsync()
    {
        IsListedInRegistry = null;
        RegistryStatusText = "Checking the public registry...";
        try
        {
            var result = await _registryChecker.CheckAsync(_index.PluginId);
            if (!result.RegistryReachable)
            {
                RegistryStatusText = "Couldn't reach the public registry to check listing. " +
                                     $"({result.Error})";
                IsListedInRegistry = null;
            }
            else if (result.IsListed)
            {
                RegistryStatusText = $"This plugin is listed in the public registry as " +
                                     $"\"{result.Entry?.Author ?? _index.PluginId}\".";
                IsListedInRegistry = true;
            }
            else
            {
                RegistryStatusText = $"Plugin id '{_index.PluginId}' is not in the public registry yet. " +
                                     "Click \"Request listing\" to open a pre-filled GitHub issue on the registry repo.";
                IsListedInRegistry = false;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Registry membership check failed");
            RegistryStatusText = $"Couldn't check registry: {ex.Message}";
            IsListedInRegistry = null;
        }
        finally
        {
            RegistryCheckCompleted = true;
        }
    }

    [RelayCommand]
    private async Task RecheckRegistryMembershipAsync()
    {
        await CheckRegistryMembershipAsync();
    }

    [RelayCommand]
    private void RequestRegistryListing()
    {
        var gitHubRepo = _configService.GetRecent(_projectPath)?.GitHubRepo;
        var url = RegistryMembershipChecker.BuildFeatureRequestUrl(
            _index.PluginId,
            _index.Author?.DisplayName,
            gitHubRepo,
            _index.Author?.Bio);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to open registry feature-request URL");
            _showInfoDialog("Could not open browser",
                $"Open this URL manually:\n\n{url}\n\n{ex.Message}");
        }
    }

    private async Task LoadGitHubReposAsync()
    {
        if (AvailableGitHubRepos.Count > 0) return;

        IsLoadingGitHubRepos = true;
        try
        {
            if (!await _gitHubService.IsAvailableAsync()) return;
            if (!await _gitHubService.IsAuthenticatedAsync()) return;

            var repos = await _gitHubService.ListReposAsync();
            foreach (var r in repos.OrderBy(r => r.NameWithOwner, StringComparer.OrdinalIgnoreCase))
                AvailableGitHubRepos.Add(r.NameWithOwner);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not load GitHub repos for picker");
        }
        finally
        {
            IsLoadingGitHubRepos = false;
        }
    }

    private PluginRepoIndex LoadOrThrow()
    {
        if (!_indexFileService.Exists(_projectPath))
            throw new InvalidOperationException($"index.json not found in {_projectPath}");
        return _indexFileService.Load(_projectPath);
    }

    private void RebuildGameList()
    {
        _suppressDirty = true;
        try
        {
            var prevSelectedId = SelectedGame?.GameId;
            Games.Clear();
            foreach (var g in _index.Games.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var releases = _index.ReleasesByGameId.TryGetValue(g.GameId, out var rs) ? rs : [];
                var item = new GameItemViewModel(g, releases, this);
                item.PerGameSourceRepo = _configService.GetGameSourceRepo(_projectPath, g.GameId);

                // Re-hydrate the absolute script paths the author picked in earlier sessions
                // so the editor reflects them on reopen. Setting these inside _suppressDirty
                // prevents the load from marking the project dirty.
                var scriptSources = _configService.GetGameScriptSources(_projectPath, g.GameId);
                if (scriptSources != null)
                {
                    item.PreInstallScript.AbsoluteSourcePath = scriptSources.PreInstall;
                    item.PostInstallScript.AbsoluteSourcePath = scriptSources.PostInstall;
                    item.PostUninstallScript.AbsoluteSourcePath = scriptSources.PostUninstall;
                }

                Games.Add(item);
            }
            SelectedGame = prevSelectedId == null
                ? Games.FirstOrDefault()
                : Games.FirstOrDefault(g => g.GameId == prevSelectedId) ?? Games.FirstOrDefault();
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    internal void MarkDirty()
    {
        if (_suppressDirty) return;
        HasUnsavedChanges = true;
        OnPropertyChanged(nameof(HasGamesWithoutFilters));
        OnPropertyChanged(nameof(FilterWarningText));
    }

    [RelayCommand]
    private void AddGame()
    {
        var existingIds = _index.Games
            .Select(g => g.GameId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = _showAddGameDialog(existingIds, AvailableGitHubRepos);
        if (result == null) return;

        var game = result.ToGame();
        _index.Games.Add(game);
        if (!_index.ReleasesByGameId.ContainsKey(game.GameId))
            _index.ReleasesByGameId[game.GameId] = [];

        // Persist the per-game GitHub repo into author config (not into index.json).
        if (!string.IsNullOrWhiteSpace(result.GitHubRepo))
            _configService.SetGameSourceRepo(_projectPath, game.GameId, result.GitHubRepo);

        MarkDirty();
        RebuildGameList();
        SelectedGame = Games.FirstOrDefault(g => g.GameId == game.GameId);
    }

    [RelayCommand]
    private void RemoveSelectedGame()
    {
        if (SelectedGame == null) return;
        var game = SelectedGame;
        if (!_confirmDialog("Remove game",
            $"Remove '{game.DisplayName}' and all its releases from the index?\n\nThis change is not saved until you click Save."))
            return;

        _index.Games.RemoveAll(g => g.GameId == game.GameId);
        _index.ReleasesByGameId.Remove(game.GameId);
        MarkDirty();
        RebuildGameList();
    }

    [RelayCommand]
    private async Task AddReleaseAsync()
    {
        if (SelectedGame == null) return;
        if (!TryValidateGameInstaller(SelectedGame)) return;

        var initialSourceRepo = _configService.GetGameSourceRepo(_projectPath, SelectedGame.GameId)
            ?? SelectedGame.PerGameSourceRepo;

        if (!TryBuildDependencies(SelectedGame, out var deps))
            return;
        if (!TryBuildScripts(SelectedGame, out var scriptInputs))
            return;

        var release = _showReleaseDialog(
            SelectedGame.GameId,
            SelectedGame.DisplayName,
            _index.PluginId,
            _projectPath,
            initialSourceRepo,
            AvailableGitHubRepos,
            deps,
            scriptInputs!,
            null);

        if (release == null) return;

        if (!_index.ReleasesByGameId.TryGetValue(SelectedGame.GameId, out var list))
        {
            list = [];
            _index.ReleasesByGameId[SelectedGame.GameId] = list;
        }
        // Replace existing release with same version+channel, otherwise add.
        var existing = list.FindIndex(r => r.Version == release.Version && r.Channel == release.Channel);
        if (existing >= 0) list[existing] = release;
        else list.Add(release);

        SelectedGame.RefreshReleases(list);

        // The dialog persists the source repo to config on save. Reflect it back in the form.
        var savedRepo = _configService.GetGameSourceRepo(_projectPath, SelectedGame.GameId);
        if (!string.IsNullOrWhiteSpace(savedRepo))
            SelectedGame.PerGameSourceRepo = savedRepo;

        MarkDirty();
        StatusMessage = $"Release v{release.Version} ({release.Channel}) added.";

        await PublishAfterReleaseChangeAsync(
            $"Add {SelectedGame.DisplayName} v{release.Version} ({release.Channel})");
    }

    [RelayCommand]
    private async Task EditSelectedReleaseAsync()
    {
        if (SelectedGame?.SelectedRelease == null) return;
        if (!TryValidateGameInstaller(SelectedGame)) return;
        var existing = SelectedGame.SelectedRelease;

        var initialSourceRepo = _configService.GetGameSourceRepo(_projectPath, SelectedGame.GameId)
            ?? SelectedGame.PerGameSourceRepo;
        if (!TryBuildDependencies(SelectedGame, out var deps))
            return;
        if (!TryBuildScripts(SelectedGame, out var scriptInputs))
            return;

        var updated = _showReleaseDialog(
            SelectedGame.GameId,
            SelectedGame.DisplayName,
            _index.PluginId,
            _projectPath,
            initialSourceRepo,
            AvailableGitHubRepos,
            deps,
            scriptInputs!,
            existing);

        if (updated == null) return;

        if (!_index.ReleasesByGameId.TryGetValue(SelectedGame.GameId, out var list))
        {
            list = [];
            _index.ReleasesByGameId[SelectedGame.GameId] = list;
        }

        // Remove the original by its identity (version+channel may have changed during edit)
        // and add the updated record. If identity stayed the same, this is just a replace.
        list.RemoveAll(r => r.Version == existing.Version && r.Channel == existing.Channel);
        var clash = list.FindIndex(r => r.Version == updated.Version && r.Channel == updated.Channel);
        if (clash >= 0) list[clash] = updated;
        else list.Add(updated);

        SelectedGame.RefreshReleases(list);
        MarkDirty();
        StatusMessage = $"Release v{updated.Version} ({updated.Channel}) updated.";

        await PublishAfterReleaseChangeAsync(
            $"Update {SelectedGame.DisplayName} v{updated.Version} ({updated.Channel})");
    }

    [RelayCommand]
    private async Task RemoveSelectedReleaseAsync()
    {
        if (SelectedGame?.SelectedRelease == null) return;
        var rel = SelectedGame.SelectedRelease;
        if (!_confirmDialog("Remove release",
            $"Remove v{rel.Version} ({rel.Channel}) from {SelectedGame.DisplayName}?\n\n" +
            "This will also save and push the index to GitHub (you'll be asked to confirm the push)."))
            return;

        if (_index.ReleasesByGameId.TryGetValue(SelectedGame.GameId, out var list))
        {
            list.RemoveAll(r => r.Version == rel.Version && r.Channel == rel.Channel);
            SelectedGame.RefreshReleases(list);
        }
        MarkDirty();

        // Match Add/Edit flows so the removal actually reaches users — without this, the
        // local index reflects the removal but GitHub still serves the old release and
        // managers continue to show it as installable.
        await PublishAfterReleaseChangeAsync(
            $"Remove {SelectedGame.DisplayName} v{rel.Version} ({rel.Channel})");
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        // Save + prompt-to-push, matching the Add/Edit/Remove-release flows. Without this,
        // changes that don't go through the release dialog (deps, filters, scripts, author
        // info) only get committed locally and the manager keeps fetching the stale GitHub
        // index — so dep auto-install silently no-ops, filters don't update, etc. The user
        // can still click No on the push prompt to keep the old "save only" behavior.
        await PublishAfterReleaseChangeAsync(SuggestCommitMessage());
    }

    [RelayCommand]
    private void EditServerUploadSettings() => _showServerUploadSettingsDialog();

    [RelayCommand]
    private void EditAuthorInfo()
    {
        var result = _showAuthorInfoDialog(_index.PluginId, _index.Author);
        if (result == null) return;

        _index = new PluginRepoIndex
        {
            PluginId = _index.PluginId,
            RepoVersion = _index.RepoVersion,
            GeneratedAt = _index.GeneratedAt,
            Games = _index.Games,
            ReleasesByGameId = _index.ReleasesByGameId,
            Author = result
        };
        MarkDirty();
        StatusMessage = "Author info updated. Click Save to persist.";
    }

    private void CommitGameEditsToModel()
    {
        foreach (var item in Games)
        {
            item.WriteBackTo(_index);
            // The per-game GitHub repo is author-only metadata: kept in author config,
            // never written into the public index.json.
            if (!string.IsNullOrWhiteSpace(item.PerGameSourceRepo))
                _configService.SetGameSourceRepo(_projectPath, item.GameId, item.PerGameSourceRepo);

            // Same story for the absolute paths picked via Browse on the Scripts tab —
            // private to the author's machine, persisted via author config so they survive
            // restarts, never serialized into index.json.
            _configService.SetGameScriptSources(_projectPath, item.GameId, new GameScriptSources
            {
                PreInstall = item.PreInstallScript.AbsoluteSourcePath,
                PostInstall = item.PostInstallScript.AbsoluteSourcePath,
                PostUninstall = item.PostUninstallScript.AbsoluteSourcePath
            });
        }
    }

    /// <summary>
    /// Pulls the three lifecycle script slots off the game's editor view-models. Each editor's
    /// <see cref="LifecycleScriptEditorViewModel.ToModel"/> throws when the slot is enabled but
    /// missing required text — surface that clearly to the author and abort the release dialog
    /// rather than crashing the call. The returned <see cref="LifecycleScriptInputs"/> pairs
    /// each public script with the absolute source path the author picked via Browse so the
    /// builder can always bundle the file (Browse paths can live outside the source folder).
    /// </summary>
    /// <summary>
    /// Builds the dependency models for a release dialog, surfacing validation errors (bad SHA,
    /// absolute/traversing target folder, non-leaf target file name) as an info dialog instead of
    /// letting the exception escape the relay command — an unhandled throw there closes the app.
    /// </summary>
    private bool TryBuildDependencies(GameItemViewModel game, out List<Dependency> deps)
    {
        deps = [];
        try
        {
            deps = game.Dependencies.Select(d => d.ToModel()).ToList();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _showInfoDialog("Dependency needs fixing",
                $"{ex.Message}\n\nFix it on the Dependencies tab, then try again.");
            return false;
        }
    }

    private bool TryBuildScripts(GameItemViewModel game, out LifecycleScriptInputs? inputs)
    {
        inputs = null;
        try
        {
            inputs = new LifecycleScriptInputs(
                PreInstall: game.PreInstallScript.ToModel(),
                PreInstallSourcePath: game.PreInstallScript.AbsoluteSourcePath,
                PostInstall: game.PostInstallScript.ToModel(),
                PostInstallSourcePath: game.PostInstallScript.AbsoluteSourcePath,
                PostUninstall: game.PostUninstallScript.ToModel(),
                PostUninstallSourcePath: game.PostUninstallScript.AbsoluteSourcePath);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _showInfoDialog("Lifecycle script incomplete",
                $"{ex.Message}\n\nFix it on the Scripts tab, then try again.");
            return false;
        }
    }

    /// <summary>
    /// A game whose game-installer is a portable app (emulator) needs an Exe name: it's the Play
    /// target and the key by which a second game reuses the same emulator install (F1-B). Warn and
    /// abort the release cleanly if it's missing. See EMULATOR_INSTALL_QUESTIONS.md.
    /// </summary>
    private bool TryValidateGameInstaller(GameItemViewModel game)
    {
        var portableApp = game.Dependencies.FirstOrDefault(
            d => d.IsGameInstaller && d.AutoInstallEnabled && d.AutoInstallKind == "extractApp");
        if (portableApp == null) return true;

        if (string.IsNullOrWhiteSpace(game.ExeName))
        {
            _showInfoDialog("Exe name required",
                $"\"{game.DisplayName}\" installs a portable app (emulator), so it needs an Exe name " +
                "on the General tab — it's the Play target and how a second game reuses the same " +
                "emulator install. Set it, then try again.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(portableApp.FixDownloadUrl))
        {
            _showInfoDialog("Download URL required",
                $"\"{game.DisplayName}\" installs a portable app (emulator), so its game-installer " +
                "dependency needs the emulator ZIP's HTTPS download URL. Set it on the Dependencies " +
                "tab (and \"Fetch from URL\" for the SHA256), then try again.");
            return false;
        }
        return true;
    }

    [RelayCommand]
    private async Task PushToGitHubAsync()
    {
        if (HasUnsavedChanges)
        {
            _showInfoDialog("Unsaved changes",
                "You have unsaved changes. Click Save first, then Push to GitHub.");
            return;
        }

        await PushIndexToGitHubAsync(SuggestCommitMessage(), confirmFirst: true);
    }

    /// <summary>
    /// Stages, commits, and pushes <c>index.json</c> to the plugin repo. Used by the manual
    /// "Push to GitHub" command (with confirmation) and by the auto-publish flow that runs
    /// after a release is added or edited (also with confirmation, but with a release-specific
    /// commit message). Returns silently — the caller decides what to surface.
    /// </summary>
    private async Task PushIndexToGitHubAsync(string commitMessage, bool confirmFirst)
    {
        if (!await _gitService.IsRepoAsync(_projectPath))
        {
            _showInfoDialog("Not a git repo",
                $"This folder isn't a git repository:\n{_projectPath}\n\nInitialize it with 'git init' and add a remote, or use a folder that is one.");
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Staging index.json...";
            var add = await _gitService.AddAsync(_projectPath, "index.json");
            if (!add.Success)
            {
                _showInfoDialog("git add failed", add.Combined);
                return;
            }

            var status = await _gitService.StatusPorcelainAsync(_projectPath);
            if (string.IsNullOrWhiteSpace(status.Stdout))
            {
                _showInfoDialog("Nothing to push", "Working tree is clean — nothing changed since the last commit.");
                StatusMessage = null;
                return;
            }

            if (confirmFirst &&
                !_confirmDialog("Commit and push",
                    $"Commit message:\n\n{commitMessage}\n\nProceed with commit and push?"))
            {
                StatusMessage = "Saved locally. Push to GitHub when ready.";
                return;
            }

            StatusMessage = "Committing...";
            var commit = await _gitService.CommitAsync(_projectPath, commitMessage);
            if (!commit.Success)
            {
                _showInfoDialog("git commit failed", commit.Combined);
                return;
            }

            StatusMessage = "Pushing...";
            var push = await _gitService.PushAsync(_projectPath);
            if (!push.Success)
            {
                _showInfoDialog("git push failed", push.Combined);
                return;
            }

            StatusMessage = "Pushed to GitHub.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Push to GitHub failed");
            _showInfoDialog("Push failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Writes <c>index.json</c> to disk, including all in-progress game edits. Returns false on
    /// failure (caller surfaces nothing extra; the dialog already showed an error).
    /// </summary>
    private bool TrySaveIndexToDisk()
    {
        try
        {
            CommitGameEditsToModel();
            var updated = new PluginRepoIndex
            {
                PluginId = _index.PluginId,
                RepoVersion = _index.RepoVersion,
                GeneratedAt = DateTime.UtcNow,
                Games = _index.Games,
                ReleasesByGameId = _index.ReleasesByGameId,
                Author = _index.Author
            };
            _indexFileService.Save(_projectPath, updated);
            _index = updated;
            HasUnsavedChanges = false;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Save failed");
            _showInfoDialog("Save failed", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Auto-save + auto-push prompt that runs after a release is added or edited. The release
    /// dialog only stages the new <see cref="ModRelease"/> in memory and uploads the asset to
    /// GitHub — without this, the user has to remember to also click Save and Push for the
    /// updated SHA256 to actually go live, and a stale index.json on GitHub means the manager
    /// rejects downloads with a hash mismatch. The push step still confirms first so the
    /// author can defer if they want.
    /// </summary>
    private async Task PublishAfterReleaseChangeAsync(string commitMessage)
    {
        if (!TrySaveIndexToDisk())
            return;

        await PushIndexToGitHubAsync(commitMessage, confirmFirst: true);
    }

    private string SuggestCommitMessage()
    {
        var games = _index.Games.Select(g => g.DisplayName).ToList();
        var releases = _index.ReleasesByGameId
            .SelectMany(kv => kv.Value.Select(r => $"{kv.Key} v{r.Version}"))
            .ToList();

        if (releases.Count == 1) return $"Update index: {releases[0]}";
        if (games.Count == 1 && releases.Count > 0) return $"Update {games[0]} releases";
        return "Update plugin index";
    }

    [RelayCommand]
    private void CloseProject()
    {
        if (HasUnsavedChanges)
        {
            if (!_confirmDialog("Unsaved changes",
                "You have unsaved changes. Discard and close?"))
                return;
        }
        _closeProject();
    }
}
