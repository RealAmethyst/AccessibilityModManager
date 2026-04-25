using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccessibilityModManager.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsDeveloperDetailsVisible))]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private GameDetailsViewModel? _gameDetailsVm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeveloperDetailsOpen))]
    [NotifyPropertyChangedFor(nameof(IsMainContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsDeveloperDetailsVisible))]
    private DeveloperDetailsViewModel? _developerDetailsVm;

    /// <summary>True when a Developer Details overlay is active.</summary>
    public bool IsDeveloperDetailsOpen => DeveloperDetailsVm != null;

    /// <summary>The main tab grid is visible only when no overlay is shown.</summary>
    public bool IsMainContentVisible => !IsDetailsOpen && !IsDeveloperDetailsOpen;

    /// <summary>Developer Details is visible when it's open AND no Game Details is on top of it.</summary>
    public bool IsDeveloperDetailsVisible => IsDeveloperDetailsOpen && !IsDetailsOpen;

    /// <summary>If GameDetails was opened from a DeveloperDetails view, we remember which
    /// plugin so Back returns there instead of straight to the main tabs.</summary>
    private PluginEntry? _gameDetailsOriginPlugin;

    public PluginsViewModel PluginsVm { get; }
    public GamesListViewModel GamesListVm { get; }
    public SettingsViewModel SettingsVm { get; }

    private bool _pluginsLoaded;
    private bool _gamesLoaded;
    private bool _settingsLoaded;

    public MainViewModel(
        PluginsViewModel pluginsVm,
        GamesListViewModel gamesListVm,
        SettingsViewModel settingsVm)
    {
        PluginsVm = pluginsVm;
        GamesListVm = gamesListVm;
        SettingsVm = settingsVm;

        // When a plugin is enabled/disabled, the active set of games changes — invalidate the
        // Games cache so the user doesn't have to remember to refresh. If they're currently on
        // the Games tab, refresh immediately.
        PluginsVm.PluginEnabledChanged += OnPluginEnabledChanged;
    }

    private void OnPluginEnabledChanged()
    {
        // The Mods tab is index 0 — its content depends on which developers are enabled, so
        // toggling a developer invalidates the cache and (if currently visible) re-runs detection.
        _gamesLoaded = false;
        if (SelectedTabIndex == 0)
        {
            _ = LoadCurrentTabAsync();
        }
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        // Default tab (index 0) is Mods — load it on startup.
        await LoadCurrentTabAsync();
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        _ = LoadCurrentTabAsync();
    }

    private async Task LoadCurrentTabAsync()
    {
        switch (SelectedTabIndex)
        {
            case 0 when !_gamesLoaded:
                _gamesLoaded = true;
                await GamesListVm.RefreshGamesCommand.ExecuteAsync(null);
                break;
            case 1 when !_pluginsLoaded:
                _pluginsLoaded = true;
                await PluginsVm.LoadPluginsCommand.ExecuteAsync(null);
                break;
            case 2 when !_settingsLoaded:
                _settingsLoaded = true;
                await SettingsVm.LoadSettingsCommand.ExecuteAsync(null);
                break;
        }
    }

    public void ShowGameDetails(GameDetailsViewModel detailsVm, PluginEntry? originPlugin = null)
    {
        // Track where this Game Details was opened from. If from a Developer Details view,
        // pressing Back closes Game Details and reveals Developer Details underneath; otherwise
        // Back returns straight to the main tabs.
        _gameDetailsOriginPlugin = originPlugin;
        GameDetailsVm = detailsVm;
        IsDetailsOpen = true;
    }

    public void CloseGameDetails()
    {
        IsDetailsOpen = false;
        GameDetailsVm = null;

        if (_gameDetailsOriginPlugin != null)
        {
            // Developer Details is still alive underneath — refresh its installed-state so the
            // mod the user just installed/uninstalled doesn't show as stale. Fire-and-forget;
            // the list updates as receipts re-load.
            _ = DeveloperDetailsVm?.RefreshAsync();
            _gameDetailsOriginPlugin = null;
            DeveloperDetailsReshown?.Invoke();
        }
        else
        {
            GameDetailsClosed?.Invoke();
        }
    }

    public void ShowDeveloperDetails(DeveloperDetailsViewModel detailsVm)
    {
        DeveloperDetailsVm = detailsVm;
    }

    public void CloseDeveloperDetails()
    {
        DeveloperDetailsVm = null;
        DeveloperDetailsClosed?.Invoke();
    }

    /// <summary>Raised when Game Details is closed and we go back to the main tabs (i.e. NOT
    /// when returning to Developer Details). The MainWindow restores focus to the Games list.</summary>
    public event Action? GameDetailsClosed;

    /// <summary>Raised when Developer Details is closed (Back from it). MainWindow restores
    /// focus to the Developers list.</summary>
    public event Action? DeveloperDetailsClosed;

    /// <summary>Raised when Game Details closes and Developer Details becomes visible again.
    /// The Developer Details view restores focus to its mod list.</summary>
    public event Action? DeveloperDetailsReshown;
}
