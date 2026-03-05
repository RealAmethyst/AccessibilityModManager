using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccessibilityModManager.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private GameDetailsViewModel? _gameDetailsVm;

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
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        // Load the default tab (Plugins) on startup
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
            case 0 when !_pluginsLoaded:
                _pluginsLoaded = true;
                await PluginsVm.LoadPluginsCommand.ExecuteAsync(null);
                break;
            case 1 when !_gamesLoaded:
                _gamesLoaded = true;
                await GamesListVm.RefreshGamesCommand.ExecuteAsync(null);
                break;
            case 2 when !_settingsLoaded:
                _settingsLoaded = true;
                await SettingsVm.LoadSettingsCommand.ExecuteAsync(null);
                break;
        }
    }

    public void ShowGameDetails(GameDetailsViewModel detailsVm)
    {
        GameDetailsVm = detailsVm;
        IsDetailsOpen = true;
    }

    public void CloseGameDetails()
    {
        IsDetailsOpen = false;
        GameDetailsVm = null;
    }
}
