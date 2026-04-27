using System.Reflection;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly UpdateChecker _updateChecker;
    private readonly ILogger _logger;
    private readonly Action<string, string> _showInfoDialog;
    private readonly Func<string, string, bool> _confirmDialog;
    private readonly Action<UpdateInfo> _runUpdate;

    /// <summary>
    /// Pops the modal Update Available dialog. Set by App.xaml.cs at construction time.
    /// Receives the available update info + the current app version so the dialog can render
    /// the headline ("Version X is available, you're on Y") and the changelog.
    /// </summary>
    private readonly Action<UpdateInfo, Version>? _showUpdateDialog;

    [ObservableProperty]
    private UpdateInfo? _availableUpdate;

    public string? AvailableUpdateText =>
        AvailableUpdate is not null
            ? $"Version {AvailableUpdate.Version} is available. You're on {GetCurrentVersion()}."
            : null;
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
        SettingsViewModel settingsVm,
        UpdateChecker updateChecker,
        ILogger logger,
        Action<string, string> showInfoDialog,
        Func<string, string, bool> confirmDialog,
        Action<UpdateInfo> runUpdate,
        Action<UpdateInfo, Version>? showUpdateDialog = null)
    {
        PluginsVm = pluginsVm;
        GamesListVm = gamesListVm;
        SettingsVm = settingsVm;
        _updateChecker = updateChecker;
        _logger = logger;
        _showInfoDialog = showInfoDialog;
        _confirmDialog = confirmDialog;
        _runUpdate = runUpdate;
        _showUpdateDialog = showUpdateDialog;
    }

    private static Version GetCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return asm.GetName().Version ?? new Version(0, 0, 0, 0);
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var info = await _updateChecker.CheckForUpdateAsync(GetCurrentVersion());
            if (info != null)
            {
                AvailableUpdate = info;
                OnPropertyChanged(nameof(AvailableUpdateText));

                // Pop the modal Update Available dialog instead of leaving an inline banner
                // for the user to discover. The dialog kicks off the install when the user
                // clicks Install; otherwise it just closes and we forget about it for the
                // session.
                _showUpdateDialog?.Invoke(info, GetCurrentVersion());
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Background update check failed");
        }
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        if (AvailableUpdate is null) return;
        _runUpdate(AvailableUpdate);
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        // Default tab (index 0) is Mods — load it on startup.
        // Kick off the update check in parallel; it's best-effort and shouldn't block the UI.
        var checkTask = CheckForUpdateAsync();
        await LoadCurrentTabAsync();
        await checkTask;
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
