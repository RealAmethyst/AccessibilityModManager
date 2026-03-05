using CommunityToolkit.Mvvm.ComponentModel;

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

    public MainViewModel(
        PluginsViewModel pluginsVm,
        GamesListViewModel gamesListVm,
        SettingsViewModel settingsVm)
    {
        PluginsVm = pluginsVm;
        GamesListVm = gamesListVm;
        SettingsVm = settingsVm;
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
