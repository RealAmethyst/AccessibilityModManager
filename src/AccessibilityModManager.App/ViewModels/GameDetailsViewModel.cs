using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
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
    private readonly IConfigService _configService;
    private readonly ILogger _logger;
    private readonly Action _navigateBack;

    /// <summary>
    /// Opens a progress dialog, runs <c>work</c> with the dialog's cancellation token, and closes
    /// the dialog when the work returns (success, failure, or cancel). Caller still handles the
    /// resulting <see cref="OperationCanceledException"/> / other exceptions.
    /// </summary>
    private readonly Func<string, string, IProgress<ProgressInfo>, Func<CancellationToken, Task>, CancellationToken, Task> _runWithProgress;

    /// <summary>
    /// Shows a modal info dialog (title + message + OK button). Used to confirm successful
    /// install / update / uninstall.
    /// </summary>
    private readonly Action<string, string> _showInfoDialog;

    /// <summary>
    /// Shows a modal Yes/No confirmation dialog. Returns true if the user clicks Yes.
    /// Used for destructive actions like uninstall.
    /// </summary>
    private readonly Func<string, string, bool> _confirmDialog;

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

    /// <summary>
    /// True when more than one release channel is available across all mod releases for this
    /// game. The Channel selector is hidden otherwise — if the only channel offered is
    /// "stable", the selector is just clutter.
    /// </summary>
    [ObservableProperty]
    private bool _hasMultipleChannels;

    public ObservableCollection<ModReleaseGroup> ModGroups { get; } = [];
    public ObservableCollection<DependencyItemViewModel> Dependencies { get; } = [];

    /// <summary>
    /// True if at least one ModReleaseGroup currently reports an installed version. Drives the
    /// Play button's visibility — no point launching the game if no mod is installed.
    /// </summary>
    public bool AnyModInstalled => ModGroups.Any(g => g.IsInstalled);

    /// <summary>
    /// Raised after a successful install / update / uninstall (and the user has dismissed the
    /// success popup). The view listens to this and moves keyboard focus back into the mod
    /// list so screen readers don't lose context.
    /// </summary>
    public event Action? OperationCompleted;

    private GameInstall? _gameInstall;
    private Dictionary<string, PluginRepoIndex> _activeIndexes = [];

    public GameDetailsViewModel(
        IPluginRepoClient repoClient,
        IInstallerEngine installerEngine,
        IReceiptStore receiptStore,
        IDependencyChecker dependencyChecker,
        IConfigService configService,
        ILogger logger,
        Action navigateBack,
        Action<string, string> showInfoDialog,
        Func<string, string, bool> confirmDialog,
        Func<string, string, IProgress<ProgressInfo>, Func<CancellationToken, Task>, CancellationToken, Task> runWithProgress)
    {
        _repoClient = repoClient;
        _installerEngine = installerEngine;
        _receiptStore = receiptStore;
        _dependencyChecker = dependencyChecker;
        _configService = configService;
        _logger = logger;
        _navigateBack = navigateBack;
        _showInfoDialog = showInfoDialog;
        _confirmDialog = confirmDialog;
        _runWithProgress = runWithProgress;
    }

    public void Load(GameInstall gameInstall, Dictionary<string, PluginRepoIndex> activeIndexes)
    {
        _gameInstall = gameInstall;
        _activeIndexes = activeIndexes;
        GameId = gameInstall.Game.GameId;
        DisplayName = gameInstall.Game.DisplayName;
        InstallPath = gameInstall.InstallPath;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();
            var desired = config.DefaultChannel;
            if (SelectedChannel == desired)
            {
                // Same value won't fire OnSelectedChannelChanged, load releases explicitly.
                await LoadReleasesAsync();
            }
            else
            {
                // Setter triggers OnSelectedChannelChanged → LoadReleasesAsync.
                SelectedChannel = desired;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load default channel from config");
            await LoadReleasesAsync();
        }
    }

    private async Task LoadReleasesAsync()
    {
        IsLoading = true;
        ModGroups.Clear();
        Dependencies.Clear();

        try
        {
            foreach (var (pluginId, index) in _activeIndexes)
            {
                if (!index.ReleasesByGameId.TryGetValue(GameId, out var releases))
                    continue;

                var filtered = releases
                    .Where(r => r.Channel == SelectedChannel)
                    .OrderByDescending(r => r.Version, VersionComparer.Instance)
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

            // ModGroups now reflects current installed state — re-evaluate the Play button gate.
            OnPropertyChanged(nameof(AnyModInstalled));

            // Hide the Channel selector when there's only one channel offered across all
            // releases — saves tab stops + visual clutter when no beta builds exist.
            HasMultipleChannels = _activeIndexes.Values
                .SelectMany(idx => idx.ReleasesByGameId.TryGetValue(GameId, out var rels)
                    ? rels.Select(r => r.Channel)
                    : Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1;

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
    private Task InstallAsync(ModReleaseGroup? group, CancellationToken ct) =>
        RunInstallOrUpdate(group, ct, isUpdate: false);

    [RelayCommand]
    private Task UpdateAsync(ModReleaseGroup? group, CancellationToken ct) =>
        RunInstallOrUpdate(group, ct, isUpdate: true);

    private async Task RunInstallOrUpdate(ModReleaseGroup? group, CancellationToken ct, bool isUpdate)
    {
        if (group?.SelectedRelease == null || _gameInstall == null) return;
        var release = group.SelectedRelease;
        var verb = isUpdate ? "Update" : "Install";
        var verbing = isUpdate ? "Updating" : "Installing";

        StatusMessage = $"{verbing}...";
        var progress = new Progress<ProgressInfo>();

        var tempZip = Path.Combine(Path.GetTempPath(), "AccessibilityModManager",
            $"download_{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(tempZip)!);

        var hashFailed = false;
        var downloadedFile = string.Empty;

        try
        {
            await _runWithProgress(verb,
                $"{verbing} {DisplayName} v{release.Version}...",
                progress,
                async innerCt =>
                {
                    downloadedFile = await _repoClient.DownloadPackageAsync(
                        release.PackageUrl, tempZip, progress, innerCt);

                    if (!await _repoClient.VerifySha256Async(downloadedFile, release.Sha256, innerCt))
                    {
                        hashFailed = true;
                        return;
                    }

                    if (isUpdate)
                        await _installerEngine.UpdateAsync(_gameInstall, release, downloadedFile, innerCt);
                    else
                        await _installerEngine.InstallAsync(_gameInstall, release, downloadedFile, innerCt);
                },
                ct);

            if (hashFailed)
            {
                StatusMessage = $"Download failed SHA256 verification. {verb} aborted.";
                return;
            }

            group.InstalledVersion = release.Version;
            OnPropertyChanged(nameof(AnyModInstalled));
            StatusMessage = null;
            _showInfoDialog(
                $"{verb} completed",
                $"{(isUpdate ? "Updated" : "Installed")} {group.ModName}");
            OperationCompleted?.Invoke();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{verb} cancelled.";
        }
        catch (MissingRequiredDependencyException ex)
        {
            _logger.Warning("{Verb} blocked by missing dependencies for {PluginId}/{GameId}", verb, release.PluginId, GameId);
            var names = string.Join(", ", ex.Blockers.Select(b => b.Dependency.Id));
            StatusMessage = $"Cannot {verb.ToLowerInvariant()} — install required dependencies first: {names}. " +
                            "Open the Dependencies section below and use Fix.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Verb} failed for {PluginId}/{GameId}", verb, release.PluginId, GameId);
            StatusMessage = $"{verb} failed: {ex.Message}";
        }
        finally
        {
            if (!string.IsNullOrEmpty(downloadedFile))
            {
                try { File.Delete(downloadedFile); } catch { }
            }
        }
    }

    [RelayCommand]
    private async Task UninstallAsync(ModReleaseGroup? group, CancellationToken ct)
    {
        if (group == null || _gameInstall == null) return;

        // Confirm before doing anything destructive — uninstall removes installed files and
        // restores the originals from backup.
        if (!_confirmDialog("Uninstall mod?", $"Remove the mod by {group.PluginId} from {DisplayName}?"))
            return;

        StatusMessage = "Uninstalling...";
        try
        {
            await _installerEngine.UninstallAsync(_gameInstall, group.PluginId, ct);
            group.InstalledVersion = null;
            OnPropertyChanged(nameof(AnyModInstalled));
            StatusMessage = null;
            _showInfoDialog("Uninstall completed", $"Uninstalled {group.ModName}");
            OperationCompleted?.Invoke();
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

    /// <summary>
    /// Launch the game. Prefers the steam:// protocol when a Steam App ID is known so Steam
    /// handles launchers, achievements, cloud saves, and overlay correctly. Falls back to a
    /// direct exe launch when only ExeName is configured, and reports a clear status when
    /// neither is available.
    /// </summary>
    [RelayCommand]
    private void Play()
    {
        if (_gameInstall == null) return;

        var steamAppId = _gameInstall.Game.SteamAppId;
        var exeName = _gameInstall.Game.ExeName;

        try
        {
            if (!string.IsNullOrEmpty(steamAppId))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://run/{steamAppId}",
                    UseShellExecute = true
                });
                StatusMessage = $"Launching {DisplayName} via Steam...";
                return;
            }

            if (!string.IsNullOrEmpty(exeName) && !string.IsNullOrEmpty(InstallPath))
            {
                var exePath = Path.Combine(InstallPath, exeName);
                if (File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true,
                        WorkingDirectory = InstallPath
                    });
                    StatusMessage = $"Launching {DisplayName}...";
                    return;
                }
                StatusMessage = $"Cannot launch — {exeName} not found in {InstallPath}.";
                return;
            }

            StatusMessage = "Cannot launch — no Steam App ID or executable name configured for this game.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to launch {GameId}", GameId);
            StatusMessage = $"Could not launch game: {ex.Message}";
        }
    }

    /// <summary>
    /// Re-runs the dependency check for the current game install. Useful right after the user
    /// clicks Fix on a missing dependency and installs it externally — without this, the UI
    /// would still show the dep as missing until the user navigated away and back.
    /// </summary>
    [RelayCommand]
    private async Task RecheckDependenciesAsync(CancellationToken ct)
    {
        if (_gameInstall == null) return;

        StatusMessage = "Rechecking dependencies...";
        try
        {
            Dependencies.Clear();
            if (_gameInstall.Game.Dependencies.Count > 0)
            {
                var depStatuses = await _dependencyChecker.CheckAsync(_gameInstall, ct);
                foreach (var ds in depStatuses)
                {
                    Dependencies.Add(new DependencyItemViewModel(ds, _dependencyChecker, _logger));
                }
            }

            var missing = Dependencies.Count(d =>
                d.IsRequired && d.StatusKind != DependencyStatusKind.Installed);
            StatusMessage = missing == 0
                ? "All required dependencies are installed."
                : $"{missing} required dependency still missing. Install before retrying.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to recheck dependencies for {GameId}", GameId);
            StatusMessage = $"Recheck failed: {ex.Message}";
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

    /// <summary>
    /// Best-effort human name for the mod, derived from the package URL. For GitHub-hosted
    /// packages this is the source repo name (e.g. "DigimonNOAccess"). Falls back to "mod" when
    /// the URL doesn't match a GitHub release pattern (NAS hosts, custom CDN, etc.).
    /// </summary>
    public string ModName
    {
        get
        {
            var url = Releases.FirstOrDefault()?.PackageUrl;
            if (url is not null && string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                // Segments for /owner/repo/releases/download/<tag>/<file>:
                // ["/", "owner/", "repo/", "releases/", ...]
                var segments = url.Segments;
                if (segments.Length >= 4 && segments[3].TrimEnd('/').Equals("releases", StringComparison.OrdinalIgnoreCase))
                {
                    return segments[2].TrimEnd('/');
                }
            }
            return "mod";
        }
    }

    /// <summary>
    /// One-line summary used as the screen-reader announcement on the containing list item.
    /// Bound by AutomationProperties.Name in GameDetailsView.xaml. Matches the visible TextBlock
    /// wording so the screen reader and the on-screen text agree.
    /// </summary>
    public string AnnouncementText =>
        InstalledVersion is null
            ? $"{ModName} by {PluginId}, not installed"
            : $"{ModName} by {PluginId}, version {InstalledVersion} installed";

    public string InstallButtonName => $"Install {ModName}";
    public string UpdateButtonName => $"Update {ModName}";
    public string UninstallButtonName => $"Uninstall {ModName}";

    partial void OnInstalledVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(AnnouncementText));
    }

    partial void OnSelectedReleaseChanged(ModRelease? value)
    {
        OnPropertyChanged(nameof(CanUpdate));
    }

    public override string ToString() => AnnouncementText;
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

    /// <summary>
    /// One-line summary used as the screen-reader announcement on the containing item.
    /// Bound by AutomationProperties.Name in GameDetailsView.xaml.
    /// </summary>
    public string AnnouncementText => $"{Name}, {StatusText}";

    public DependencyItemViewModel(DependencyStatus status, IDependencyChecker checker, ILogger logger)
    {
        Status = status;
        _checker = checker;
        _logger = logger;
    }

    public override string ToString() => AnnouncementText;

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
