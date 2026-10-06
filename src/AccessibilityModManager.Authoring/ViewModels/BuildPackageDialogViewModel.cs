using System.Collections.ObjectModel;
using System.IO;
using AccessibilityModManager.Authoring.Services;
using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.Authoring.ViewModels;

public sealed partial class BuildPackageDialogViewModel : ObservableObject
{
    private readonly ManifestBuilderService _builder;
    private readonly string _gameId;
    private readonly string _pluginId;
    private readonly IList<Dependency> _dependencies;
    private readonly LifecycleScriptInputs _scripts;
    private readonly Func<string?, string?> _browseForFolder;
    private readonly Action<string, string> _showInfoDialog;
    private readonly ILogger _logger;

    public string GameDisplayName { get; }

    [ObservableProperty]
    private string? _sourceFolder;

    [ObservableProperty]
    private string? _version;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProton))]
    private string _targetPlatform = "windows";

    public bool IsProton => TargetPlatform == ReleaseTarget.Proton;

    [ObservableProperty]
    private string? _steamAppId;

    [ObservableProperty]
    private string? _gameExecutable;

    [ObservableProperty]
    private string _launchMode = "direct";

    [ObservableProperty]
    private string? _launcherPath;

    [ObservableProperty]
    private string? _bridgeDirectory;

    [ObservableProperty]
    private string? _wineDllOverrides;

    [ObservableProperty]
    private string? _wineDllProxyPaths;

    [ObservableProperty]
    private string? _windowsDesktopRuntimeVersion;

    [ObservableProperty]
    private string? _windowsDesktopRuntimeSha512;

    [ObservableProperty]
    private string? _reloadedRoot;

    [ObservableProperty]
    private string? _reloadedModId;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<DetectedEntry> DetectedEntries { get; } = [];

    public Action? CloseDialog { get; set; }
    public string? ResultZipPath { get; private set; }

    public BuildPackageDialogViewModel(
        string gameId,
        string gameDisplayName,
        string pluginId,
        string suggestedVersion,
        IList<Dependency> dependencies,
        ManifestBuilderService builder,
        Func<string?, string?> browseForFolder,
        Action<string, string> showInfoDialog,
        ILogger logger,
        LifecycleScriptInputs? scripts = null)
    {
        _gameId = gameId;
        GameDisplayName = gameDisplayName;
        _pluginId = pluginId;
        _version = suggestedVersion;
        _dependencies = dependencies;
        _builder = builder;
        _browseForFolder = browseForFolder;
        _showInfoDialog = showInfoDialog;
        _logger = logger;
        _scripts = scripts ?? new LifecycleScriptInputs();
    }

    [RelayCommand]
    private void PickSource()
    {
        var path = _browseForFolder(SourceFolder);
        if (string.IsNullOrEmpty(path)) return;
        SourceFolder = path;
        RebuildPreview();
    }

    partial void OnSourceFolderChanged(string? value) => RebuildPreview();

    private void RebuildPreview()
    {
        DetectedEntries.Clear();
        if (string.IsNullOrEmpty(SourceFolder) || !Directory.Exists(SourceFolder)) return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(SourceFolder, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(entry);
            var isFolder = Directory.Exists(entry);
            DetectedEntries.Add(new DetectedEntry
            {
                Name = name,
                IsFolder = isFolder,
                Description = isFolder
                    ? $"folder copies to: game-folder/{name}/"
                    : $"file copies to: game-folder/{name}"
            });
        }
    }

    [RelayCommand]
    private async Task BuildAsync()
    {
        if (string.IsNullOrEmpty(SourceFolder))
        {
            _showInfoDialog("Pick a folder",
                "Pick the folder containing your mod's files first. The folder layout is preserved as-is — top-level files copy to the game folder, top-level folders copy to the game folder.");
            return;
        }
        if (string.IsNullOrWhiteSpace(Version))
        {
            _showInfoDialog("Version required", "Type the version number, e.g. 1.0.0 or 1.8.0.");
            return;
        }

        IsBusy = true;
        StatusMessage = "Building wrapped package...";
        try
        {
            var sanitizedVersion = Version!.Trim();
            var fileName = $"{_gameId}-v{sanitizedVersion}" +
                           (TargetPlatform == ReleaseTarget.Windows ? "" : "-" + TargetPlatform) + "-amm.zip";
            var outputPath = Path.Combine(ManifestBuilderService.GetBuildsDirectory(), fileName);
            ProtonLaunchConfig? protonLaunch = null;
            if (IsProton)
            {
                protonLaunch = new ProtonLaunchConfig
                {
                    SteamAppId = SteamAppId?.Trim() ?? "",
                    GameDisplayName = GameDisplayName,
                    GameExecutable = GameExecutable?.Trim() ?? "",
                    LaunchMode = LaunchMode,
                    LauncherPath = EmptyAsNull(LauncherPath),
                    BridgeDirectory = EmptyAsNull(BridgeDirectory),
                    WineDllOverrides = (WineDllOverrides ?? "")
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    WineDllProxyPaths = ParseProxyPaths(WineDllProxyPaths),
                    WindowsDesktopRuntimeVersion = EmptyAsNull(WindowsDesktopRuntimeVersion),
                    WindowsDesktopRuntimeSha512 = EmptyAsNull(WindowsDesktopRuntimeSha512),
                    ReloadedRoot = EmptyAsNull(ReloadedRoot),
                    ReloadedModId = EmptyAsNull(ReloadedModId)
                };
            }

            var result = await _builder.BuildPackageAsync(
                SourceFolder,
                _gameId,
                _pluginId,
                sanitizedVersion,
                _dependencies,
                outputPath,
                targetPlatform: TargetPlatform,
                protonLaunch: protonLaunch,
                scripts: TargetPlatform == ReleaseTarget.Windows ? _scripts : null);

            ResultZipPath = result.ZipPath;
            StatusMessage = $"Built {result.FileCount} files. Returning to release dialog.";
            CloseDialog?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Build failed");
            _showInfoDialog("Build failed", ex.Message);
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string? EmptyAsNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, string> ParseProxyPaths(string? value)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in (value ?? "").Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0 ||
                !paths.TryAdd(parts[0], parts[1]))
                throw new InvalidOperationException(
                    "Wine proxy paths must use unique name=relative/path.dll entries separated by semicolons.");
        }
        return paths;
    }

    [RelayCommand]
    private void Cancel()
    {
        ResultZipPath = null;
        CloseDialog?.Invoke();
    }
}

public sealed class DetectedEntry
{
    public required string Name { get; init; }
    public required bool IsFolder { get; init; }
    public required string Description { get; init; }
    public override string ToString() => Name;
}
