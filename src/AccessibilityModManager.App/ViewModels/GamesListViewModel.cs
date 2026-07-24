using System.Collections.ObjectModel;
using System.Diagnostics;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Patreon;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.App.ViewModels;

public partial class GamesListViewModel : ObservableObject
{
    private readonly IPluginRegistryClient _registryClient;
    private readonly IPluginRepoClient _repoClient;
    private readonly IConfigService _configService;
    private readonly IReceiptStore _receiptStore;
    private readonly IGameVerifier _gameVerifier;
    private readonly GameAggregator _gameAggregator;
    private readonly PatreonService _patreon;
    private readonly ILogger _logger;
    private readonly Action<GameInstall, Dictionary<string, PluginRepoIndex>> _navigateToDetails;
    /// <summary>
    /// Opens Game Details for a game that isn't installed yet but declares a game-installer
    /// dependency (so the user can install the game + mod in one flow). Args: game definition,
    /// owning plugin id, scoped indexes. Wired in App.xaml.cs.
    /// </summary>
    private readonly Action<GameDefinition, string, Dictionary<string, PluginRepoIndex>> _navigateToDetailsUninstalled;
    /// <summary>
    /// Returns the user-selected folder, or null if cancelled. The string param is an optional
    /// initial directory. Wired to <c>Microsoft.Win32.OpenFolderDialog</c> in App.xaml.cs.
    /// </summary>
    private readonly Func<string?, string?> _browseForFolder;

    // Cached after last refresh, so navigation to details can use them
    private Dictionary<string, PluginRepoIndex> _lastActiveIndexes = [];
    private List<GameInstall> _lastInstalls = [];
    private Dictionary<string, List<(string PluginId, GameDefinition Game)>> _lastGameMap = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _matchCountText;

    public ObservableCollection<ModItemViewModel> Mods { get; } = [];

    /// <summary>Source-of-truth list. <see cref="Mods"/> is the filtered view bound to UI.</summary>
    private readonly List<ModItemViewModel> _allMods = [];

    public ObservableCollection<TagFilterItem> TagFilters { get; } = [];
    public ObservableCollection<LanguageFilterItem> LanguageFilters { get; } = [];
    public ObservableCollection<AuthorFilterItem> AuthorFilters { get; } = [];

    public bool HasAnyFilterSelected =>
        TagFilters.Any(f => f.IsSelected) ||
        LanguageFilters.Any(f => f.IsSelected) ||
        AuthorFilters.Any(f => f.IsSelected);

    public GamesListViewModel(
        IPluginRegistryClient registryClient,
        IPluginRepoClient repoClient,
        IConfigService configService,
        IReceiptStore receiptStore,
        IGameVerifier gameVerifier,
        GameAggregator gameAggregator,
        PatreonService patreon,
        ILogger logger,
        Action<GameInstall, Dictionary<string, PluginRepoIndex>> navigateToDetails,
        Action<GameDefinition, string, Dictionary<string, PluginRepoIndex>> navigateToDetailsUninstalled,
        Func<string?, string?> browseForFolder)
    {
        _registryClient = registryClient;
        _repoClient = repoClient;
        _configService = configService;
        _receiptStore = receiptStore;
        _gameVerifier = gameVerifier;
        _gameAggregator = gameAggregator;
        _patreon = patreon;
        _logger = logger;
        _navigateToDetails = navigateToDetails;
        _navigateToDetailsUninstalled = navigateToDetailsUninstalled;
        _browseForFolder = browseForFolder;

        // When Patreon membership data finishes loading at startup (or refreshes after a
        // sign-in/sign-out), re-render the catalog so newly-visible gated releases show
        // up without requiring the user to click Refresh manually.
        _patreon.SignInStateChanged += OnPatreonStateChanged;
    }

    private void OnPatreonStateChanged()
    {
        // RefreshGamesCommand re-fetches the full registry which is overkill for "just
        // recompute visibility." But the existing aggregation is fast and this only fires
        // a few times per session, so the simplicity wins.
        if (RefreshGamesCommand.CanExecute(null))
        {
            _ = RefreshGamesCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task RefreshGamesAsync(CancellationToken ct)
    {
        IsLoading = true;
        StatusMessage = "Detecting mods...";

        try
        {
            var config = await _configService.LoadAsync();
            var registry = await _registryClient.FetchRegistryAsync(new Uri(config.PluginRegistryUrl), ct);

            // Every registry-listed plugin is active. We don't filter by an "enabled" state
            // anymore — registry membership IS the gate.
            var activeIndexes = new Dictionary<string, PluginRepoIndex>();
            foreach (var plugin in registry.Plugins)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var index = await _repoClient.FetchPluginIndexAsync(plugin, ct);
                    activeIndexes[plugin.Id] = index;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to fetch index for plugin {PluginId}", plugin.Id);
                }
            }

            var installs = await _gameAggregator.DetectAllGamesAsync(activeIndexes, config.KnownGameOverrides, ct);

            _lastActiveIndexes = activeIndexes;
            _lastInstalls = installs;
            _lastGameMap = GameAggregator.GetGamesByGameId(activeIndexes);
            _allMods.Clear();
            Mods.Clear();

            // One row per mod = one row per (developer, game) pair. Same shape as Developer Details.
            var rows = new List<ModItemViewModel>();
            foreach (var (pluginId, index) in activeIndexes)
            {
                foreach (var game in index.Games)
                {
                    // Skip games the developer has declared but hasn't published any release for
                    // yet — there's no "mod" to install, so listing them would be confusing.
                    if (!index.ReleasesByGameId.TryGetValue(game.GameId, out var releases) ||
                        releases.Count == 0)
                    {
                        continue;
                    }

                    // Q3=A: hide the entire row if every release is Patreon-gated and the
                    // user can't see any of them. Mods with at least one public (or
                    // entitled-Patreon) release stay visible — only fully-locked mods
                    // disappear from the catalog.
                    if (!releases.Any(r => IsReleaseVisibleToUser(r)))
                    {
                        continue;
                    }

                    var install = installs.FirstOrDefault(i => i.Game.GameId == game.GameId && i.IsValid);
                    var receipt = await _receiptStore.LoadAsync(game.GameId, pluginId);

                    var latestVersion = releases
                        .Where(r => r.Channel == config.DefaultChannel)
                        .OrderByDescending(r => r.Version, VersionComparer.Instance)
                        .FirstOrDefault()?.Version;

                    var hasUpdate = receipt != null && latestVersion != null &&
                                    VersionComparer.Instance.Compare(latestVersion, receipt.InstalledVersion) > 0;

                    var modName = !string.IsNullOrWhiteSpace(game.ModName)
                        ? game.ModName!
                        : DeriveModName(releases);

                    rows.Add(new ModItemViewModel
                    {
                        GameId = game.GameId,
                        GameDisplayName = game.DisplayName,
                        ModName = modName,
                        PluginId = pluginId,
                        IsDetected = install != null,
                        HasGameInstaller = game.Dependencies.Any(d => d.IsGameInstaller),
                        InstallPath = install?.InstallPath,
                        InstalledVersion = receipt?.InstalledVersion,
                        HasUpdate = hasUpdate,
                        Tags = game.Tags.ToList(),
                        Languages = game.Languages.ToList()
                    });
                }
            }

            // Sort alphabetically by game, then by mod name within a game.
            foreach (var row in rows
                .OrderBy(r => r.GameDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.ModName, StringComparer.OrdinalIgnoreCase))
            {
                _allMods.Add(row);
            }

            RebuildFilters(config);
            ApplyFilters();

            var detected = _allMods.Count(m => m.IsDetected);
            StatusMessage = $"Found {_allMods.Count} mod{(_allMods.Count == 1 ? "" : "s")} ({detected} detected).";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Detection cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to detect mods");
            StatusMessage = $"Failed to detect mods: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Q3=A: a release is visible if it isn't Patreon-gated, or if the user is currently
    /// entitled to one of the gate's tiers. Per Q6=C the gate is purely per-release —
    /// channel-default schema was dropped. Creators who own the gate's campaign also see
    /// their own gated releases so they can install via the local-file path.
    /// </summary>
    private bool IsReleaseVisibleToUser(ModRelease release)
    {
        if (release.Patreon == null) return true;
        if (_patreon.IsCampaignOwner(release.Patreon.CampaignId)) return true;
        return _patreon.IsEntitled(release.Patreon);
    }

    /// <summary>
    /// Best-effort human name for a mod, parsed from its first release's package URL. For
    /// GitHub-hosted releases this is the source repo name (e.g. "DigimonNOAccess"). Falls back
    /// to "mod" when the URL doesn't match a github.com release pattern. Same logic as
    /// ModReleaseGroup.ModName / DeveloperDetailsViewModel — keep them in sync.
    /// </summary>
    private static string DeriveModName(IReadOnlyList<ModRelease> releases)
    {
        if (releases.Count == 0) return "mod";
        // Skip Patreon-gated releases — their PackageUrl is null. Falls back to "mod" if all
        // releases are gated, which is the desired behavior for a Patron-only mod anyway.
        var url = releases.Select(r => r.PackageUrl).FirstOrDefault(u => u is not null);
        return DeriveModNameFromUrl(url);
    }

    private static string DeriveModNameFromUrl(Uri? url)
    {
        if (url is not null && string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = url.Segments;
            if (segments.Length >= 4 &&
                segments[3].TrimEnd('/').Equals("releases", StringComparison.OrdinalIgnoreCase))
            {
                return segments[2].TrimEnd('/');
            }
        }
        return "mod";
    }

    [RelayCommand]
    private void OpenGameDetails(ModItemViewModel? mod)
    {
        if (mod == null) return;
        // Detected games open normally; an undetected game opens only if it can install itself.
        if (!mod.IsDetected && !mod.HasGameInstaller) return;

        // Scope the navigation payload to this mod's developer so Game Details shows just one
        // mod card (not every developer's mods for that game). Matches Developer Details flow.
        if (!_lastActiveIndexes.TryGetValue(mod.PluginId, out var pluginIndex)) return;
        var scoped = new Dictionary<string, PluginRepoIndex> { [mod.PluginId] = pluginIndex };

        if (mod.IsDetected)
        {
            var install = _lastInstalls.FirstOrDefault(i => i.Game.GameId == mod.GameId && i.IsValid);
            if (install == null) return;
            _navigateToDetails(install, scoped);
        }
        else
        {
            // Not installed yet, but the game declares a game-installer dependency: open Game
            // Details in the not-installed state so the user can pick a version and Install —
            // which runs the game installer first, then the mod.
            var def = pluginIndex.Games.FirstOrDefault(g => g.GameId == mod.GameId);
            if (def == null) return;
            _navigateToDetailsUninstalled(def, mod.PluginId, scoped);
        }
    }

    [RelayCommand]
    private void OpenGameFolder(ModItemViewModel? mod)
    {
        if (string.IsNullOrEmpty(mod?.InstallPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = mod.InstallPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to open game folder");
            StatusMessage = $"Could not open folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Browse to a folder and use it as the install path for an undetected game.
    /// The folder must pass the game's probe rules (exe presence, etc.) — otherwise the
    /// override is rejected so users can't accidentally point at the wrong game.
    /// </summary>
    [RelayCommand]
    private async Task BrowseForGameAsync(ModItemViewModel? mod)
    {
        if (mod == null) return;
        if (!_lastGameMap.TryGetValue(mod.GameId, out var pluginGames) || pluginGames.Count == 0)
        {
            StatusMessage = "Game definition not loaded — try refreshing first.";
            return;
        }

        var pickedPath = _browseForFolder(null);
        if (string.IsNullOrEmpty(pickedPath)) return;

        // Verify against any plugin's definition for this game; the first one that accepts the
        // path wins. (All definitions for the same gameId should agree on probe rules in practice.)
        var validatingDefinition = pluginGames
            .Select(pg => pg.Game)
            .FirstOrDefault(g => _gameVerifier.VerifyInstallPath(g, pickedPath));

        if (validatingDefinition == null)
        {
            var firstName = pluginGames[0].Game.DisplayName;
            StatusMessage = $"That folder doesn't look like a {firstName} install — " +
                            "expected files were not found. Override not saved.";
            _logger.Warning("Browse rejected for {GameId} at {Path}: probe rules failed", mod.GameId, pickedPath);
            return;
        }

        try
        {
            var config = await _configService.LoadAsync();
            config.KnownGameOverrides[mod.GameId] = pickedPath;
            await _configService.SaveAsync(config);
            _logger.Information("Saved manual override for {GameId}: {Path}", mod.GameId, pickedPath);

            StatusMessage = $"Saved location for {validatingDefinition.DisplayName}. Refreshing...";
            await RefreshGamesCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save game override");
            StatusMessage = $"Could not save location: {ex.Message}";
        }
    }

    private void RebuildFilters(AppConfig config)
    {
        var savedTags = new HashSet<string>(config.SelectedTagFilters, StringComparer.OrdinalIgnoreCase);
        var savedLangs = new HashSet<string>(config.SelectedLanguageFilters, StringComparer.OrdinalIgnoreCase);
        var savedAuthors = new HashSet<string>(config.SelectedAuthorFilters, StringComparer.OrdinalIgnoreCase);

        TagFilters.Clear();
        foreach (var tag in TagCatalog.Core)
        {
            TagFilters.Add(new TagFilterItem(
                tag.Id, tag.Label, tag.Category,
                isSelected: savedTags.Contains(tag.Id),
                onToggle: OnFilterToggled));
        }
        // Custom tags found across loaded mods get appended after the core list.
        var customTagIds = _allMods
            .SelectMany(m => m.Tags)
            .Where(id => TagCatalog.FindById(id) == null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        foreach (var customId in customTagIds)
        {
            TagFilters.Add(new TagFilterItem(
                customId, customId, "Custom",
                isSelected: savedTags.Contains(customId),
                onToggle: OnFilterToggled));
        }

        LanguageFilters.Clear();
        // Always show the curated language catalog so users can pick filters even when no
        // loaded mod has declared a language yet. Mirrors the Tags section where the core
        // tag list is always available regardless of what mods have selected.
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in LanguageCatalog.All)
        {
            LanguageFilters.Add(new LanguageFilterItem(
                lang.Code, lang.Label,
                isSelected: savedLangs.Contains(lang.Code),
                onToggle: OnFilterToggled));
            seenCodes.Add(lang.Code);
        }
        // Append any extra codes that some mod declared but the catalog doesn't list, so we
        // never silently hide a filter the user might be looking for.
        var extras = _allMods
            .SelectMany(m => m.Languages)
            .Where(c => !seenCodes.Contains(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase);
        foreach (var code in extras)
        {
            LanguageFilters.Add(new LanguageFilterItem(
                code, LanguageCatalog.LabelFor(code),
                isSelected: savedLangs.Contains(code),
                onToggle: OnFilterToggled));
        }

        AuthorFilters.Clear();
        var presentAuthors = _allMods
            .Select(m => m.PluginId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        foreach (var pluginId in presentAuthors)
        {
            AuthorFilters.Add(new AuthorFilterItem(
                pluginId,
                isSelected: savedAuthors.Contains(pluginId),
                onToggle: OnFilterToggled));
        }
    }

    private bool _suppressFilterChanges;

    private void OnFilterToggled()
    {
        if (_suppressFilterChanges) return;
        ApplyFilters();
        _ = PersistFiltersAsync();
        OnPropertyChanged(nameof(HasAnyFilterSelected));
    }

    private void ApplyFilters()
    {
        var selectedTags = TagFilters.Where(f => f.IsSelected).Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedLangs = LanguageFilters.Where(f => f.IsSelected).Select(f => f.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedAuthors = AuthorFilters.Where(f => f.IsSelected).Select(f => f.PluginId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filtered = _allMods.Where(m =>
            (selectedTags.Count == 0 || m.Tags.Any(t => selectedTags.Contains(t))) &&
            (selectedLangs.Count == 0 || m.Languages.Any(l => selectedLangs.Contains(l))) &&
            (selectedAuthors.Count == 0 || selectedAuthors.Contains(m.PluginId))
        ).ToList();

        Mods.Clear();
        foreach (var m in filtered) Mods.Add(m);

        MatchCountText = filtered.Count == _allMods.Count
            ? $"{_allMods.Count} mod{(_allMods.Count == 1 ? "" : "s")} shown."
            : $"{filtered.Count} of {_allMods.Count} mods shown.";
    }

    private async Task PersistFiltersAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();
            config.SelectedTagFilters = TagFilters.Where(f => f.IsSelected).Select(f => f.Id).ToList();
            config.SelectedLanguageFilters = LanguageFilters.Where(f => f.IsSelected).Select(f => f.Code).ToList();
            config.SelectedAuthorFilters = AuthorFilters.Where(f => f.IsSelected).Select(f => f.PluginId).ToList();
            await _configService.SaveAsync(config);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist filter selections");
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressFilterChanges = true;
        try
        {
            foreach (var f in TagFilters) f.IsSelected = false;
            foreach (var f in LanguageFilters) f.IsSelected = false;
            foreach (var f in AuthorFilters) f.IsSelected = false;
        }
        finally { _suppressFilterChanges = false; }

        ApplyFilters();
        _ = PersistFiltersAsync();
        OnPropertyChanged(nameof(HasAnyFilterSelected));
    }
}

public sealed partial class TagFilterItem : ObservableObject
{
    private readonly Action _onToggle;
    public string Id { get; }
    public string Label { get; }
    public string Category { get; }

    [ObservableProperty]
    private bool _isSelected;

    public TagFilterItem(string id, string label, string category, bool isSelected, Action onToggle)
    {
        Id = id;
        Label = label;
        Category = category;
        _isSelected = isSelected;
        _onToggle = onToggle;
    }

    partial void OnIsSelectedChanged(bool value) => _onToggle();
    public override string ToString() => Label;
}

public sealed partial class LanguageFilterItem : ObservableObject
{
    private readonly Action _onToggle;
    public string Code { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public LanguageFilterItem(string code, string label, bool isSelected, Action onToggle)
    {
        Code = code;
        Label = label;
        _isSelected = isSelected;
        _onToggle = onToggle;
    }

    partial void OnIsSelectedChanged(bool value) => _onToggle();
    public override string ToString() => Label;
}

public sealed partial class AuthorFilterItem : ObservableObject
{
    private readonly Action _onToggle;
    public string PluginId { get; }

    [ObservableProperty]
    private bool _isSelected;

    public AuthorFilterItem(string pluginId, bool isSelected, Action onToggle)
    {
        PluginId = pluginId;
        _isSelected = isSelected;
        _onToggle = onToggle;
    }

    partial void OnIsSelectedChanged(bool value) => _onToggle();
    public override string ToString() => PluginId;
}

/// <summary>
/// One row in the Mods tab — represents a single developer's mod for a single game. Same shape
/// and announcement format as DeveloperModItemViewModel so the screen reader reads consistently.
/// </summary>
public partial class ModItemViewModel : ObservableObject
{
    public required string GameId { get; init; }
    public required string GameDisplayName { get; init; }
    public required string ModName { get; init; }
    public required string PluginId { get; init; }
    public required bool IsDetected { get; init; }
    /// <summary>True when the game declares a game-installer dependency, so it can be installed
    /// from the not-detected state.</summary>
    public bool HasGameInstaller { get; init; }
    public string? InstallPath { get; init; }
    public string? InstalledVersion { get; init; }
    public bool HasUpdate { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Languages { get; init; } = [];

    public string StatusText => (IsDetected, InstalledVersion, HasUpdate) switch
    {
        (false, _, _) => HasGameInstaller ? "Not installed" : "Game not detected",
        (true, null, _) => "Not installed",
        (true, _, true) => $"v{InstalledVersion} — update available",
        (true, _, false) => $"v{InstalledVersion} installed",
    };

    public string AnnouncementText => $"{ModName} for {GameDisplayName}, {StatusText}";

    public override string ToString() => AnnouncementText;
}
