using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.AuthorTool.ViewModels;
using AccessibilityModManager.Authoring.Services;
using AccessibilityModManager.Authoring.ViewModels;
using AccessibilityModManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AccessibilityModManager.LinuxAuthorTool;

internal sealed class AuthorWindow : Window
{
    private readonly ServiceProvider services;
    private readonly MainViewModel main;
    private readonly ContentControl page = new();
    private readonly ILogger logger;
    private Window? activeReleaseDialog;
    private bool pageFocusPending = true;

    public AuthorWindow()
    {
        Width = 1000;
        Height = 730;
        MinWidth = 650;
        MinHeight = 450;
        Title = "Plugin Index Author";
        AutomationProperties.SetName(this, Title);
        logger = new LoggerConfiguration().MinimumLevel.Information()
            .WriteTo.File(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AccessibilityModManager-Author", "logs", "linux-author-.log"),
                rollingInterval: RollingInterval.Day).CreateLogger();
        var collection = new ServiceCollection();
        collection.AddSingleton(logger);
        collection.AddSingleton<HttpClient>();
        collection.AddSingleton(sp => new AuthorConfigService(sp.GetRequiredService<ILogger>(),
            Environment.GetEnvironmentVariable("AMM_AUTHOR_CONFIG_DIR")));
        collection.AddSingleton<IndexFileService>();
        collection.AddSingleton<Sha256HashService>();
        collection.AddSingleton<GitService>();
        collection.AddSingleton<GitHubService>();
        collection.AddSingleton<ManifestBuilderService>();
        collection.AddSingleton<RegistryMembershipChecker>();
        collection.AddSingleton<PatreonAuthorService>();
        collection.AddSingleton<ServerUploadService>();
        collection.AddSingleton<PublisherHeadStore>();
        collection.AddSingleton<ClaimSigningKeyStore>();
        collection.AddSingleton<IndexProofService>();
        collection.AddSingleton<ProjectReconciler>();
        collection.AddSingleton<IndexPublishCoordinator>();
        collection.AddSingleton<GitHubIndexPublisher>();
        collection.AddSingleton<UnsignedPublishGate>();
        services = collection.BuildServiceProvider();
        main = new MainViewModel(services.GetRequiredService<AuthorConfigService>(), logger,
            CreatePicker, CreateEditor, CreateRegistryAdmin, NativeDialogs.Info,
            NativeDialogs.Confirm, NativeDialogs.Folder);
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.WindowTitle)) Title = main.WindowTitle;
            if (e.PropertyName == nameof(MainViewModel.CurrentView)) ShowCurrent();
        };
        Content = page;
        Opened += (_, _) => QueueFocus();
        Activated += (_, _) => QueueFocus();
        main.ShowPicker();
        _ = services.GetRequiredService<PatreonAuthorService>().LoadAsync();
        Closed += (_, _) => services.Dispose();
    }

    private void ShowCurrent()
    {
        page.Content = main.CurrentView switch
        {
            ProjectPickerViewModel picker => AuthorPages.Picker(picker),
            IndexEditorViewModel editor => AuthorPages.Editor(editor),
            RegistryAdminViewModel registry => AuthorPages.Registry(registry),
            _ => null
        };
        pageFocusPending = true;
        QueueFocus();
    }

    private void QueueFocus()
    {
        if (!pageFocusPending) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!pageFocusPending || !IsActive) return;
            var preferred = page.Content is StackPanel { Tag: Control tagged } ? tagged : null;
            var target = preferred is ListBox list && list.SelectedIndex >= 0
                ? list.ContainerFromIndex(list.SelectedIndex) as Control ?? list
                : preferred ?? FindFirstFocusable(page);
            pageFocusPending = !(target?.Focus() ?? false);
        }, DispatcherPriority.ApplicationIdle);
    }

    private static Control? FindFirstFocusable(Control root)
    {
        if (root is Button or ListBox or TextBox) return root;
        if (root is Panel panel)
            return panel.Children.Select(FindFirstFocusable).FirstOrDefault(control => control is not null);
        if (root is ContentControl content && content.Content is Control child)
            return FindFirstFocusable(child);
        if (root is ScrollViewer scroll && scroll.Content is Control scrolled)
            return FindFirstFocusable(scrolled);
        return null;
    }

    private ProjectPickerViewModel CreatePicker() => new(
        services.GetRequiredService<AuthorConfigService>(),
        services.GetRequiredService<GitHubService>(), services.GetRequiredService<GitService>(),
        services.GetRequiredService<IndexFileService>(), logger, main.OpenProject,
        NativeDialogs.Info, NativeDialogs.Confirm, NativeDialogs.Folder,
        NativeDialogs.Input, main.OpenRegistryAdmin, () => _ = ShowServerSettingsAsync());

    private RegistryAdminViewModel CreateRegistryAdmin() => new(
        services.GetRequiredService<AuthorConfigService>(),
        services.GetRequiredService<GitHubService>(), services.GetRequiredService<GitService>(),
        services.GetRequiredService<ServerUploadService>(),
        services.GetRequiredService<ClaimSigningKeyStore>(), logger, NativeDialogs.Info,
        NativeDialogs.Confirm, NativeDialogs.Folder, NativeDialogs.File, main.ShowPicker);

    private IndexEditorViewModel CreateEditor(string path) => new(
        path, services.GetRequiredService<AuthorConfigService>(),
        services.GetRequiredService<IndexFileService>(), services.GetRequiredService<Sha256HashService>(),
        services.GetRequiredService<GitService>(), services.GetRequiredService<GitHubService>(),
        services.GetRequiredService<ServerUploadService>(),
        services.GetRequiredService<PatreonAuthorService>(), logger, NativeDialogs.Info,
        NativeDialogs.Confirm, NativeDialogs.File, main.CloseProject,
        ShowReleaseAsync, ShowAddGameAsync, ShowAuthorInfoAsync, ShowServerSettingsAsync,
        services.GetRequiredService<RegistryMembershipChecker>(),
        services.GetRequiredService<ProjectReconciler>(),
        services.GetRequiredService<IndexPublishCoordinator>(),
        services.GetRequiredService<GitHubIndexPublisher>(),
        services.GetRequiredService<UnsignedPublishGate>(), ShowSigningAsync);

    private async Task<AddGameDialogViewModel?> ShowAddGameAsync(ISet<string> ids,
        ObservableCollection<string> repos)
    {
        var vm = new AddGameDialogViewModel(ids, repos, NativeDialogs.Info);
        await AuthorPages.Dialog(this, "Add game", vm, AuthorPages.AddGame(vm),
            close => vm.CloseDialog = close);
        return vm.Confirmed ? vm : null;
    }

    private async Task<PluginAuthorInfo?> ShowAuthorInfoAsync(string pluginId, PluginAuthorInfo? existing)
    {
        var vm = new AuthorInfoDialogViewModel(pluginId, existing);
        await AuthorPages.Dialog(this, "Author info", vm, AuthorPages.AuthorInfo(vm),
            close => vm.CloseDialog = close);
        return vm.Confirmed ? vm.ToModel() : null;
    }

    private async Task ShowServerSettingsAsync()
    {
        var vm = new ServerUploadSettingsViewModel(
            services.GetRequiredService<AuthorConfigService>(),
            services.GetRequiredService<ServerUploadService>(), logger);
        await AuthorPages.Dialog(this, "Download server settings", vm,
            AuthorPages.ServerSettings(vm), close => vm.CloseDialog = close);
    }

    private async Task<ReleaseDialogResult?> ShowReleaseAsync(string gameId, string gameName,
        string pluginId, string projectPath, string? sourceRepo, ObservableCollection<string> repos,
        IList<Dependency> deps, LifecycleScriptInputs scripts, ModRelease? existing)
    {
        var vm = new ReleaseDialogViewModel(gameId, gameName, pluginId, projectPath,
            sourceRepo, repos, services.GetRequiredService<Sha256HashService>(),
            services.GetRequiredService<GitHubService>(),
            services.GetRequiredService<AuthorConfigService>(),
            services.GetRequiredService<PatreonAuthorService>(),
            services.GetRequiredService<ServerUploadService>(), logger,
            NativeDialogs.Info, NativeDialogs.Confirm, NativeDialogs.File,
            version => ShowBuildAsync(gameId, gameName, pluginId, version, deps, scripts), existing);
        try
        {
            await AuthorPages.Dialog(this, vm.DialogTitle, vm, AuthorPages.Release(vm),
                close => vm.CloseDialog = close, 680, 780,
                window => activeReleaseDialog = window);
        }
        finally { activeReleaseDialog = null; }
        return vm.Result is null ? null : new ReleaseDialogResult(vm.Result, vm.GateChange);
    }

    private async Task<string?> ShowBuildAsync(string gameId, string gameName, string pluginId,
        string version, IList<Dependency> deps, LifecycleScriptInputs scripts)
    {
        var vm = new BuildPackageDialogViewModel(gameId, gameName, pluginId, version, deps,
            services.GetRequiredService<ManifestBuilderService>(), NativeDialogs.Folder,
            NativeDialogs.Info, logger, scripts);
        await AuthorPages.Dialog(activeReleaseDialog ?? this, "Build mod package", vm, AuthorPages.Build(vm),
            close => vm.CloseDialog = close, 650, 700);
        return vm.ResultZipPath;
    }

    private async Task ShowSigningAsync(string pluginId, RegistryTrustState trust)
    {
        var vm = new ClaimSigningViewModel(pluginId,
            services.GetRequiredService<ClaimSigningKeyStore>(),
            services.GetRequiredService<PublisherHeadStore>(), logger,
            NativeDialogs.Info, NativeDialogs.Confirm, NativeDialogs.SaveFile,
            NativeDialogs.File, trust);
        await AuthorPages.Dialog(this, "Catalog signing", vm, AuthorPages.Signing(vm), null, 620, 550);
    }
}

internal static class NativeDialogs
{
    public static void Info(string title, string message) => Run("--info", title, message);
    public static bool Confirm(string title, string message) => Run("--question", title, message).ExitCode == 0;
    public static string? Input(string title, string message, string? initial) =>
        Value("--entry", title, message, initial);
    public static string? Folder(string? initial) => Value("--file-selection", "Choose folder",
        null, initial, "--directory");
    public static string? File(string title, string filter, string? initial) =>
        Value("--file-selection", title, null, initial);
    public static string? SaveFile(string title, string suggested, string filter) =>
        Value("--file-selection", title, null, suggested, "--save", "--confirm-overwrite");

    private static string? Value(string mode, string title, string? message,
        string? initial, params string[] extra)
    {
        var result = Run(mode, title, message, initial, extra);
        return result.ExitCode == 0 ? result.Output.TrimEnd('\r', '\n') : null;
    }

    private static (int ExitCode, string Output) Run(string mode, string title,
        string? message, string? initial = null, params string[] extra)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("zenity")
        {
            UseShellExecute = false, RedirectStandardOutput = true
        };
        process.StartInfo.ArgumentList.Add(mode);
        process.StartInfo.ArgumentList.Add("--modal");
        process.StartInfo.ArgumentList.Add("--title=" + title);
        if (message is not null) process.StartInfo.ArgumentList.Add("--text=" + message);
        if (initial is not null)
            process.StartInfo.ArgumentList.Add(mode == "--entry" ? "--entry-text=" + initial : "--filename=" + initial);
        foreach (var arg in extra) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
