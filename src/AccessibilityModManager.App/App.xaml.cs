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

        // Registry signature verification (RSA-PSS/SHA256)
        // TODO: Set your real RSA public key PEM to enable signature verification.
        // Generate key pair:  openssl genrsa -out registry-private.pem 4096
        // Extract public key: openssl rsa -in registry-private.pem -pubout -out registry-public.pem
        // Sign registry:      openssl dgst -sha256 -sigopt rsa_padding_mode:pss -sign registry-private.pem registry.json | base64 > registry.json.sig
        // Replace with your PEM string to enable signature verification
        var registryPublicKeyPem = GetRegistryPublicKey();
        if (registryPublicKeyPem != null)
            services.AddSingleton(new RegistrySignatureVerifier(registryPublicKeyPem, logger));

        services.AddSingleton<IPluginRegistryClient>(sp => new PluginRegistryClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<ILogger>(),
            sp.GetService<RegistrySignatureVerifier>()));
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
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ProgressDialogViewModel>();

        services.AddTransient<MainViewModel>(sp =>
        {
            var settingsVm = sp.GetRequiredService<SettingsViewModel>();

            MainViewModel? mainVm = null;

            // PluginsViewModel needs a callback to open Developer Details for a chosen plugin.
            var pluginsVm = new PluginsViewModel(
                sp.GetRequiredService<IPluginRegistryClient>(),
                sp.GetRequiredService<IPluginStateStore>(),
                sp.GetRequiredService<IConfigService>(),
                sp.GetRequiredService<ILogger>(),
                plugin =>
                {
                    var devVm = new DeveloperDetailsViewModel(
                        plugin,
                        sp.GetRequiredService<IPluginRepoClient>(),
                        sp.GetRequiredService<IConfigService>(),
                        sp.GetRequiredService<IReceiptStore>(),
                        sp.GetRequiredService<GameAggregator>(),
                        sp.GetRequiredService<ILogger>(),
                        () => mainVm!.CloseDeveloperDetails(),
                        (gameInstall, activeIndexes, originPluginId) =>
                        {
                            // Game Details opened from inside Developer Details: pass the
                            // origin plugin so Back returns to Developer Details, not main.
                            var detailsVm = CreateGameDetailsViewModel(sp, mainVm!);
                            detailsVm.Load(gameInstall, activeIndexes);
                            mainVm!.ShowGameDetails(detailsVm, plugin);
                        });
                    mainVm!.ShowDeveloperDetails(devVm);
                });

            var gamesListVm = new GamesListViewModel(
                sp.GetRequiredService<IPluginRegistryClient>(),
                sp.GetRequiredService<IPluginRepoClient>(),
                sp.GetRequiredService<IPluginStateStore>(),
                sp.GetRequiredService<IConfigService>(),
                sp.GetRequiredService<IReceiptStore>(),
                sp.GetRequiredService<IGameVerifier>(),
                sp.GetRequiredService<GameAggregator>(),
                sp.GetRequiredService<ILogger>(),
                (gameInstall, activeIndexes) =>
                {
                    // Game Details opened from the Games tab: no origin plugin, Back goes
                    // straight to the main tabs.
                    var detailsVm = CreateGameDetailsViewModel(sp, mainVm!);
                    detailsVm.Load(gameInstall, activeIndexes);
                    mainVm!.ShowGameDetails(detailsVm);
                },
                BrowseForFolder);

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
            sp.GetRequiredService<IConfigService>(),
            sp.GetRequiredService<ILogger>(),
            () => mainVm.CloseGameDetails(),
            ShowInfoDialog,
            ConfirmDialog,
            async (title, message, progress, work, ct) =>
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                ProgressDialog? dialog = null;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var progressVm = sp.GetRequiredService<ProgressDialogViewModel>();
                    dialog = new ProgressDialog(progressVm)
                    {
                        Owner = Application.Current.MainWindow
                    };
                    dialog.Start(title, message, cts, progress);
                    dialog.Show();
                });

                try
                {
                    await work(cts.Token);
                }
                finally
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (dialog is { IsVisible: true }) dialog.Close();
                    });
                }
            });
    }

    /// <summary>
    /// Returns the RSA public key PEM for registry signature verification.
    /// Paired with the private key held offline by the registry maintainer; the
    /// matching .sig file is published alongside plugin-registry.json on each release.
    /// Public keys are safe to commit — only the private key is sensitive.
    /// </summary>
    private static string? GetRegistryPublicKey() =>
        """
        -----BEGIN PUBLIC KEY-----
        MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAvPTABidJcBN5V4kWommo
        arlzq5pKHXNXrkFX8HUHjwK+SBiqUzWuZyZOEw5vAv+X6oa3T3g8iF+h+Hu+NHQ+
        dw/cLy+Vmmlaz3YgBJRKMrEQspySI8cM3+4ZU54YzUCpPNSwi37P5JmC1lJEeRMJ
        KxXz3Cwots1Zr2jZOn0l39+/9Vu8lQ84mVFd4wWIAfpBvc8FNVfw2p+qsOX3xZCa
        vhV2Q7YGXgf+N09OfCSB74pU/qBYXDZ+FP2w+2ywCMWOOKmX0t9C4EusZ28QTabj
        XkzrPyB5lhpMigl9HhvYjmtCjqPR7uzohIpRNLir02po3FRMAuW4sSxp0rkxu6pX
        huQsHbfgR12aX1/Cv6fR9ez3EH8/ODXrJDANoL8NDuJ0hkfsXPSEn8tv7d7ZV/S5
        4HpK6I/uwGMhY+YrkOCtj/FKDM+JaxD1PRqLZU/4uGiOG+Z2z4Cv7oA/ZnCW4EBn
        DI+9Ibfu1Ox+PtrLTr5hxUqiqsJfIYYLaWPJSAgzK4TkzumHp64/2kVmS0bb3xJ+
        +tytJv054d2PwLgaLLioD0CnRPQhXK1JPKmqUVP3aCIWJIa/1vchqgIXcXUyaQzG
        ghi2SW1UOrX1iNzJiO6CCkO0ad4V7FnvbMS2uxFpwYQ97/Mwh/iF0BhblcFM5niO
        OrUeiLZWMTgg4PWc06FFTyECAwEAAQ==
        -----END PUBLIC KEY-----
        """;

    /// <summary>
    /// Modal info dialog shown after install/update/uninstall completes successfully. WPF's
    /// MessageBox is fully accessible — screen readers announce the title, content, and OK
    /// button without any extra wiring on our side.
    /// </summary>
    private static void ShowInfoDialog(string title, string message)
    {
        var owner = Application.Current?.MainWindow;
        if (owner != null)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Modal Yes/No confirmation dialog. Returns true if the user clicks Yes. Used by
    /// destructive actions (e.g. uninstall) so the user gets a chance to back out.
    /// </summary>
    private static bool ConfirmDialog(string title, string message)
    {
        var owner = Application.Current?.MainWindow;
        var result = owner != null
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Shows the Windows folder picker on the UI thread and returns the selected path,
    /// or null if the user cancelled. Used by GamesListViewModel for "Browse for game folder".
    /// </summary>
    private static string? BrowseForFolder(string? initialDirectory)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select game install folder",
            Multiselect = false
        };
        if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        var owner = Application.Current?.MainWindow;
        var ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FolderName : null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
