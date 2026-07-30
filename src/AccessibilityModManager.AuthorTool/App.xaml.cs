using System.Net.Http;
using System.Windows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.AuthorTool.ViewModels;
using AccessibilityModManager.AuthorTool.Views;
using AccessibilityModManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AccessibilityModManager.AuthorTool;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // Best-effort hydrate any saved Patreon-author session before the UI mounts so the
        // release dialog can show tier checkboxes immediately when the author opens it.
        _ = _serviceProvider.GetRequiredService<PatreonAuthorService>().LoadAsync();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var logger = LoggingSetup.CreateLogger();
        services.AddSingleton<ILogger>(logger);

        services.AddSingleton<HttpClient>();

        services.AddSingleton<AuthorConfigService>();
        services.AddSingleton<IndexFileService>();
        services.AddSingleton<Sha256HashService>();
        services.AddSingleton<GitService>();
        services.AddSingleton<GitHubService>();
        services.AddSingleton<ManifestBuilderService>();
        services.AddSingleton<RegistryMembershipChecker>();
        services.AddSingleton<PatreonAuthorService>();
        services.AddSingleton<ServerUploadService>();

        // The signed-catalog side. Registered together because they only mean anything together:
        // the head store is this machine's memory of what it published, the key store holds what it
        // published with, and the proof service is the only thing allowed to read either into a
        // decision.
        services.AddSingleton<PublisherHeadStore>();
        services.AddSingleton<ClaimSigningKeyStore>();
        services.AddSingleton<IndexProofService>();
        services.AddSingleton<ProjectReconciler>();
        services.AddSingleton<IndexPublishCoordinator>();

        services.AddTransient<ProjectPickerViewModel>();
        services.AddTransient<IndexEditorViewModel>();

        services.AddTransient<MainViewModel>(sp =>
        {
            MainViewModel? mainVm = null;
            mainVm = new MainViewModel(
                sp.GetRequiredService<AuthorConfigService>(),
                sp.GetRequiredService<ILogger>(),
                () => CreateProjectPicker(sp, mainVm!),
                projectPath => CreateIndexEditor(sp, mainVm!, projectPath),
                () => CreateRegistryAdmin(sp, mainVm!),
                ShowInfoDialog,
                ConfirmDialog,
                BrowseForFolder);
            // Initialize after construction so the picker factory closure sees a non-null mainVm.
            mainVm.ShowPicker();
            return mainVm;
        });

        services.AddTransient<MainWindow>();
    }

    private static ProjectPickerViewModel CreateProjectPicker(IServiceProvider sp, MainViewModel mainVm)
    {
        return new ProjectPickerViewModel(
            sp.GetRequiredService<AuthorConfigService>(),
            sp.GetRequiredService<GitHubService>(),
            sp.GetRequiredService<GitService>(),
            sp.GetRequiredService<IndexFileService>(),
            sp.GetRequiredService<ILogger>(),
            projectPath => mainVm.OpenProject(projectPath),
            ShowInfoDialog,
            ConfirmDialog,
            BrowseForFolder,
            PromptForString,
            () => mainVm.OpenRegistryAdmin(),
            () => ShowServerUploadSettingsDialog(sp));
    }

    private static RegistryAdminViewModel CreateRegistryAdmin(IServiceProvider sp, MainViewModel mainVm)
    {
        return new RegistryAdminViewModel(
            sp.GetRequiredService<AuthorConfigService>(),
            sp.GetRequiredService<GitHubService>(),
            sp.GetRequiredService<GitService>(),
            sp.GetRequiredService<ServerUploadService>(),
            sp.GetRequiredService<ILogger>(),
            ShowInfoDialog,
            ConfirmDialog,
            BrowseForFolder,
            BrowseForFile,
            () => mainVm.ShowPicker());
    }

    /// <summary>
    /// Modal one-line input prompt. Returns the entered text on OK, or null on Cancel.
    /// </summary>
    private static string? PromptForString(string title, string prompt, string? defaultValue)
    {
        var vm = new InputDialogViewModel(title, prompt, defaultValue);
        var dialog = new InputDialog(vm)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.ShowDialog();
        return vm.Confirmed && !string.IsNullOrWhiteSpace(vm.Value) ? vm.Value.Trim() : null;
    }

    private static IndexEditorViewModel CreateIndexEditor(IServiceProvider sp, MainViewModel mainVm, string projectPath)
    {
        return new IndexEditorViewModel(
            projectPath,
            sp.GetRequiredService<AuthorConfigService>(),
            sp.GetRequiredService<IndexFileService>(),
            sp.GetRequiredService<Sha256HashService>(),
            sp.GetRequiredService<GitService>(),
            sp.GetRequiredService<GitHubService>(),
            sp.GetRequiredService<ServerUploadService>(),
            sp.GetRequiredService<PatreonAuthorService>(),
            sp.GetRequiredService<ILogger>(),
            ShowInfoDialog,
            ConfirmDialog,
            BrowseForFile,
            () => mainVm.CloseProject(),
            (gameId, gameDisplayName, pluginId, projPath, initialSourceRepo, repos, deps, scriptInputs, existing)
                => ShowReleaseDialog(sp, gameId, gameDisplayName, pluginId, projPath, initialSourceRepo, repos, deps, scriptInputs, existing),
            (existingIds, repos) => ShowAddGameDialog(existingIds, repos),
            (pluginId, existing) => ShowAuthorInfoDialog(pluginId, existing),
            () => ShowServerUploadSettingsDialog(sp),
            sp.GetRequiredService<RegistryMembershipChecker>(),
            sp.GetRequiredService<ProjectReconciler>(),
            sp.GetRequiredService<IndexPublishCoordinator>());
    }

    private static void ShowServerUploadSettingsDialog(IServiceProvider sp)
    {
        var vm = new ServerUploadSettingsViewModel(
            sp.GetRequiredService<AuthorConfigService>(),
            sp.GetRequiredService<ServerUploadService>(),
            sp.GetRequiredService<ILogger>());
        var dialog = new ServerUploadSettingsDialog(vm)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.ShowDialog();
    }

    private static AccessibilityModManager.Core.Models.PluginAuthorInfo? ShowAuthorInfoDialog(
        string pluginId,
        AccessibilityModManager.Core.Models.PluginAuthorInfo? existing)
    {
        var vm = new AuthorInfoDialogViewModel(pluginId, existing);
        var dialog = new AuthorInfoDialog(vm)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.ShowDialog();
        return vm.Confirmed ? vm.ToModel() : null;
    }

    private static AddGameDialogViewModel? ShowAddGameDialog(
        ISet<string> existingGameIds,
        System.Collections.ObjectModel.ObservableCollection<string> availableGitHubRepos)
    {
        var vm = new AddGameDialogViewModel(existingGameIds, availableGitHubRepos, ShowInfoDialog);
        var dialog = new AddGameDialog(vm)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.ShowDialog();
        return vm.Confirmed ? vm : null;
    }

    private static ReleaseDialogResult? ShowReleaseDialog(
        IServiceProvider sp,
        string gameId, string gameDisplayName, string pluginId, string projectPath, string? initialSourceRepo,
        System.Collections.ObjectModel.ObservableCollection<string> availableGitHubRepos,
        IList<AccessibilityModManager.Core.Models.Dependency> deps,
        LifecycleScriptInputs scriptInputs,
        AccessibilityModManager.Core.Models.ModRelease? existingRelease)
    {
        // Build-package factory captures the per-game context so the dialog only needs to
        // hand it the user-typed version when "Build..." is clicked. The lifecycle script
        // bundle is captured the same way — it's constant for the lifetime of the release
        // dialog, so the closure forwards it on every Build click without changing the
        // Func shape.
        Func<string, string?> showBuildPackage = version
            => ShowBuildPackageDialog(sp, gameId, gameDisplayName, pluginId, version, deps, scriptInputs);

        var vm = new ReleaseDialogViewModel(
            gameId, gameDisplayName, pluginId, projectPath, initialSourceRepo,
            availableGitHubRepos,
            sp.GetRequiredService<Sha256HashService>(),
            sp.GetRequiredService<GitHubService>(),
            sp.GetRequiredService<AuthorConfigService>(),
            sp.GetRequiredService<PatreonAuthorService>(),
            sp.GetRequiredService<ServerUploadService>(),
            sp.GetRequiredService<ILogger>(),
            ShowInfoDialog,
            ConfirmDialog,
            BrowseForFile,
            showBuildPackage,
            existingRelease);

        var dialog = new ReleaseDialog(vm)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.ShowDialog();
        return vm.Result is null ? null : new ReleaseDialogResult(vm.Result, vm.GateChange);
    }

    private static string? ShowBuildPackageDialog(
        IServiceProvider sp,
        string gameId, string gameDisplayName, string pluginId, string suggestedVersion,
        IList<AccessibilityModManager.Core.Models.Dependency> deps,
        LifecycleScriptInputs scriptInputs)
    {
        var vm = new BuildPackageDialogViewModel(
            gameId, gameDisplayName, pluginId, suggestedVersion, deps,
            sp.GetRequiredService<ManifestBuilderService>(),
            BrowseForFolder,
            ShowInfoDialog,
            sp.GetRequiredService<ILogger>(),
            scriptInputs);

        var dialog = new BuildPackageDialog(vm)
        {
            Owner = Application.Current?.Windows.OfType<ReleaseDialog>().FirstOrDefault()
                    ?? Application.Current?.MainWindow
        };
        dialog.ShowDialog();
        return vm.ResultZipPath;
    }

    private static void ShowInfoDialog(string title, string message)
    {
        var owner = GetActiveOwnerWindow();
        if (owner != null)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static bool ConfirmDialog(string title, string message)
    {
        var owner = GetActiveOwnerWindow();
        var result = owner != null
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    private static string? BrowseForFolder(string? initialDirectory)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select project folder",
            Multiselect = false
        };
        if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        var owner = GetActiveOwnerWindow();
        var ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FolderName : null;
    }

    private static string? BrowseForFile(string title, string filter, string? initialDirectory)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = false
        };
        if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        var owner = GetActiveOwnerWindow();
        var ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Returns the topmost active window so file/folder pickers attach to whichever modal is
    /// currently open (Add-Release, Build-Package, etc.) rather than to MainWindow. Anchoring
    /// to MainWindow while a modal child is open breaks the modal stack: when the picker
    /// closes, focus returns to MainWindow and the open modal silently dismisses with
    /// <c>ShowDialog</c> returning false.
    /// </summary>
    private static Window? GetActiveOwnerWindow()
    {
        if (Application.Current == null) return null;
        var active = Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive);
        return active ?? Application.Current.MainWindow;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
