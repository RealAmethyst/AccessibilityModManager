using System.Collections.ObjectModel;
using System.Diagnostics;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.App.ViewModels;

public partial class GamesListViewModel : ObservableObject
{
    private readonly IPluginRegistryClient _registryClient;
    private readonly IPluginRepoClient _repoClient;
    private readonly IPluginStateStore _stateStore;
    private readonly IConfigService _configService;
    private readonly IReceiptStore _receiptStore;
    private readonly IGameVerifier _gameVerifier;
    private readonly GameAggregator _gameAggregator;
    private readonly ILogger _logger;
    private readonly Action<GameInstall, Dictionary<string, PluginRepoIndex>> _navigateToDetails;
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

    public ObservableCollection<ModItemViewModel> Mods { get; } = [];

    public GamesListViewModel(
        IPluginRegistryClient registryClient,
        IPluginRepoClient repoClient,
        IPluginStateStore stateStore,
        IConfigService configService,
        IReceiptStore receiptStore,
        IGameVerifier gameVerifier,
        GameAggregator gameAggregator,
        ILogger logger,
        Action<GameInstall, Dictionary<string, PluginRepoIndex>> navigateToDetails,
        Func<string?, string?> browseForFolder)
    {
        _registryClient = registryClient;
        _repoClient = repoClient;
        _stateStore = stateStore;
        _configService = configService;
        _receiptStore = receiptStore;
        _gameVerifier = gameVerifier;
        _gameAggregator = gameAggregator;
        _logger = logger;
        _navigateToDetails = navigateToDetails;
        _browseForFolder = browseForFolder;
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
            var states = await _stateStore.LoadAllAsync();
            var enabledIds = new HashSet<string>(states.Where(s => s.IsEnabled).Select(s => s.PluginId));

            var activeIndexes = new Dictionary<string, PluginRepoIndex>();
            foreach (var plugin in registry.Plugins.Where(p => p.IsBuiltIn || enabledIds.Contains(p.Id)))
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

                    var install = installs.FirstOrDefault(i => i.Game.GameId == game.GameId && i.IsValid);
                    var receipt = await _receiptStore.LoadAsync(game.GameId, pluginId);

                    var latestVersion = releases
                        .Where(r => r.Channel == config.DefaultChannel)
                        .OrderByDescending(r => r.Version, VersionComparer.Instance)
                        .FirstOrDefault()?.Version;

                    var hasUpdate = receipt != null && latestVersion != null &&
                                    VersionComparer.Instance.Compare(latestVersion, receipt.InstalledVersion) > 0;

                    rows.Add(new ModItemViewModel
                    {
                        GameId = game.GameId,
                        GameDisplayName = game.DisplayName,
                        ModName = DeriveModName(releases),
                        PluginId = pluginId,
                        IsDetected = install != null,
                        InstallPath = install?.InstallPath,
                        InstalledVersion = receipt?.InstalledVersion,
                        HasUpdate = hasUpdate
                    });
                }
            }

            // Sort alphabetically by game, then by mod name within a game.
            foreach (var row in rows
                .OrderBy(r => r.GameDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.ModName, StringComparer.OrdinalIgnoreCase))
            {
                Mods.Add(row);
            }

            var detected = Mods.Count(m => m.IsDetected);
            StatusMessage = $"Found {Mods.Count} mod{(Mods.Count == 1 ? "" : "s")} ({detected} detected).";
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
    /// Best-effort human name for a mod, parsed from its first release's package URL. For
    /// GitHub-hosted releases this is the source repo name (e.g. "DigimonNOAccess"). Falls back
    /// to "mod" when the URL doesn't match a github.com release pattern. Same logic as
    /// ModReleaseGroup.ModName / DeveloperDetailsViewModel — keep them in sync.
    /// </summary>
    private static string DeriveModName(IReadOnlyList<ModRelease> releases)
    {
        if (releases.Count == 0) return "mod";
        var url = releases[0].PackageUrl;
        if (string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase))
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
        if (mod == null || !mod.IsDetected) return;

        var install = _lastInstalls.FirstOrDefault(i => i.Game.GameId == mod.GameId && i.IsValid);
        if (install == null) return;

        // Scope the navigation payload to this mod's developer so Game Details shows just one
        // mod card (not every developer's mods for that game). Matches Developer Details flow.
        if (!_lastActiveIndexes.TryGetValue(mod.PluginId, out var pluginIndex)) return;
        var scoped = new Dictionary<string, PluginRepoIndex> { [mod.PluginId] = pluginIndex };
        _navigateToDetails(install, scoped);
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
    public string? InstallPath { get; init; }
    public string? InstalledVersion { get; init; }
    public bool HasUpdate { get; init; }

    public string StatusText => (IsDetected, InstalledVersion, HasUpdate) switch
    {
        (false, _, _) => "Game not detected",
        (true, null, _) => "Not installed",
        (true, _, true) => $"v{InstalledVersion} — update available",
        (true, _, false) => $"v{InstalledVersion} installed",
    };

    public string AnnouncementText => $"{ModName} for {GameDisplayName}, {StatusText}";

    public override string ToString() => AnnouncementText;
}
