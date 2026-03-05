using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.App.ViewModels;

public partial class GameDetailsViewModel : ObservableObject
{
    private readonly IPluginRepoClient _repoClient;
    private readonly IInstallerEngine _installerEngine;
    private readonly IReceiptStore _receiptStore;
    private readonly IDependencyChecker _dependencyChecker;
    private readonly ILogger _logger;
    private readonly Action _navigateBack;
    private readonly Func<string, string, IProgress<ProgressInfo>, CancellationToken, Task> _showProgress;

    [ObservableProperty]
    private string _gameId = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string? _installPath;

    [ObservableProperty]
    private string _selectedChannel = "stable";

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isLoading;

    public ObservableCollection<ModReleaseGroup> ModGroups { get; } = [];
    public ObservableCollection<DependencyItemViewModel> Dependencies { get; } = [];

    private GameInstall? _gameInstall;
    private Dictionary<string, PluginRepoIndex> _activeIndexes = [];

    public GameDetailsViewModel(
        IPluginRepoClient repoClient,
        IInstallerEngine installerEngine,
        IReceiptStore receiptStore,
        IDependencyChecker dependencyChecker,
        ILogger logger,
        Action navigateBack,
        Func<string, string, IProgress<ProgressInfo>, CancellationToken, Task> showProgress)
    {
        _repoClient = repoClient;
        _installerEngine = installerEngine;
        _receiptStore = receiptStore;
        _dependencyChecker = dependencyChecker;
        _logger = logger;
        _navigateBack = navigateBack;
        _showProgress = showProgress;
    }

    public void Load(GameInstall gameInstall, Dictionary<string, PluginRepoIndex> activeIndexes)
    {
        _gameInstall = gameInstall;
        _activeIndexes = activeIndexes;
        GameId = gameInstall.Game.GameId;
        DisplayName = gameInstall.Game.DisplayName;
        InstallPath = gameInstall.InstallPath;
        _ = LoadReleasesAsync();
    }

    private async Task LoadReleasesAsync()
    {
        IsLoading = true;
        ModGroups.Clear();
        Dependencies.Clear();

        try
        {
            var receipt = await _receiptStore.LoadAsync(GameId, _gameInstall!.PluginId);

            foreach (var (pluginId, index) in _activeIndexes)
            {
                if (!index.ReleasesByGameId.TryGetValue(GameId, out var releases))
                    continue;

                var filtered = releases
                    .Where(r => r.Channel == SelectedChannel)
                    .OrderByDescending(r => r.Version)
                    .ToList();

                if (filtered.Count == 0) continue;

                var pluginReceipt = await _receiptStore.LoadAsync(GameId, pluginId);

                ModGroups.Add(new ModReleaseGroup
                {
                    PluginId = pluginId,
                    Releases = filtered,
                    SelectedRelease = filtered.First(),
                    InstalledVersion = pluginReceipt?.InstalledVersion
                });
            }

            // Check dependencies defined on the game
            if (_gameInstall != null && _gameInstall.Game.Dependencies.Count > 0)
            {
                var depStatuses = await _dependencyChecker.CheckAsync(_gameInstall);
                foreach (var ds in depStatuses)
                {
                    Dependencies.Add(new DependencyItemViewModel(ds, _dependencyChecker, _logger));
                }
            }

            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load releases for {GameId}", GameId);
            StatusMessage = $"Failed to load releases: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedChannelChanged(string value)
    {
        _ = LoadReleasesAsync();
    }

    [RelayCommand]
    private async Task InstallAsync(ModReleaseGroup? group, CancellationToken ct)
    {
        if (group?.SelectedRelease == null || _gameInstall == null) return;
        var release = group.SelectedRelease;

        StatusMessage = "Downloading...";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new Progress<ProgressInfo>();

        try
        {
            var tempZip = Path.Combine(Path.GetTempPath(), "AccessibilityModManager",
                $"download_{Guid.NewGuid():N}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(tempZip)!);

            await _showProgress("Installing", $"Installing {DisplayName} v{release.Version}...",
                progress, cts.Token);

            var downloadedFile = await _repoClient.DownloadPackageAsync(
                release.PackageUrl, tempZip, progress, cts.Token);

            if (!await _repoClient.VerifySha256Async(downloadedFile, release.Sha256, cts.Token))
            {
                try { File.Delete(downloadedFile); } catch { }
                StatusMessage = "Download failed SHA256 verification. Install aborted.";
                return;
            }

            await _installerEngine.InstallAsync(_gameInstall, release, downloadedFile, cts.Token);
            group.InstalledVersion = release.Version;
            StatusMessage = $"Installed v{release.Version} successfully.";

            try { File.Delete(downloadedFile); } catch { }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Install cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Install failed for {PluginId}/{GameId}", release.PluginId, GameId);
            StatusMessage = $"Install failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task UpdateAsync(ModReleaseGroup? group, CancellationToken ct)
    {
        if (group?.SelectedRelease == null || _gameInstall == null) return;
        var release = group.SelectedRelease;

        StatusMessage = "Updating...";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new Progress<ProgressInfo>();

        try
        {
            var tempZip = Path.Combine(Path.GetTempPath(), "AccessibilityModManager",
                $"download_{Guid.NewGuid():N}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(tempZip)!);

            await _showProgress("Updating", $"Updating {DisplayName} to v{release.Version}...",
                progress, cts.Token);

            var downloadedFile = await _repoClient.DownloadPackageAsync(
                release.PackageUrl, tempZip, progress, cts.Token);

            if (!await _repoClient.VerifySha256Async(downloadedFile, release.Sha256, cts.Token))
            {
                try { File.Delete(downloadedFile); } catch { }
                StatusMessage = "Download failed SHA256 verification. Update aborted.";
                return;
            }

            await _installerEngine.UpdateAsync(_gameInstall, release, downloadedFile, cts.Token);
            group.InstalledVersion = release.Version;
            StatusMessage = $"Updated to v{release.Version} successfully.";

            try { File.Delete(downloadedFile); } catch { }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Update cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Update failed for {PluginId}/{GameId}", release.PluginId, GameId);
            StatusMessage = $"Update failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task UninstallAsync(ModReleaseGroup? group, CancellationToken ct)
    {
        if (group == null || _gameInstall == null) return;

        StatusMessage = "Uninstalling...";
        try
        {
            await _installerEngine.UninstallAsync(_gameInstall, group.PluginId, ct);
            group.InstalledVersion = null;
            StatusMessage = "Uninstalled successfully.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Uninstall failed for {PluginId}/{GameId}", group.PluginId, GameId);
            StatusMessage = $"Uninstall failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        if (string.IsNullOrEmpty(InstallPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = InstallPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to open game folder");
            StatusMessage = $"Could not open folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private void GoBack() => _navigateBack();
}

public partial class ModReleaseGroup : ObservableObject
{
    public required string PluginId { get; init; }
    public required List<ModRelease> Releases { get; init; }

    [ObservableProperty]
    private ModRelease? _selectedRelease;

    [ObservableProperty]
    private string? _installedVersion;

    public bool IsInstalled => InstalledVersion != null;
    public bool CanUpdate => IsInstalled && SelectedRelease != null && SelectedRelease.Version != InstalledVersion;

    partial void OnInstalledVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(CanUpdate));
    }

    partial void OnSelectedReleaseChanged(ModRelease? value)
    {
        OnPropertyChanged(nameof(CanUpdate));
    }
}

public partial class DependencyItemViewModel : ObservableObject
{
    private readonly IDependencyChecker _checker;
    private readonly ILogger _logger;

    public DependencyStatus Status { get; }
    public string Name => Status.Dependency.Id;
    public string Type => Status.Dependency.Type;
    public bool IsRequired => Status.Dependency.Required;
    public DependencyStatusKind StatusKind => Status.Status;
    public string? Details => Status.Details;

    public string StatusText => StatusKind switch
    {
        DependencyStatusKind.Installed => "Installed",
        DependencyStatusKind.Missing => IsRequired ? "Missing (required)" : "Missing (optional)",
        DependencyStatusKind.Incompatible => $"Incompatible: {Details}",
        _ => "Unknown"
    };

    public DependencyItemViewModel(DependencyStatus status, IDependencyChecker checker, ILogger logger)
    {
        Status = status;
        _checker = checker;
        _logger = logger;
    }

    [RelayCommand]
    private async Task FixAsync(CancellationToken ct)
    {
        try
        {
            await _checker.FixAsync(Status.Dependency, ct);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fix dependency {DepId}", Name);
        }
    }
}
