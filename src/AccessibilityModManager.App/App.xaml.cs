using System.Net.Http;
using System.Windows;
using AccessibilityModManager.App.Services;
using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.App.Views;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Logging;
using AccessibilityModManager.Infrastructure.Patreon;
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

        // Hydrate any saved Patreon session before the UI mounts so visibility filters
        // (which depend on entitlement) start with the right state.
        _ = _serviceProvider.GetRequiredService<PatreonService>().LoadAsync();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();

        // Surface a recovered-from-corruption settings load once at startup. A silent reset hides
        // real data loss (manually located games, emulator records, filters) — the user deserves
        // to know it happened and what was restored.
        _ = Task.Run(async () =>
        {
            var configService = _serviceProvider.GetRequiredService<IConfigService>();
            await configService.LoadAsync();
            if (configService.LastLoadProblem is { } problem)
            {
                configService.AcknowledgeLoadProblem();
                await Current.Dispatcher.InvokeAsync(() =>
                    MessageBox.Show(Current.MainWindow, problem, "Settings problem detected",
                        MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        });
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

        // Registry signature verification (RSA-PSS/SHA256). The verifier is REQUIRED — the
        // registry is the trust anchor, and both the verifier and the client refuse to exist
        // without a key, so a wiring mistake can never silently accept unsigned registries.
        services.AddSingleton(new RegistrySignatureVerifier(GetRegistryPublicKey(), logger));

        services.AddSingleton<IPluginRegistryClient>(sp => new PluginRegistryClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<ILogger>(),
            sp.GetRequiredService<RegistrySignatureVerifier>()));
        services.AddSingleton<UpdateChecker>();
        services.AddSingleton<IPluginRepoClient, PluginRepoClient>();
        services.AddSingleton<IReceiptStore, ReceiptStore>();
        services.AddSingleton<IDependencyReceiptStore, DependencyReceiptStore>();
        services.AddSingleton<IDependencyChecker, DependencyChecker>();

        // Patreon sign-in / entitlement gating.
        services.AddSingleton<IPatreonAccountStore, DpapiPatreonAccountStore>();
        services.AddSingleton<IPatreonEntitlementCache, PatreonEntitlementCache>();
        services.AddSingleton(sp => new PatreonClient(
            sp.GetRequiredService<HttpClient>(),
            PatreonAppRegistry.Manager,
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<PatreonService>();

        // Infrastructure — detection
        services.AddSingleton<IGameVerifier, GameVerifier>();
        services.AddSingleton<ISteamDetector, SteamDetector>();
        services.AddSingleton<IRegistryGameDetector, RegistryGameDetector>();
        services.AddSingleton<GameAggregator>();

        // Infrastructure — installer
        services.AddSingleton<BackupManager>();
        services.AddSingleton<InstallActionExecutor>();
        services.AddSingleton<InstallVerifier>();
        services.AddSingleton<ManifestParser>();
        services.AddSingleton<SafeZipExtractor>();
        services.AddSingleton<LifecycleScriptRunner>();
        services.AddSingleton<DependencyAutoInstaller>();
        services.AddSingleton<IAsciiPathShimService, AsciiPathShimService>();
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
                        },
                        (game, pluginId, activeIndexes) =>
                        {
                            // Not-detected game with a game-installer dep, opened from Developer
                            // Details: open Game Details in the not-installed state, returning here
                            // on Back.
                            var detailsVm = CreateGameDetailsViewModel(sp, mainVm!);
                            detailsVm.LoadUninstalled(game, pluginId, activeIndexes);
                            mainVm!.ShowGameDetails(detailsVm, plugin);
                        });
                    mainVm!.ShowDeveloperDetails(devVm);
                });

            var gamesListVm = new GamesListViewModel(
                sp.GetRequiredService<IPluginRegistryClient>(),
                sp.GetRequiredService<IPluginRepoClient>(),
                sp.GetRequiredService<IConfigService>(),
                sp.GetRequiredService<IReceiptStore>(),
                sp.GetRequiredService<IGameVerifier>(),
                sp.GetRequiredService<GameAggregator>(),
                sp.GetRequiredService<PatreonService>(),
                sp.GetRequiredService<ILogger>(),
                (gameInstall, activeIndexes) =>
                {
                    // Game Details opened from the Games tab: no origin plugin, Back goes
                    // straight to the main tabs.
                    var detailsVm = CreateGameDetailsViewModel(sp, mainVm!);
                    detailsVm.Load(gameInstall, activeIndexes);
                    mainVm!.ShowGameDetails(detailsVm);
                },
                (game, pluginId, activeIndexes) =>
                {
                    // Not-detected game with a game-installer dependency: open Game Details in the
                    // not-installed state so Install can fetch the game first, then the mod.
                    var detailsVm = CreateGameDetailsViewModel(sp, mainVm!);
                    detailsVm.LoadUninstalled(game, pluginId, activeIndexes);
                    mainVm!.ShowGameDetails(detailsVm);
                },
                BrowseForFolder);

            mainVm = new MainViewModel(
                pluginsVm, gamesListVm, settingsVm,
                sp.GetRequiredService<UpdateChecker>(),
                sp.GetRequiredService<ILogger>(),
                ShowInfoDialog,
                ConfirmDialog,
                info => RunUpdate(sp, info),
                (info, current) => ShowUpdateDialog(sp, info, current));
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
            sp.GetRequiredService<IGameVerifier>(),
            sp.GetRequiredService<IAsciiPathShimService>(),
            sp.GetRequiredService<IRegistryGameDetector>(),
            sp.GetRequiredService<DependencyAutoInstaller>(),
            sp.GetRequiredService<PatreonService>(),
            sp.GetRequiredService<ILogger>(),
            () => mainVm.CloseGameDetails(),
            ShowInfoDialog,
            ConfirmDialog,
            async (title, message, progress, work, ct) =>
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                ProgressDialog? dialog = null;
                ProgressDialogViewModel? progressVm = null;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    progressVm = sp.GetRequiredService<ProgressDialogViewModel>();
                    dialog = new ProgressDialog(progressVm)
                    {
                        Owner = Application.Current.MainWindow
                    };
                    dialog.Start(title, message, cts, progress);
                    dialog.Show();
                });

                // Build a single host that satisfies both IScriptHost and IDependencyHost so
                // dep consents, script confirmations, and live stdout streaming all surface
                // in the same UI the user is already watching.
                var host = new DialogScriptHost(
                    Application.Current.Dispatcher,
                    () => (Window?)dialog ?? Application.Current.MainWindow,
                    progressVm!);

                try
                {
                    await work(host, host, cts.Token);
                }
                finally
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (dialog is { IsVisible: true }) dialog.Close();
                    });
                }
            },
            // Uninstall path doesn't open a ProgressDialog, but the engine still needs an
            // IScriptHost so cached post-uninstall scripts can be confirmed + streamed. The
            // ProgressDialogViewModel here is a one-off scratch instance — script output isn't
            // shown anywhere, but the modal warning still works.
            () => new DialogScriptHost(
                Application.Current.Dispatcher,
                () => Application.Current.MainWindow,
                sp.GetRequiredService<ProgressDialogViewModel>()),
            ShowChangelog,
            PickFile,
            BrowseForFolderTitled);
    }

    /// <summary>
    /// File-picker callback used by the GameDetails install flow when the signed-in user is
    /// the creator of a Patreon-gated release's campaign — Patreon refuses to hand the
    /// creator a download URL for their own paid post, so we ask them to point at the
    /// wrapped ZIP they already have. SHA256 still gates correctness.
    /// </summary>
    private static string? PickFile(string title, string filter, string? suggestedFileName)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedFileName ?? string.Empty,
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false
        };
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current.MainWindow;
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Modal in-app changelog viewer. Uses the in-index notes when present, falls back to
    /// pointing at the external URL. Plain text rendering — markdown stays as-is, which is
    /// readable and screen-reader friendly. (A proper markdown renderer is a future polish.)
    /// </summary>
    /// <summary>
    /// Pops the modal Update Available dialog and, if the user clicks Install, kicks off the
    /// download + installer launch. Marshaled onto the UI thread because the update check
    /// runs on a background task.
    /// </summary>
    private static void ShowUpdateDialog(IServiceProvider sp, UpdateInfo info, Version current)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var vm = new UpdateAvailableDialogViewModel(info, current);
            var dialog = new UpdateAvailableDialog(vm)
            {
                Owner = Application.Current?.MainWindow
            };
            dialog.ShowDialog();
            if (dialog.UserChoseInstall)
                RunUpdate(sp, info);
        });
    }

    private static void ShowChangelog(string modName, string version, string? notes, string? externalUrl)
    {
        var owner = Application.Current?.MainWindow;
        var dialog = new ChangelogDialog
        {
            Owner = owner
        };
        dialog.Show(modName, version, notes, externalUrl);
        dialog.ShowDialog();
    }

    /// <summary>
    /// Returns the RSA public key PEM for registry signature verification.
    /// Paired with the private key held offline by the registry maintainer; the
    /// matching .sig file is published alongside plugin-registry.json on each release.
    /// Public keys are safe to commit — only the private key is sensitive.
    /// </summary>
    private static string GetRegistryPublicKey() =>
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
    /// Downloads the new manager installer with SHA256 verification, launches it, and exits the
    /// running app so Inno's upgrade flow can replace files. Surface errors to the user via the
    /// standard MessageBox path.
    /// </summary>
    private static async void RunUpdate(IServiceProvider sp, UpdateInfo info)
    {
        var checker = sp.GetRequiredService<UpdateChecker>();
        var logger = sp.GetRequiredService<ILogger>();

        ProgressDialog? dialog = null;
        var progressVm = sp.GetRequiredService<ProgressDialogViewModel>();
        var cts = new CancellationTokenSource();
        var progress = new Progress<ProgressInfo>(p => progressVm.OnProgress(p));

        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                dialog = new ProgressDialog(progressVm)
                {
                    Owner = Application.Current.MainWindow
                };
                dialog.Start("Downloading update", $"Downloading version {info.Version}...", cts, progress);
                dialog.Show();
            });

            var byteProgress = new Progress<double>(fraction =>
            {
                progressVm.OnProgress(new ProgressInfo
                {
                    Percentage = fraction * 100,
                    StatusText = $"Downloading version {info.Version} ({(int)(fraction * 100)}%)",
                    StepDescription = $"{(int)(fraction * 100)}% complete"
                });
            });

            var installerPath = await checker.DownloadAsync(info, byteProgress, cts.Token);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (dialog is { IsVisible: true }) dialog.Close();
            });

            // Hand off to the OS — Inno detects the existing install via AppId and runs an
            // upgrade. The current app must exit before file replacement runs.
            // /SILENT runs the upgrade with just a progress bar (no wizard pages); the installer's
            // [Run] entry (no longer skipifsilent) relaunches the app once files are replaced.
            // /NORESTART keeps it from ever forcing a reboot.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/SILENT /NORESTART",
                UseShellExecute = true
            });

            logger.Information("Launched update installer (/SILENT); shutting down to allow upgrade");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (dialog is { IsVisible: true }) dialog.Close();
            });
            logger.Error(ex, "Update installation failed");
            ShowInfoDialog("Update failed",
                $"Could not install the update:\n\n{ex.Message}\n\nYou can download the installer manually from {info.ReleasePageUrl}.");
        }
    }

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

    /// <summary>
    /// Folder picker with a caller-supplied title. Used by the GameDetails install flow to let the
    /// user choose where a portable-app (emulator) game-installer extracts to. Returns the chosen
    /// folder, or null if the user cancelled.
    /// </summary>
    private static string? BrowseForFolderTitled(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
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
