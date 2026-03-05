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
    private readonly GameAggregator _gameAggregator;
    private readonly ILogger _logger;
    private readonly Action<GameInstall, Dictionary<string, PluginRepoIndex>> _navigateToDetails;

    // Cached after last refresh, so navigation to details can use them
    private Dictionary<string, PluginRepoIndex> _lastActiveIndexes = [];
    private List<GameInstall> _lastInstalls = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<GameItemViewModel> Games { get; } = [];

    public GamesListViewModel(
        IPluginRegistryClient registryClient,
        IPluginRepoClient repoClient,
        IPluginStateStore stateStore,
        IConfigService configService,
        IReceiptStore receiptStore,
        GameAggregator gameAggregator,
        ILogger logger,
        Action<GameInstall, Dictionary<string, PluginRepoIndex>> navigateToDetails)
    {
        _registryClient = registryClient;
        _repoClient = repoClient;
        _stateStore = stateStore;
        _configService = configService;
        _receiptStore = receiptStore;
        _gameAggregator = gameAggregator;
        _logger = logger;
        _navigateToDetails = navigateToDetails;
    }

    [RelayCommand]
    private async Task RefreshGamesAsync(CancellationToken ct)
    {
        IsLoading = true;
        StatusMessage = "Detecting games...";

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

            var gameMap = GameAggregator.GetGamesByGameId(activeIndexes);
            Games.Clear();

            foreach (var (gameId, pluginGames) in gameMap.OrderBy(kv => kv.Value.First().Game.DisplayName))
            {
                var matchingInstalls = installs.Where(i => i.Game.GameId == gameId).ToList();
                var receipts = await _receiptStore.LoadAllForGameAsync(gameId);

                var displayName = pluginGames.First().Game.DisplayName;
                var pluginNames = pluginGames.Select(pg => pg.PluginId).ToList();
                var isDetected = matchingInstalls.Any(i => i.IsValid);
                var installPath = matchingInstalls.FirstOrDefault(i => i.IsValid)?.InstallPath;
                var installedVersion = receipts.FirstOrDefault()?.InstalledVersion;

                Games.Add(new GameItemViewModel
                {
                    GameId = gameId,
                    DisplayName = displayName,
                    PluginIds = pluginNames,
                    IsDetected = isDetected,
                    InstallPath = installPath,
                    InstalledVersion = installedVersion,
                    HasUpdate = false
                });
            }

            StatusMessage = $"Found {Games.Count} games ({Games.Count(g => g.IsDetected)} detected).";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Game detection cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to detect games");
            StatusMessage = $"Failed to detect games: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenGameDetails(GameItemViewModel? game)
    {
        if (game == null || !game.IsDetected) return;

        var install = _lastInstalls.FirstOrDefault(i => i.Game.GameId == game.GameId && i.IsValid);
        if (install == null) return;

        _navigateToDetails(install, _lastActiveIndexes);
    }

    [RelayCommand]
    private void OpenGameFolder(GameItemViewModel? game)
    {
        if (string.IsNullOrEmpty(game?.InstallPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = game.InstallPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to open game folder");
            StatusMessage = $"Could not open folder: {ex.Message}";
        }
    }
}

public partial class GameItemViewModel : ObservableObject
{
    public required string GameId { get; init; }
    public required string DisplayName { get; init; }
    public required List<string> PluginIds { get; init; }
    public required bool IsDetected { get; init; }
    public string? InstallPath { get; init; }
    public string? InstalledVersion { get; init; }
    public bool HasUpdate { get; init; }

    public string StatusText => (IsDetected, InstalledVersion) switch
    {
        (false, _) => "Not found",
        (true, null) => "Detected — no mod installed",
        (true, _) when HasUpdate => $"v{InstalledVersion} — update available",
        (true, _) => $"v{InstalledVersion} installed",
    };
}
