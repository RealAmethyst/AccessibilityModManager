using System.Collections.ObjectModel;
using System.IO;
using System.Text;
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
    private readonly ServerUploadService _serverUploadService;
    private readonly ILogger _logger;

    /// <summary>Live-index fetches (baseline capture, third-party-change check, post-publish verify).</summary>
    private static readonly System.Net.Http.HttpClient CatalogHttp = new();

    /// <summary>
    /// The live index's bytes as they were when this project OPENED — the third-party-change
    /// baseline. A live index at publish time that matches neither this nor the candidate means
    /// someone else changed it while this editor was open; publishing would clobber their work.
    /// Null when the live index couldn't be read at load (offline) — the check then softens to
    /// a confirm.
    /// </summary>
    private byte[]? _liveIndexAtLoad;
    private readonly Action<string, string> _showInfoDialog;
    private readonly Func<string, string, bool> _confirmDialog;
    private readonly Func<string, string, string?, string?> _browseForFile;
    private readonly Action _closeProject;
    private readonly Func<string, string, string, string, string?, ObservableCollection<string>, IList<Dependency>, LifecycleScriptInputs, ModRelease?, ReleaseDialogResult?> _showReleaseDialog;
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
        ServerUploadService serverUploadService,
        PatreonAuthorService patreon,
        ILogger logger,
        Action<string, string> showInfoDialog,
        Func<string, string, bool> confirmDialog,
        Func<string, string, string?, string?> browseForFile,
        Action closeProject,
        Func<string, string, string, string, string?, ObservableCollection<string>, IList<Dependency>, LifecycleScriptInputs, ModRelease?, ReleaseDialogResult?> showReleaseDialog,
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
        _serverUploadService = serverUploadService;
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
        _ = CaptureLiveIndexBaselineAsync();
    }

    private async Task CaptureLiveIndexBaselineAsync()
    {
        try
        {
            _liveIndexAtLoad = await TryFetchLiveIndexAsync();
            if (_liveIndexAtLoad is not null)
                ReconcileWithLiveIndex(_liveIndexAtLoad);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Couldn't capture the live index baseline at load");
        }
    }

    /// <summary>
    /// When the local index.json and the published one disagree at open time, the published one
    /// is what users are actually reading, so it's the copy that should win — Amethyst's call,
    /// 2026-07-25.
    /// <para>
    /// With one exception, which is the whole reason this isn't a silent overwrite: "different"
    /// covers two opposite situations. If the local file is still exactly what this machine last
    /// published, then the LIVE copy moved on without it (published from somewhere else) and
    /// adopting it loses nothing. But if the local file has been edited since that publish, those
    /// edits are unpublished work, and taking the live copy would throw them away. That case asks
    /// first, and defaults to keeping the local draft.
    /// </para>
    /// </summary>
    private void ReconcileWithLiveIndex(byte[] live)
    {
        var indexPath = Path.Combine(_projectPath, "index.json");
        byte[] local;
        try
        {
            local = File.ReadAllBytes(indexPath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't read the local index to compare it with the live one");
            return;
        }

        if (local.AsSpan().SequenceEqual(live)) return;

        var localSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(local));
        var lastPublished = _configService.GetLastPublishedIndexSha(_projectPath);
        var localIsUnpublishedWork =
            lastPublished is null ||
            !string.Equals(localSha, lastPublished, StringComparison.OrdinalIgnoreCase);

        if (localIsUnpublishedWork &&
            !_confirmDialog("Your copy and the published one differ",
                "The index published on your server isn't the same as the copy in this folder, and " +
                "this folder has changes that were never published.\n\n" +
                "Taking the published copy would discard those local changes. Keeping yours means the " +
                "published index stays as it is until you publish.\n\n" +
                "Replace this folder's copy with the published one?"))
        {
            StatusMessage = "Kept your local copy. It differs from what's published until you publish it.";
            return;
        }

        try
        {
            File.WriteAllBytes(indexPath, live);
            _index = LoadOrThrow();
            RebuildGameList();
            if (Games.Count > 0) SelectedGame = Games[0];
            HasUnsavedChanges = false;
            _configService.SetLastPublishedIndexSha(_projectPath, Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(live)));
            StatusMessage = "Loaded the published index — this folder's copy was out of date.";
            _logger.Information("Adopted the live index for {Project}", _projectPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Couldn't adopt the live index for {Project}", _projectPath);
            _showInfoDialog("Couldn't take the published copy",
                $"{ex.Message}\n\nThis folder's copy is unchanged.");
        }
    }

    /// <summary>The plugin's canonical live index URL, derived from the registry's fixed home.</summary>
    private Uri LiveIndexUrl => new(RegistryMembershipChecker.RegistryUrl,
        $"plugins/{Uri.EscapeDataString(_index.PluginId)}/index.json");

    /// <summary>
    /// Checks that the signed registry actually sends managers to the address this editor
    /// publishes to. Returns null when they agree (or when the registry can't be read, which is
    /// its own visible problem elsewhere), otherwise a description for the author.
    /// </summary>
    private async Task<string?> FindRegistryIndexUrlMismatchAsync()
    {
        RegistryMembershipResult membership;
        try
        {
            membership = await _registryChecker.CheckAsync(_index.PluginId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't read the registry to compare index URLs; continuing");
            return null;
        }

        if (!membership.RegistryReachable || membership.Entry?.RepoIndexUrl is not { } registered)
            return null;

        var target = LiveIndexUrl;

        // Scheme and host are case-insensitive by definition; the PATH is not — the catalog is
        // served off a Linux filesystem, where /plugins/Amethyst/ and /plugins/amethyst/ are two
        // different places. Comparing them loosely would call a real mismatch a match.
        var sameOrigin = Uri.Compare(registered, target,
            UriComponents.Scheme | UriComponents.HostAndPort,
            UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
        var samePath = Uri.Compare(registered, target,
            UriComponents.PathAndQuery,
            UriFormat.Unescaped, StringComparison.Ordinal) == 0;
        if (sameOrigin && samePath) return null;

        return $"The signed registry tells managers to read '{_index.PluginId}' from:\n\n{registered}\n\n" +
               $"but publishing here would write to:\n\n{target}\n\n" +
               "Publishing now would look like it worked while every manager kept reading the old address. " +
               "Update the plugin's index URL in the registry admin screen (then sign and publish the " +
               "registry) so the two match. Nothing was uploaded.";
    }

    /// <summary>
    /// Whether a download address actually answers.
    /// <para>
    /// Deliberately a GET, not a HEAD: the download server answers GET (401 for a gated file, 404
    /// for a missing one) but rejects HEAD with 405, so a HEAD check would report every healthy
    /// release as unreachable. Reading response headers only means the package body is never
    /// pulled down just to prove it's there.
    /// </para>
    /// </summary>
    private async Task<bool> PublicUrlServesSomethingAsync(string url)
    {
        try
        {
            using var response = await CatalogHttp.GetAsync(
                url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't reach the public download address {Url}", url);
            return false;
        }
    }

    /// <summary>
    /// Remembers exactly what went live, so a later open can tell "this folder is stale" apart
    /// from "this folder has work that was never published". Best-effort: failing to record it
    /// only costs the next open a question it could otherwise have answered itself.
    /// </summary>
    private void RecordPublishedIndex(byte[] published)
    {
        try
        {
            _configService.SetLastPublishedIndexSha(_projectPath, Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(published)));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't record the published index fingerprint");
        }
    }

    private async Task<byte[]?> TryFetchLiveIndexAsync()
    {
        try
        {
            var url = LiveIndexUrl;
            var separator = string.IsNullOrEmpty(url.Query) ? "?" : "&";
            var busted = new Uri(url.AbsoluteUri + separator + "_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var resp = await CatalogHttp.GetAsync(busted);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch
        {
            return null;
        }
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
                RegistryStatusText = $"Plugin id '{_index.PluginId}' is not in the public registry yet — add it from the registry admin screen.";
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

    /// <summary>
    /// Asks the author to confirm something during a save-back. Kept internal so
    /// <see cref="GameItemViewModel"/> can warn about a rename that will break already-published
    /// packages, without knowing anything about dialogs.
    /// </summary>
    internal bool ConfirmDuringSave(string title, string message) => _confirmDialog(title, message);

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

        var dialogResult = _showReleaseDialog(
            SelectedGame.GameId,
            SelectedGame.DisplayName,
            _index.PluginId,
            _projectPath,
            initialSourceRepo,
            AvailableGitHubRepos,
            deps,
            scriptInputs!,
            null);

        if (dialogResult == null) return;
        var release = dialogResult.Release;

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
            $"Add {SelectedGame.DisplayName} v{release.Version} ({release.Channel})",
            dialogResult.GateChange);
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

        var dialogResult = _showReleaseDialog(
            SelectedGame.GameId,
            SelectedGame.DisplayName,
            _index.PluginId,
            _projectPath,
            initialSourceRepo,
            AvailableGitHubRepos,
            deps,
            scriptInputs!,
            existing);

        if (dialogResult == null) return;
        var updated = dialogResult.Release;

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
            $"Update {SelectedGame.DisplayName} v{updated.Version} ({updated.Channel})",
            dialogResult.GateChange);
    }

    [RelayCommand]
    private async Task RemoveSelectedReleaseAsync()
    {
        if (SelectedGame?.SelectedRelease == null) return;
        var rel = SelectedGame.SelectedRelease;
        if (!_confirmDialog("Remove release",
            $"Remove v{rel.Version} ({rel.Channel}) from {SelectedGame.DisplayName}?\n\n" +
            "This will also save and publish the index to your server (you'll be asked to confirm)."))
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
        // Save + prompt-to-publish, matching the Add/Edit/Remove-release flows. Without this,
        // changes that don't go through the release dialog (deps, filters, scripts, author
        // info) only land on disk and the manager keeps fetching the stale live index — so
        // dep auto-install silently no-ops, filters don't update, etc. The user can still
        // click No on the publish prompt to keep the old "save only" behavior.
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
    private async Task PublishIndexAsync()
    {
        if (HasUnsavedChanges)
        {
            _showInfoDialog("Unsaved changes",
                "You have unsaved changes. Click Save first, then Publish index.");
            return;
        }

        await PublishIndexToServerAsync(SuggestCommitMessage(), confirmFirst: true);
    }

    /// <summary>
    /// Publishes <c>index.json</c> to the author's server (the catalog's canonical home since
    /// GitHub retired): strict manager-grade validation, a third-party-change check against the
    /// live index, staged upload with an atomic rename, then verification from the PUBLIC url.
    /// A local git commit records history afterwards, best-effort — git state never decides
    /// whether the publish happened; the live remote does.
    /// </summary>
    /// <returns>
    /// True when the live catalog now matches the local index — either because this publish
    /// switched it, or because it was already identical. Callers use it to gate work that must
    /// only happen once users can see the change.
    /// </returns>
    private async Task<bool> PublishIndexToServerAsync(string commitMessage, bool confirmFirst)
    {
        var indexPath = Path.Combine(_projectPath, "index.json");
        byte[] candidate;
        try
        {
            candidate = File.ReadAllBytes(indexPath);
        }
        catch (Exception ex)
        {
            _showInfoDialog("Can't read index.json", ex.Message);
            return false;
        }

        // The manager's own validation, strictly: publishing something users' managers would
        // refuse (or silently drop) is an authoring error caught HERE, not in the field.
        try
        {
            var report = AccessibilityModManager.Infrastructure.Services.PluginIndexValidation
                .Validate(_index.PluginId, Encoding.UTF8.GetString(candidate));
            var problems = report.TrustErrors.Concat(report.UnobtainableReleases).ToList();
            if (problems.Count > 0)
            {
                const int shown = 6;
                var text = string.Join("\n\n", problems.Take(shown));
                if (problems.Count > shown) text += $"\n\n...and {problems.Count - shown} more.";
                _showInfoDialog("Fix the index before publishing", text);
                return false;
            }
        }
        catch (Exception ex)
        {
            _showInfoDialog("Index doesn't validate", ex.Message);
            return false;
        }

        var cfg = _configService.GetServerUploadConfig();
        if (cfg is null)
        {
            _showInfoDialog("Server upload not configured",
                "Publishing sends index.json to your server over SFTP. Set up Server upload " +
                "settings (host, key, host key fingerprint) first.");
            return false;
        }

        IsBusy = true;
        try
        {
            // Publishing to an address nobody reads is the quietest possible failure: the tool
            // would upload, verify the upload from the public URL, and report success while every
            // manager went on fetching the address the SIGNED registry names. Compare the two
            // before touching anything.
            if (await FindRegistryIndexUrlMismatchAsync() is { } mismatch)
            {
                _showInfoDialog("The registry points somewhere else", mismatch);
                return false;
            }

            // Third-party-change check: the live index should be either what was live when this
            // project opened, or already the candidate (an interrupted earlier publish).
            var live = await TryFetchLiveIndexAsync();
            if (live is not null && live.AsSpan().SequenceEqual(candidate))
            {
                StatusMessage = "The live index is already identical. Nothing to publish.";
                _liveIndexAtLoad = candidate;
                RecordPublishedIndex(candidate);
                return true;
            }
            if (live is not null && _liveIndexAtLoad is not null &&
                !live.AsSpan().SequenceEqual(_liveIndexAtLoad))
            {
                if (!_confirmDialog("The live index changed",
                    "The index on the server is different from when this project was opened — another " +
                    "publish happened in between. Publishing now REPLACES the server's copy with yours.\n\n" +
                    "Replace it anyway?"))
                {
                    StatusMessage = "Publish cancelled — the server's index was left alone.";
                    return false;
                }
            }

            if (confirmFirst &&
                !_confirmDialog("Publish index",
                    $"This uploads index.json for '{_index.PluginId}' to {cfg.Host} and switches it live " +
                    $"atomically. Managers see the change on their next refresh.\n\nChange: {commitMessage}\n\nProceed?"))
            {
                StatusMessage = "Saved locally. Publish index when ready.";
                return false;
            }

            StatusMessage = "Publishing index...";
            await _serverUploadService.PublishIndexAsync(cfg, _index.PluginId, candidate, CancellationToken.None);

            var verify = await TryFetchLiveIndexAsync();
            if (verify is null || !verify.AsSpan().SequenceEqual(candidate))
            {
                _showInfoDialog("Published, but verification failed",
                    "The index uploaded and switched live, but reading it back from the public address " +
                    "didn't return the same bytes. Publish again; if it persists, check the server.");
                return false;
            }

            _liveIndexAtLoad = candidate;
            RecordPublishedIndex(candidate);
            StatusMessage = "Published the index and verified it live from the public address.";

            // Local history, best-effort — a git hiccup must never repaint a live publish as failed.
            try
            {
                if (await _gitService.IsRepoAsync(_projectPath))
                {
                    await _gitService.AddAsync(_projectPath, "index.json");
                    var status = await _gitService.StatusPorcelainAsync(_projectPath);
                    if (!string.IsNullOrWhiteSpace(status.Stdout))
                        await _gitService.CommitAsync(_projectPath, commitMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Local history commit after index publish failed");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Index publish failed");
            _showInfoDialog("Publish failed — the live index is unchanged", ex.Message);
            return false;
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
    /// Auto-save + auto-publish prompt that runs after a release is added or edited. The
    /// release dialog only stages the new <see cref="ModRelease"/> in memory and uploads the
    /// asset — without this, the user has to remember to also click Save and Publish for the
    /// updated SHA256 to actually go live, and a stale live index means the manager rejects
    /// downloads with a hash mismatch. The publish step still confirms first so the author can
    /// defer if they want.
    /// </summary>
    private async Task PublishAfterReleaseChangeAsync(
        string commitMessage, PendingGateChange? gateChange = null)
    {
        if (!TrySaveIndexToDisk())
            return;

        var catalogMatches = await PublishIndexToServerAsync(commitMessage, confirmFirst: true);

        if (gateChange == null) return;

        // What the server enforces changes here and nowhere else: only once the public catalog
        // describes the release does the enforcement follow it. A declined or failed publish
        // leaves both the live index and the server as they were — consistent with each other —
        // and saving the release again picks the change back up.
        if (!catalogMatches)
        {
            StatusMessage = gateChange.Gate == null
                ? "The catalog wasn't updated, so the release is still patrons-only on your server. " +
                  "Publish the index to finish making it public."
                : "The catalog wasn't updated, so your server still enforces the old tiers. Publish the " +
                  "index to apply the change.";
            return;
        }

        var cfg = _configService.GetServerUploadConfig();
        if (cfg == null) return;

        try
        {
            if (gateChange.Gate == null)
            {
                await _serverUploadService.RemoveGateAsync(
                    cfg, gateChange.GameId, gateChange.Version, CancellationToken.None);

                // Now that it's actually public, the address in the index can finally be proved —
                // the one check a still-locked release couldn't run for itself.
                if (!string.IsNullOrWhiteSpace(gateChange.PublicUrl) &&
                    !await PublicUrlServesSomethingAsync(gateChange.PublicUrl!))
                {
                    _showInfoDialog("Published, but the download address doesn't answer",
                        $"The release is public now, but {gateChange.PublicUrl} didn't return the file.\n\n" +
                        "Users following your catalog would get nothing. Check that the public base URL and " +
                        "the remote releases path in Server upload settings describe the same place.");
                    StatusMessage = "Published, but the public download address didn't answer.";
                    return;
                }

                StatusMessage = "Published the index, and the release is now public on your server.";
            }
            else
            {
                await _serverUploadService.PublishGateOnlyAsync(
                    cfg, gateChange.GameId, gateChange.Version, gateChange.Gate, CancellationToken.None);
                StatusMessage = "Published the index, and your server now enforces the new tiers.";
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Applying the tier change failed for {Game} v{Version}",
                gateChange.GameId, gateChange.Version);
            _showInfoDialog("The catalog is live, but your server wasn't updated",
                $"Your index now describes {gateChange.GameId} {gateChange.Version}, but changing what the " +
                $"server enforces for it failed:\n\n{ex.Message}\n\n" +
                (gateChange.Gate == null
                    ? "Until that's cleared, patrons can download it and everyone else is turned away."
                    : "Until then, your server still enforces the old tiers.") +
                "\n\nSave the release again to retry.");
        }
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
