using System.Windows;
using System.Windows.Threading;
using AccessibilityModManager.App.ViewModels;

namespace AccessibilityModManager.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Restore keyboard focus to the games list (and the previously-selected game) when
        // the user navigates back from the Game Details overlay TO the main tabs.
        viewModel.GameDetailsClosed += () =>
            _ = Dispatcher.BeginInvoke(
                new Action(() => GamesTabView.FocusList()),
                DispatcherPriority.ApplicationIdle);

        // When Developer Details closes (Back/Escape), focus returns to the developers list.
        viewModel.DeveloperDetailsClosed += () =>
            _ = Dispatcher.BeginInvoke(
                new Action(() => DevelopersTabView.FocusList()),
                DispatcherPriority.ApplicationIdle);

        // When Game Details closes and Developer Details becomes the top-of-stack again,
        // focus the developer's mods list so the screen reader has a logical landing spot.
        viewModel.DeveloperDetailsReshown += () =>
            _ = Dispatcher.BeginInvoke(
                new Action(() => DeveloperDetailsViewControl.FocusList()),
                DispatcherPriority.ApplicationIdle);

        // Default tab is Mods (index 0). Focus its list as soon as the mods are loaded — NOT after
        // the whole of Initialize, which also waits on the update check and so held focus back for a
        // network round trip. ApplicationIdle priority so the ListView's item containers are
        // generated before we land on the first item.
        viewModel.InitialTabLoaded += () =>
            _ = Dispatcher.BeginInvoke(
                new Action(() => GamesTabView.FocusList()),
                DispatcherPriority.ApplicationIdle);

        Loaded += async (_, _) => await viewModel.InitializeCommand.ExecuteAsync(null);
    }
}
