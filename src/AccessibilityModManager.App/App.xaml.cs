using System.Net.Http;
using System.Windows;
using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.App.Views;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Logging;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AccessibilityModManager.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Logging
        var logger = LoggingSetup.CreateLogger();
        services.AddSingleton<ILogger>(logger);

        // HTTP
        services.AddSingleton<HttpClient>();

        // Infrastructure — services
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IPluginRegistryClient, PluginRegistryClient>();
        services.AddSingleton<IPluginRepoClient, PluginRepoClient>();
        services.AddSingleton<IPluginStateStore, PluginStateStore>();
        services.AddSingleton<IReceiptStore, ReceiptStore>();
        services.AddSingleton<IDependencyChecker, DependencyChecker>();

        // Infrastructure — detection
        services.AddSingleton<IGameVerifier, GameVerifier>();
        services.AddSingleton<ISteamDetector, SteamDetector>();
        services.AddSingleton<GameAggregator>();

        // Infrastructure — installer
        services.AddSingleton<BackupManager>();
        services.AddSingleton<InstallActionExecutor>();
        services.AddSingleton<InstallVerifier>();
        services.AddSingleton<ManifestParser>();
        services.AddSingleton<SafeZipExtractor>();
        services.AddSingleton<IInstallerEngine, InstallerEngine>();

        // ViewModels
        services.AddTransient<PluginsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ProgressDialogViewModel>();

        services.AddTransient<MainViewModel>(sp =>
        {
            var pluginsVm = sp.GetRequiredService<PluginsViewModel>();
            var settingsVm = sp.GetRequiredService<SettingsViewModel>();

            MainViewModel? mainVm = null;

            var gamesListVm = new GamesListViewModel(
                sp.GetRequiredService<IPluginRegistryClient>(),
                sp.GetRequiredService<IPluginRepoClient>(),
                sp.GetRequiredService<IPluginStateStore>(),
                sp.GetRequiredService<IConfigService>(),
                sp.GetRequiredService<IReceiptStore>(),
                sp.GetRequiredService<GameAggregator>(),
                sp.GetRequiredService<ILogger>(),
                (gameInstall, activeIndexes) =>
                {
                    var detailsVm = CreateGameDetailsViewModel(sp, mainVm!);
                    detailsVm.Load(gameInstall, activeIndexes);
                    mainVm!.ShowGameDetails(detailsVm);
                });

            mainVm = new MainViewModel(pluginsVm, gamesListVm, settingsVm);
            return mainVm;
        });

        // Windows
        services.AddTransient<MainWindow>();
    }

    private static GameDetailsViewModel CreateGameDetailsViewModel(IServiceProvider sp, MainViewModel mainVm)
    {
        return new GameDetailsViewModel(
            sp.GetRequiredService<IPluginRepoClient>(),
            sp.GetRequiredService<IInstallerEngine>(),
            sp.GetRequiredService<IReceiptStore>(),
            sp.GetRequiredService<IDependencyChecker>(),
            sp.GetRequiredService<ILogger>(),
            () => mainVm.CloseGameDetails(),
            async (title, message, progress, ct) =>
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var progressVm = sp.GetRequiredService<ProgressDialogViewModel>();
                    var dialog = new ProgressDialog(progressVm);
                    dialog.Owner = Application.Current.MainWindow;

                    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    dialog.Start(title, message, cts, progress);
                    dialog.Show();

                    // Close when token is cancelled or operation completes
                    ct.Register(() => Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (dialog.IsVisible) dialog.Close();
                    }));
                });
            });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
