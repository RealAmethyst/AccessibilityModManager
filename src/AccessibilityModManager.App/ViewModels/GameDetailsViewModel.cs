using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Patreon;
using AccessibilityModManager.Infrastructure.Security;
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
    private readonly IGameVerifier _gameVerifier;
    private readonly IAsciiPathShimService _shimService;
    private readonly IRegistryGameDetector _registryDetector;
    private readonly DependencyAutoInstaller _depAutoInstaller;
    private readonly PatreonService _patreon;
    private readonly ILogger _logger;
    private readonly Action _navigateBack;

    /// <summary>
    /// Opens the developer's own page for the mod being shown. Null in contexts that have no
    /// navigation host (tests), which simply hides the button.
    /// </summary>
    private readonly Action<PluginEntry>? _navigateToDeveloper;

    /// <summary>
    /// Opens a progress dialog, runs <c>work</c> with the dialog's cancellation token, and closes
    /// the dialog when the work returns (success, failure, or cancel). The work delegate receives
    /// an <see cref="IScriptHost"/> wired to the same dialog so lifecycle script confirmations
    /// and live stdout streaming both surface in the running progress UI. Caller still handles
    /// the resulting <see cref="OperationCanceledException"/> / other exceptions.
    /// </summary>
    private readonly Func<string, string, IProgress<ProgressInfo>, Func<IScriptHost, IDependencyHost, CancellationToken, Task>, CancellationToken, Task> _runWithProgress;

    /// <summary>
    /// Builds an <see cref="IScriptHost"/> backed only by a modal warning dialog (no progress
    /// streaming). Used by the uninstall path which doesn't open a progress dialog.
    /// </summary>
    private readonly Func<IScriptHost> _createUninstallScriptHost;

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

    /// <summary>
    /// Opens the in-app changelog viewer for a release. Args: modName, version, notes (markdown), externalUrl (fallback).
    /// </summary>
    private readonly Action<string, string, string?, string?> _showChangelog;

    /// <summary>
    /// Opens an OpenFile dialog. Args: title, filter ("ZIP files (*.zip)|*.zip"), suggested
    /// filename. Returns the picked path or null on cancel. Used for the creator-of-campaign
    /// install path on Patreon-gated releases — Patreon's API doesn't return a download URL
    /// for creators viewing their own paid posts, so we ask them to point at the wrapped
    /// ZIP they already have locally.
    /// </summary>
    private readonly Func<string, string, string?, string?> _pickFile;

    /// <summary>
    /// Opens a folder picker (arg: dialog title). Returns the chosen folder or null on cancel.
    /// Used to let the user choose where a portable-app (emulator) game-installer extracts to.
    /// </summary>
    private readonly Func<string, string?> _pickFolder;

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
    private GameDefinition? _gameDef;
    private string _pluginId = string.Empty;
    private Dictionary<string, PluginRepoIndex> _activeIndexes = [];

    /// <summary>
    /// The developer whose mod the user opened to get here — supplied by the caller that knows,
    /// never inferred from <see cref="_pluginId"/>.
    ///
    /// <para>That distinction is the whole point: <see cref="_pluginId"/> comes from the
    /// <see cref="GameInstall"/>, and when several developers support one game the mods list falls
    /// back to whichever plugin's detection found the folder. Reading the owner off it could open a
    /// different developer's page than the mod the user actually chose.</para>
    ///
    /// <para>Null when the caller couldn't supply one, which hides the button — better no shortcut
    /// than a shortcut to the wrong person.</para>
    /// </summary>
    private PluginEntry? _ownerPlugin;

    public GameDetailsViewModel(
        IPluginRepoClient repoClient,
        IInstallerEngine installerEngine,
        IReceiptStore receiptStore,
        IDependencyChecker dependencyChecker,
        IConfigService configService,
        IGameVerifier gameVerifier,
        IAsciiPathShimService shimService,
        IRegistryGameDetector registryDetector,
        DependencyAutoInstaller depAutoInstaller,
        PatreonService patreon,
        ILogger logger,
        Action navigateBack,
        Action<string, string> showInfoDialog,
        Func<string, string, bool> confirmDialog,
        Func<string, string, IProgress<ProgressInfo>, Func<IScriptHost, IDependencyHost, CancellationToken, Task>, CancellationToken, Task> runWithProgress,
        Func<IScriptHost> createUninstallScriptHost,
        Action<string, string, string?, string?> showChangelog,
        Func<string, string, string?, string?> pickFile,
        Func<string, string?> pickFolder,
        Action<PluginEntry>? navigateToDeveloper = null)
    {
        _navigateToDeveloper = navigateToDeveloper;
        _repoClient = repoClient;
        _installerEngine = installerEngine;
        _receiptStore = receiptStore;
        _dependencyChecker = dependencyChecker;
        _configService = configService;
        _gameVerifier = gameVerifier;
        _shimService = shimService;
        _registryDetector = registryDetector;
        _depAutoInstaller = depAutoInstaller;
        _patreon = patreon;
        _logger = logger;
        _navigateBack = navigateBack;
        _showInfoDialog = showInfoDialog;
        _confirmDialog = confirmDialog;
        _runWithProgress = runWithProgress;
        _createUninstallScriptHost = createUninstallScriptHost;
        _showChangelog = showChangelog;
        _pickFile = pickFile;
        _pickFolder = pickFolder;
    }

    /// <summary>
    /// True once we have a real game install (detected, or just installed via the game-installer
    /// dependency). Gates the install-path line + Open Folder button — both hidden until the game
    /// itself is present.
    /// </summary>
    public bool IsGameInstalled => _gameInstall != null;

    /// <summary>The developer whose page the Developer button opens. Empty when there is none.</summary>
    [ObservableProperty]
    private string _developerName = string.Empty;

    /// <summary>Drives the Developer button's visibility — hidden when the owner is unknown.</summary>
    public bool CanOpenDeveloper => _ownerPlugin != null;

    /// <summary>
    /// Jumps to the developer's page: their other mods, their bio and their links. The page
    /// REPLACES this one rather than stacking on top, so Back from there returns to the mods list.
    /// </summary>
    [RelayCommand]
    private void OpenDeveloper()
    {
        if (_ownerPlugin is { } owner) _navigateToDeveloper?.Invoke(owner);
    }

    private void SetOwner(PluginEntry? owner, string pluginId)
    {
        _ownerPlugin = owner;
        DeveloperName = DeveloperNames.Resolve(
            _activeIndexes.GetValueOrDefault(pluginId), owner, pluginId);
        OnPropertyChanged(nameof(CanOpenDeveloper));
    }

    public void Load(
        GameInstall gameInstall,
        Dictionary<string, PluginRepoIndex> activeIndexes,
        PluginEntry? ownerPlugin = null)
    {
        _gameInstall = gameInstall;
        _gameDef = gameInstall.Game;
        _pluginId = gameInstall.PluginId;
        _activeIndexes = activeIndexes;
        GameId = gameInstall.Game.GameId;
        DisplayName = gameInstall.Game.DisplayName;
        InstallPath = gameInstall.InstallPath;
        // Named off the OWNER the caller passed, not off gameInstall.PluginId — see _ownerPlugin.
        SetOwner(ownerPlugin, ownerPlugin?.Id ?? gameInstall.PluginId);
        OnPropertyChanged(nameof(IsGameInstalled));
        _ = InitializeAsync();
    }

    /// <summary>
    /// Opens details for a game that isn't installed yet but declares a game-installer dependency.
    /// The version picker + Install still show; the game itself is installed as the first step of
    /// the install (see <see cref="EnsureGameInstalledAsync"/>).
    /// </summary>
    public void LoadUninstalled(
        GameDefinition game,
        string pluginId,
        Dictionary<string, PluginRepoIndex> activeIndexes,
        PluginEntry? ownerPlugin = null)
    {
        _gameInstall = null;
        _gameDef = game;
        _pluginId = pluginId;
        _activeIndexes = activeIndexes;
        GameId = game.GameId;
        DisplayName = game.DisplayName;
        InstallPath = null;
        // No GameInstall here, so pluginId IS the row's own developer — but the owner entry still
        // comes from the caller, because only it holds the registry entry.
        SetOwner(ownerPlugin, pluginId);
        OnPropertyChanged(nameof(IsGameInstalled));
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();
            var desired = config.DefaultChannel;

            // If the user's default channel has no visible releases for this game but other
            // channels do, switch to one that has releases. Without this, opening a game
            // whose only available build is on beta (e.g. an early-access Patreon release)
            // shows an empty list with no obvious way out — the channel selector is also
            // hidden when only one channel exists.
            var availableChannels = _activeIndexes.Values
                .SelectMany(idx => idx.ReleasesByGameId.TryGetValue(GameId, out var rels)
                    ? rels
                    : Enumerable.Empty<ModRelease>())
                .Where(IsReleaseVisibleToUser)
                .Select(r => r.Channel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (availableChannels.Count > 0 &&
                !availableChannels.Contains(desired, StringComparer.OrdinalIgnoreCase))
            {
                desired = availableChannels[0];
            }

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

                var gameDef = index.Games.FirstOrDefault(g => g.GameId == GameId);

                // Q3=A: hide releases the user can't access. Each release carries its own
                // Patreon block (Q6=C — channel-default schema is gone) so the gate decision
                // is purely per-release.
                var filtered = releases
                    .Where(r => r.Channel == SelectedChannel)
                    .Where(r => IsReleaseVisibleToUser(r))
                    .OrderByDescending(r => r.Version, VersionComparer.Instance)
                    .ToList();

                if (filtered.Count == 0) continue;

                var pluginReceipt = await _receiptStore.LoadAsync(GameId, pluginId);

                ModGroups.Add(new ModReleaseGroup
                {
                    PluginId = pluginId,
                    // Resolved per group, not from DeveloperName: a page can hold more than one
                    // developer's mods, and each card must name its own.
                    DeveloperName = DeveloperNames.Resolve(
                        index,
                        pluginId == _ownerPlugin?.Id ? _ownerPlugin : null,
                        pluginId),
                    Releases = filtered,
                    SelectedRelease = filtered.First(),
                    InstalledVersion = pluginReceipt?.InstalledVersion,
                    ExplicitModName = gameDef?.ModName,
                    Description = gameDef?.Description
                });
            }

            // Check dependencies defined on the game
            if (_gameInstall != null && _gameInstall.Game.Dependencies.Count > 0)
            {
                var depStatuses = await _dependencyChecker.CheckAsync(_gameInstall);
                foreach (var ds in depStatuses)
                {
                    Dependencies.Add(new DependencyItemViewModel(ds, _dependencyChecker, _logger,
                        msg => StatusMessage = msg));
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
            StatusMessage = "Couldn't load the versions for this mod. " +
                            CatalogRefusedException.SpeakableReason(ex);
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

    /// <summary>
    /// Ensures the game itself is installed before the mod install proceeds. Used when Game
    /// Details was opened from the "not installed" state (the game declares a game-installer
    /// dependency). After running the installer it re-detects via the registry; if the game still
    /// isn't found (a bootstrapper that keeps downloading after it exits) it waits for the user to
    /// confirm it's done, then re-detects. Returns true once we have a real install, false to
    /// abort. See GAME_INSTALLER_QUESTIONS.md.
    /// </summary>
    private async Task<bool> EnsureGameInstalledAsync(CancellationToken ct)
    {
        if (_gameInstall != null) return true;
        if (_gameDef == null) return false;

        // It may have been installed since this page opened.
        var existing = _registryDetector.ResolveInstallPath(_gameDef);
        if (existing != null) { AdoptDetectedGame(existing); return true; }

        var dep = _gameDef.Dependencies.FirstOrDefault(d => d.IsGameInstaller);

        // Portable app / emulator: extract a ZIP into a folder the user picks (or reuse an existing
        // install of the same emulator). See EMULATOR_INSTALL_QUESTIONS.md.
        if (dep?.Fix?.AutoInstall is ExtractAppAutoInstall)
            return await EnsureEmulatorInstalledAsync(dep, ct);

        if (dep?.Fix?.AutoInstall is not RunInstallerAutoInstall)
        {
            _showInfoDialog($"{DisplayName} isn't installed",
                $"{DisplayName} isn't installed and no game installer is configured for it. " +
                "Install the game, then use \"Browse for Folder\" on the Mods list to point at it.");
            return false;
        }

        var consent = _confirmDialog(
            $"Install {DisplayName}",
            $"{DisplayName} isn't installed yet. The manager will download its official installer " +
            "(verified by SHA256) and run it. Follow the installer's prompts; when it finishes, the " +
            "mod install continues automatically.\n\nContinue?");
        if (!consent)
        {
            StatusMessage = $"{DisplayName} needs to be installed before the mod can be added.";
            return false;
        }

        try
        {
            // One shared progress object: the dialog subscribes to it, the download reports into
            // it — the game-installer download shows a real percentage (finding 43).
            var progress = new Progress<ProgressInfo>();
            await _runWithProgress(
                "Install",
                $"Installing {DisplayName}...",
                progress,
                (scriptHost, depHost, innerCt) => _depAutoInstaller.RunGameInstallerAsync(dep, depHost, innerCt, progress),
                ct);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{DisplayName} install cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Game installer failed for {GameId}", GameId);
            _showInfoDialog($"{DisplayName} install failed", ex.Message);
            return false;
        }

        // 3A: re-detect once the installer process has closed. PTC fully installs before exit, so
        // this usually finds it right away; the retry absorbs a brief settle, and the gate below
        // covers download-after-exit (bootstrapper) installers.
        var path = await ResolveWithRetryAsync(_gameDef, ct);
        if (path == null)
        {
            var done = _confirmDialog(
                $"Finish installing {DisplayName}",
                $"Click OK once {DisplayName} has finished installing and the manager will continue.");
            if (!done)
            {
                StatusMessage = $"{DisplayName} install not finished.";
                return false;
            }
            path = await ResolveWithRetryAsync(_gameDef, ct);
        }

        if (path == null)
        {
            _showInfoDialog($"Couldn't find {DisplayName}",
                $"The manager couldn't find {DisplayName} after the installer ran. If you did install it, " +
                "use \"Browse for Folder\" on the Mods list to point at it.");
            return false;
        }

        AdoptDetectedGame(path);
        return true;
    }

    private void AdoptDetectedGame(string installPath)
    {
        _gameInstall = new GameInstall
        {
            Game = _gameDef!,
            PluginId = _pluginId,
            InstallPath = installPath,
            IsValid = true
        };
        InstallPath = installPath;
        OnPropertyChanged(nameof(IsGameInstalled));
        _logger.Information("Game {GameId} present at {Path}; continuing with the mod install", GameId, installPath);
    }

    /// <summary>
    /// Portable-app (emulator) game installer. If this emulator is already installed — matched by
    /// the game's <see cref="GameDefinition.ExeName"/> against emulators the manager installed
    /// before (F1-B / F3) — reuse that folder with no download and no prompt (F2). Otherwise ask
    /// the user where to put it (2B), download + verify + extract the ZIP there, locate the folder
    /// that actually holds the exe (F4), and record it as the install path (and remember the
    /// emulator so a later game on the same one reuses it). Returns true to continue into the mod
    /// install, false to abort. See EMULATOR_INSTALL_QUESTIONS.md.
    /// </summary>
    private async Task<bool> EnsureEmulatorInstalledAsync(Dependency dep, CancellationToken ct)
    {
        var exeName = _gameDef!.ExeName;

        // 1) Reuse an emulator the manager already installed. Only our own installs are recorded
        //    (Browse-for-Folder deliberately isn't), so this never grabs an unrelated copy.
        if (!string.IsNullOrWhiteSpace(exeName))
        {
            var config = await _configService.LoadAsync();
            if (config.InstalledEmulators.TryGetValue(exeName.ToLowerInvariant(), out var known))
            {
                var reuseRoot = PortableAppLayout.ResolveInstallRoot(known, exeName);
                if (reuseRoot != null)
                {
                    await PersistEmulatorPathsAsync(reuseRoot, exeName);
                    AdoptDetectedGame(reuseRoot);
                    _logger.Information("Reusing existing {Exe} install at {Path} for {GameId}",
                        exeName, reuseRoot, GameId);
                    return true;
                }
                _logger.Information("Recorded {Exe} install is gone; installing {GameId} fresh", exeName, GameId);
            }
        }

        // 2) Fresh install: consent → pick a folder → download + extract.
        var consent = _confirmDialog(
            $"Install {DisplayName}",
            $"{DisplayName} isn't installed yet. Choose a folder and the manager will download it " +
            "(verified by SHA256), extract it there, and then install the accessibility mod.\n\nContinue?");
        if (!consent)
        {
            StatusMessage = $"{DisplayName} needs to be installed before the mod can be added.";
            return false;
        }

        var picked = _pickFolder($"Select where to install {DisplayName}");
        if (string.IsNullOrEmpty(picked))
        {
            StatusMessage = $"{DisplayName} install cancelled.";
            return false;
        }

        try
        {
            // Shared progress object, same as the run-installer path: the emulator download
            // reports a real percentage into the dialog (finding 43).
            var progress = new Progress<ProgressInfo>();
            await _runWithProgress(
                "Install",
                $"Installing {DisplayName}...",
                progress,
                (_, depHost, innerCt) => _depAutoInstaller.ExtractPortableAppAsync(dep, picked, depHost, innerCt, progress),
                ct);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{DisplayName} install cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Portable app install failed for {GameId}", GameId);
            _showInfoDialog($"{DisplayName} install failed", ex.Message);
            return false;
        }

        // 3) Find the folder that actually holds the exe (top level per F4, else a single
        //    sub-folder if the ZIP wraps everything in one), then record + adopt.
        var root = PortableAppLayout.ResolveInstallRoot(picked, exeName);
        if (root == null)
        {
            _showInfoDialog($"Couldn't find {DisplayName}",
                $"The download was extracted to \"{picked}\", but \"{exeName}\" wasn't found inside it. " +
                $"The file may not be the expected {DisplayName} package.");
            return false;
        }

        await PersistEmulatorPathsAsync(root, exeName);
        AdoptDetectedGame(root);
        return true;
    }

    /// <summary>
    /// Persist the emulator's location so detection finds it next launch (KnownGameOverrides) and
    /// so a later game on the same emulator can reuse it (InstalledEmulators, keyed by exe name).
    /// Best-effort: the install still proceeds in-memory if the write fails; detection just won't
    /// remember it next launch.
    /// </summary>
    private async Task PersistEmulatorPathsAsync(string root, string? exeName)
    {
        try
        {
            await _configService.UpdateAsync(config =>
            {
                config.KnownGameOverrides[GameId] = root;
                if (!string.IsNullOrWhiteSpace(exeName))
                    config.InstalledEmulators[exeName.ToLowerInvariant()] = root;
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Installed {GameId} at {Path} but couldn't persist the override", GameId, root);
        }
    }

    /// <summary>
    /// Registry detection, retried a few times with a short delay — a game installer's registry
    /// key + files can take a moment to settle after the installer process exits.
    /// </summary>
    private async Task<string?> ResolveWithRetryAsync(GameDefinition game, CancellationToken ct)
    {
        const int attempts = 6;
        for (var i = 0; i < attempts; i++)
        {
            var p = _registryDetector.ResolveInstallPath(game);
            if (p != null) return p;
            if (i < attempts - 1) await Task.Delay(400, ct);
        }
        return null;
    }

    /// <summary>
    /// <see cref="IGameVerifier.VerifyInstallPath"/>, retried a few times with a short delay —
    /// tolerant of a just-created junction or a game installer whose files are still settling.
    /// Removes the "restart the manager and try again" workaround users reported.
    /// </summary>
    private async Task<bool> VerifyWithRetryAsync(GameDefinition game, string path, CancellationToken ct)
    {
        const int attempts = 6;
        for (var i = 0; i < attempts; i++)
        {
            if (_gameVerifier.VerifyInstallPath(game, path)) return true;
            if (i < attempts - 1) await Task.Delay(400, ct);
        }
        return false;
    }

    /// <summary>
    /// For a game with an <see cref="AsciiPathShim"/>, ensure the ASCII junction exists and the
    /// manager is pointed at it before installing. The junction is created on first install,
    /// with the user's consent, then adopted as the install path (persisted as a known-game
    /// override + swapped into the in-memory install) so install actions, verification, and the
    /// Play button all run through the ASCII path while the real files stay put. Returns true to
    /// proceed (a game with no shim also returns true), false to abort. See PTCGL_INSTALL_QUESTIONS.md.
    /// </summary>
    private async Task<bool> EnsureInstallPathReadyAsync(CancellationToken ct)
    {
        if (_gameInstall is null) return false;

        var shim = _gameInstall.Game.AsciiPathShim;
        if (shim is null) return true; // game doesn't use a path shim — nothing to do

        var current = _gameInstall.InstallPath;
        string junctionPath;
        try
        {
            junctionPath = _shimService.GetJunctionPath(shim, current);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Couldn't compute junction path for {GameId}", GameId);
            _showInfoDialog("Setup failed", $"Couldn't work out where to create the folder link.\n\n{ex.Message}");
            return false;
        }

        // Already set up: detection resolved us straight to the junction (the override points
        // here), so there's nothing to create or adopt.
        if (PathsEqual(current, junctionPath))
            return true;

        // 'current' is the real (problematic) path. Create the junction the first time, with a
        // one-time consent prompt. If it already exists we silently reuse it.
        if (!_shimService.JunctionPathExists(junctionPath))
        {
            var consent = _confirmDialog(
                $"Set up {DisplayName}",
                $"{shim.Reason}\n\n" +
                $"The manager will create a folder link at \"{junctionPath}\" pointing to your existing " +
                $"install at \"{current}\". Your game files are not moved or copied — this just gives the " +
                $"mod loader a compatible path. It's a one-time step.\n\nContinue?");
            if (!consent)
            {
                StatusMessage = $"{DisplayName} needs a one-time setup before the mod can be installed.";
                return false;
            }

            try
            {
                await _shimService.CreateJunctionAsync(junctionPath, current, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to create ASCII junction {Junction} -> {Real}", junctionPath, current);
                _showInfoDialog("Setup failed",
                    $"Couldn't create the folder link at \"{junctionPath}\".\n\n{ex.Message}");
                return false;
            }
        }
        else
        {
            // The junction already exists. If it points somewhere other than the install we just
            // detected — a stale link left from a previous install location — re-point it so we
            // never install mods into the wrong/old copy while telling the user we're operating on
            // the detected game. Removing the link is a non-recursive delete of the reparse point
            // only; neither location's files are touched.
            var existingTarget = _shimService.GetJunctionTarget(junctionPath);
            if (existingTarget != null && !PathsEqual(existingTarget, current))
            {
                _logger.Warning("ASCII junction {Junction} points at {Old}, not the detected install {New}; re-pointing",
                    junctionPath, existingTarget, current);
                try
                {
                    _shimService.RemoveJunctionLink(junctionPath);
                    await _shimService.CreateJunctionAsync(junctionPath, current, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to re-point stale junction {Junction}", junctionPath);
                    _showInfoDialog("Setup failed",
                        $"The folder link at \"{junctionPath}\" points at a different location and couldn't be updated.\n\n{ex.Message}");
                    return false;
                }
            }
        }

        // Make sure the game is really present before we commit to the junction. The install we
        // actually care about is the real path; the junction is just an ASCII-safe alias to it.
        // Verify the REAL path (retried briefly for a game whose files are still settling right
        // after a fresh install) and confirm the junction points there via its reparse target.
        // Both read stable metadata, so neither depends on the freshly-created junction being
        // walkable *through* yet — that dependency was the spurious "doesn't look like a valid
        // install" users had to clear by restarting the manager. As a fallback (e.g. the link
        // target couldn't be read), verify straight through the junction with the same retry.
        var realValidAndLinked =
            await VerifyWithRetryAsync(_gameInstall.Game, current, ct)
            && PathsEqual(_shimService.GetJunctionTarget(junctionPath) ?? "", current);

        if (!realValidAndLinked && !await VerifyWithRetryAsync(_gameInstall.Game, junctionPath, ct))
        {
            _showInfoDialog("Setup failed",
                $"The folder link at \"{junctionPath}\" doesn't look like a valid {DisplayName} install, " +
                $"so setup wasn't completed. Try installing again in a moment.");
            return false;
        }

        // Adopt the junction: persist it as the override so future detection finds the game here,
        // and swap the in-memory install so this install + the Play button use the ASCII path.
        try
        {
            await _configService.UpdateAsync(config => config.KnownGameOverrides[GameId] = junctionPath);
        }
        catch (Exception ex)
        {
            // Non-fatal: the junction exists and we still swap in-memory below, so this install
            // proceeds correctly; detection just won't remember the junction next launch.
            _logger.Warning(ex, "Junction created but couldn't persist the override for {GameId}", GameId);
        }

        _gameInstall = new GameInstall
        {
            Game = _gameInstall.Game,
            PluginId = _gameInstall.PluginId,
            InstallPath = junctionPath,
            IsValid = true,
            DetectedVersion = _gameInstall.DetectedVersion,
            ModState = _gameInstall.ModState
        };
        InstallPath = junctionPath;
        _logger.Information("Adopted ASCII junction {Junction} as install path for {GameId}", junctionPath, GameId);
        return true;
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    private Task InstallAsync(ModReleaseGroup? group, CancellationToken ct) =>
        RunInstallOrUpdate(group, ct, isUpdate: false);

    [RelayCommand]
    private Task UpdateAsync(ModReleaseGroup? group, CancellationToken ct) =>
        RunInstallOrUpdate(group, ct, isUpdate: true);

    private async Task RunInstallOrUpdate(ModReleaseGroup? group, CancellationToken ct, bool isUpdate)
    {
        if (group?.SelectedRelease == null) return;
        var release = group.SelectedRelease;
        // A selected release older than the installed one is a DOWNGRADE, and every string in the
        // flow says so (finding 43) — the button quietly labelled "Update" while rolling backwards
        // was a long-standing trap, worst over a screen reader. Downgrading stays possible on
        // purpose: it's the rescue path when a new release breaks.
        var isDowngrade = isUpdate && group.InstalledVersion != null &&
            VersionComparer.Instance.Compare(release.Version, group.InstalledVersion) < 0;
        var verb = isDowngrade ? "Downgrade" : isUpdate ? "Update" : "Install";
        var verbing = isDowngrade ? "Downgrading" : isUpdate ? "Updating" : "Installing";
        var verbed = isDowngrade ? "Downgraded" : isUpdate ? "Updated" : "Installed";

        // If the game itself isn't installed yet — this page was opened from the not-detected
        // state via a game-installer dependency — install the game first, then detect where it
        // landed. EnsureGameInstalledAsync sets _gameInstall on success.
        if (_gameInstall == null)
        {
            if (!await EnsureGameInstalledAsync(ct))
                return;
        }

        // For a game with an ASCII path shim (e.g. Pokémon TCG Live), make sure the junction is
        // created and adopted as the install path before anything else — including before any
        // Patreon browser tab opens — so a declined setup aborts cleanly with nothing done.
        if (!await EnsureInstallPathReadyAsync(ct))
            return;

        // Patreon-gated pre-flight. The path divides three ways (the old "auto-download from
        // Patreon's CDN" leg was removed in the round-2 audit — Amethyst doesn't use Patreon's
        // site directly for files any more, and it attached the bearer token to API-supplied
        // URLs without a host check):
        // 1. Creator of the campaign — API refuses to return a download URL for own posts;
        //    file picker (creator's own local copy).
        // 2. Patron + author has a download server (Patreon.ServerUrl set) — manager streams
        //    from the author's server with the patron's bearer token; the server validates
        //    entitlement against Patreon API and serves the file. The clean happy path.
        // 3. Patron + no server URL — manual path: open the post in their browser, ask them
        //    to grab the file, then pick it (SHA256 still gates correctness).
        string? localFilePath = null;
        var useAuthorServer = false;
        if (release.Patreon != null)
        {
            var isCreator = _patreon.IsCampaignOwner(release.Patreon.CampaignId);
            var needFilePicker = false;

            if (isCreator)
            {
                needFilePicker = true;
            }
            else
            {
                // Q4=A: recheck entitlements every install attempt before doing anything.
                await _patreon.RefreshEntitlementsAsync(ct);
                if (!_patreon.IsEntitled(release.Patreon))
                {
                    StatusMessage =
                        "You're no longer entitled to this Patreon-gated release. " +
                        "Sign in again or check your Patreon membership.";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(release.Patreon.ServerUrl))
                {
                    // Author hosts the file themselves; manager will stream from there with
                    // the patron's bearer token. The author's server does its own entitlement
                    // check via Patreon's API. Whitespace counts as absent — the fetch-time
                    // validation treats it that way, and routing must agree.
                    useAuthorServer = true;
                }
                else
                {
                    // No author server: the manual browser-download path is the only one left.
                    needFilePicker = true;
                }
            }

            if (needFilePicker)
            {
                if (!isCreator)
                {
                    // Open the post in the patron's browser so they can grab the file from
                    // Patreon's web UI — and be HONEST about whether that worked. Telling a
                    // blind user "we've opened the post" when the browser never launched leaves
                    // them stranded in a file picker with nothing to pick.
                    var postUrl = $"https://www.patreon.com/posts/{release.Patreon.PostId}";
                    var browserOpened = ExternalLink.TryOpen(postUrl, _logger);

                    var fileName = release.Patreon.AttachmentFileName ?? "the wrapped ZIP";
                    _showInfoDialog(
                        "Manual download needed",
                        browserOpened
                            ? $"Patreon doesn't hand out direct download links for tier-locked posts.\n\n" +
                              $"We've opened the post in your browser. Download '{fileName}' from there, then pick it in the next dialog. " +
                              $"The manager verifies the SHA256 to make sure it's the right file before installing."
                            : $"Patreon doesn't hand out direct download links for tier-locked posts, and the post " +
                              $"couldn't be opened in your browser automatically.\n\n" +
                              $"Open this address yourself: {postUrl}\n\n" +
                              $"Download '{fileName}' from there, then pick it in the next dialog. " +
                              $"The manager verifies the SHA256 to make sure it's the right file before installing.");
                }

                var suggested = release.Patreon.AttachmentFileName ?? $"{release.PluginId}-{release.Version}.zip";
                var pickerTitle = isCreator
                    ? $"Pick your local copy of {DisplayName} v{release.Version}"
                    : $"Pick the file you downloaded from Patreon for {DisplayName} v{release.Version}";
                localFilePath = _pickFile(
                    pickerTitle,
                    "Wrapped ZIP (*.zip)|*.zip|All files (*.*)|*.*",
                    suggested);
                if (string.IsNullOrEmpty(localFilePath))
                {
                    StatusMessage = $"{verb} cancelled.";
                    return;
                }
            }
        }

        StatusMessage = $"{verbing}...";
        var progress = new Progress<ProgressInfo>();

        var tempZip = Path.Combine(Path.GetTempPath(), "AccessibilityModManager",
            $"download_{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(tempZip)!);

        var hashFailed = false;
        var downloadedFile = string.Empty;

        // Both ensure-steps above guarantee a non-null install (the game exists and, for a shimmed
        // game, the junction has been adopted). Capture it so the progress closure is null-safe.
        var gameInstall = _gameInstall!;

        try
        {
            await _runWithProgress(verb,
                $"{verbing} {DisplayName} v{release.Version}...",
                progress,
                async (scriptHost, depHost, innerCt) =>
                {
                    if (localFilePath != null)
                    {
                        // User-picked-file path: skip downloading, use what they pointed at.
                        // Covers both the creator and the entitled-but-API-broken patron
                        // cases. SHA256 still gates correctness — a wrong file fails the
                        // hash check, same as a corrupted download would.
                        downloadedFile = localFilePath;
                    }
                    else if (useAuthorServer && release.Patreon != null)
                    {
                        // Author-hosted server path: stream from the author's download
                        // server with the patron's Patreon bearer token. The server
                        // validates the token and entitlement before streaming the file.
                        await _patreon.DownloadFromServerAsync(
                            release.Patreon.ServerUrl!, tempZip, progress, innerCt);
                        downloadedFile = tempZip;
                    }
                    else
                    {
                        // Every gated case was resolved above (creator picker, author server,
                        // or manual pick) — reaching here means a plain public release.
                        downloadedFile = await _repoClient.DownloadPackageAsync(
                            release.PackageUrl!, tempZip, progress, innerCt);
                    }

                    if (!await _repoClient.VerifySha256Async(downloadedFile, release.Sha256, innerCt))
                    {
                        hashFailed = true;
                        return;
                    }

                    if (isUpdate)
                        await _installerEngine.UpdateAsync(gameInstall, release, downloadedFile, scriptHost, depHost, innerCt);
                    else
                        await _installerEngine.InstallAsync(gameInstall, release, downloadedFile, scriptHost, depHost, innerCt);
                },
                ct);

            if (hashFailed)
            {
                StatusMessage = $"Download failed SHA256 verification. {verb} aborted.";
                return;
            }

            group.InstalledVersion = release.Version;
            OnPropertyChanged(nameof(AnyModInstalled));
            // The install may have auto-installed dependencies — re-check so the Dependencies
            // section doesn't keep announcing them as missing (finding 34).
            await TryRefreshDependencyListAsync(ct);
            StatusMessage = null;
            _showInfoDialog(
                $"{verb} completed",
                $"{verbed} {group.ModName}");
            OperationCompleted?.Invoke();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{verb} cancelled.";
        }
        catch (MissingRequiredDependencyException ex)
        {
            _logger.Warning("{Verb} blocked by missing dependencies for {PluginId}/{GameId}", verb, release.PluginId, GameId);
            // Some dependencies may have installed before the blocker — refresh the list first,
            // then write the guidance line so it stays the message the user lands on.
            await TryRefreshDependencyListAsync(ct);
            var names = string.Join(", ", ex.Blockers.Select(b => b.Dependency.Id));
            StatusMessage = $"Cannot {verb.ToLowerInvariant()} — install required dependencies first: {names}. " +
                            "Open the Dependencies section below and use Fix.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Verb} failed for {PluginId}/{GameId}", verb, release.PluginId, GameId);
            StatusMessage = $"{verb} failed. Check the log for details.";
        }
        finally
        {
            // Only delete files we downloaded into the temp folder. If the user picked a
            // local file (creator self-install or manual Patreon download), that's their
            // copy — leave it alone.
            if (!string.IsNullOrEmpty(downloadedFile) && localFilePath == null)
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
            // Cached post-uninstall scripts can be present from a previous install — give the
            // engine an IScriptHost so it can re-confirm and stream output. The host here only
            // owns the modal warning; uninstall doesn't currently route through ProgressDialog.
            var scriptHost = _createUninstallScriptHost();
            await _installerEngine.UninstallAsync(_gameInstall, group.PluginId, scriptHost, ct);
            group.InstalledVersion = null;
            OnPropertyChanged(nameof(AnyModInstalled));
            // Uninstall can release dependencies (refcount to zero removes a loader) — re-check
            // so the list reflects it (finding 34).
            await TryRefreshDependencyListAsync(ct);
            StatusMessage = null;
            _showInfoDialog("Uninstall completed", $"Uninstalled {group.ModName}");
            OperationCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Uninstall failed for {PluginId}/{GameId}", group.PluginId, GameId);
            StatusMessage = "Uninstall failed. Check the log for details.";
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
            StatusMessage = "Couldn't open the game folder. Check the log for details.";
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
            StatusMessage = "Couldn't launch the game. Check the log for details.";
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
            var missing = await RefreshDependencyListAsync(ct);
            StatusMessage = missing == 0
                ? "All required dependencies are installed."
                : $"{missing} required dependency still missing. Install before retrying.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to recheck dependencies for {GameId}", GameId);
            StatusMessage = "Couldn't re-check what this mod needs. Check the log for details.";
        }
    }

    /// <summary>
    /// Re-runs the dependency check and rebuilds the list, writing NO status message — used both
    /// by the explicit Recheck command and after install/update/uninstall (finding 34: the list
    /// went stale right after the flow that changes it, which reads as wrong over a screen
    /// reader). Returns how many required dependencies are still not installed.
    /// </summary>
    private async Task<int> RefreshDependencyListAsync(CancellationToken ct)
    {
        // Build first, swap after: clearing up front would leave an EMPTY list behind if the
        // check throws mid-way — worse than the stale list this method exists to fix.
        var items = new List<DependencyItemViewModel>();
        if (_gameInstall is { Game.Dependencies.Count: > 0 })
        {
            var depStatuses = await _dependencyChecker.CheckAsync(_gameInstall, ct);
            foreach (var ds in depStatuses)
            {
                items.Add(new DependencyItemViewModel(ds, _dependencyChecker, _logger,
                    msg => StatusMessage = msg));
            }
        }

        Dependencies.Clear();
        foreach (var item in items)
            Dependencies.Add(item);

        return items.Count(d =>
            d.IsRequired && d.StatusKind != DependencyStatusKind.Installed);
    }

    /// <summary>
    /// Best-effort dependency-list refresh for the end of a mutation flow: the mutation already
    /// succeeded (or already reported its own error), so a hiccup here only logs — it must never
    /// repaint a completed install as a failure.
    /// </summary>
    private async Task TryRefreshDependencyListAsync(CancellationToken ct)
    {
        try
        {
            await RefreshDependencyListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't refresh the dependency list after the operation for {GameId}", GameId);
        }
    }

    [RelayCommand]
    private void GoBack() => _navigateBack();

    [RelayCommand]
    private void ViewChangelog(ModReleaseGroup? group)
    {
        if (group?.SelectedRelease is not { } release) return;
        _showChangelog(group.ModName, release.Version, release.Notes, release.ChangelogUrl);
    }

    /// <summary>
    /// True when a release should be visible to the current user. Public releases are
    /// always visible; Patreon-gated releases are visible when the user is entitled to one
    /// of the gate's tiers (Q3=A) OR when they own the campaign — creators need to see
    /// their own gated releases so they can install via the local-file path (Patreon's API
    /// returns no download URL for creators viewing their own paid posts).
    /// </summary>
    private bool IsReleaseVisibleToUser(ModRelease release)
    {
        if (release.Patreon == null) return true;
        if (_patreon.IsCampaignOwner(release.Patreon.CampaignId)) return true;
        return _patreon.IsEntitled(release.Patreon);
    }
}

public partial class ModReleaseGroup : ObservableObject
{
    public required string PluginId { get; init; }

    /// <summary>
    /// Who made this mod, for the "by ..." line. The card used to show <see cref="PluginId"/>
    /// there, which is a slug like <c>digimon-tools</c> rather than a person's name.
    /// </summary>
    public required string DeveloperName { get; init; }

    public required List<ModRelease> Releases { get; init; }

    /// <summary>
    /// Author-supplied mod name from <c>GameDefinition.ModName</c>. Preferred over the
    /// URL-derived fallback when present.
    /// </summary>
    public string? ExplicitModName { get; init; }

    /// <summary>
    /// Author-supplied mod description from <c>GameDefinition.Description</c>. Shown to users
    /// on the game details view so they know what the mod actually does before installing.
    /// </summary>
    public string? Description { get; init; }

    [ObservableProperty]
    private ModRelease? _selectedRelease;

    [ObservableProperty]
    private string? _installedVersion;

    public bool IsInstalled => InstalledVersion != null;

    /// <summary>Semantic comparison, not string inequality: "1.0" and "1.0.0" are the same
    /// version and must not offer an Update/Downgrade that goes nowhere.</summary>
    public bool CanUpdate => IsInstalled && SelectedRelease != null &&
        VersionComparer.Instance.Compare(SelectedRelease.Version, InstalledVersion) != 0;

    /// <summary>
    /// True when the selected release is OLDER than the installed one. The action button then
    /// says "Downgrade" — visibly and to the screen reader — instead of quietly rolling back
    /// under an "Update" label (finding 43). Downgrading itself stays allowed: it's the rescue
    /// path when a new release breaks.
    /// </summary>
    public bool IsDowngrade => IsInstalled && SelectedRelease != null &&
        VersionComparer.Instance.Compare(SelectedRelease.Version, InstalledVersion) < 0;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasChangelog =>
        !string.IsNullOrWhiteSpace(SelectedRelease?.Notes) ||
        SelectedRelease?.ChangelogUrl is { Length: > 0 };

    /// <summary>
    /// Human name for the mod. Prefers the explicit <c>GameDefinition.ModName</c> from the
    /// plugin's index, falling back to the GitHub repo name parsed from the package URL.
    /// </summary>
    public string ModName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ExplicitModName)) return ExplicitModName!;

            // PackageUrl is null on Patreon-gated releases — fall back to the URL-less
            // default ("mod") in that case, since the Patreon post URL isn't a stable
            // place to derive a mod name from.
            var url = Releases.Select(r => r.PackageUrl).FirstOrDefault(u => u is not null);
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
    /// Bound by AutomationProperties.Name in GameDetailsView.xaml. Description is exposed as
    /// its own focusable navigable text element below, not folded in here, so the user can
    /// arrow through it line-by-line in NVDA's focus mode.
    /// </summary>
    public string AnnouncementText =>
        InstalledVersion is null
            ? $"{ModName} by {DeveloperName}, not installed"
            : $"{ModName} by {DeveloperName}, version {InstalledVersion} installed";

    public string InstallButtonName => $"Install {ModName}";

    /// <summary>Visible label of the update/downgrade button.</summary>
    public string UpdateButtonLabel => IsDowngrade ? "Downgrade" : "Update";
    public string UpdateButtonName => $"{UpdateButtonLabel} {ModName}";
    public string UninstallButtonName => $"Uninstall {ModName}";

    partial void OnInstalledVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(IsDowngrade));
        OnPropertyChanged(nameof(UpdateButtonLabel));
        OnPropertyChanged(nameof(UpdateButtonName));
        OnPropertyChanged(nameof(AnnouncementText));
    }

    partial void OnSelectedReleaseChanged(ModRelease? value)
    {
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(IsDowngrade));
        OnPropertyChanged(nameof(UpdateButtonLabel));
        OnPropertyChanged(nameof(UpdateButtonName));
        OnPropertyChanged(nameof(HasChangelog));
    }

    public override string ToString() => AnnouncementText;
}

public partial class DependencyItemViewModel : ObservableObject
{
    private readonly IDependencyChecker _checker;
    private readonly ILogger _logger;
    private readonly Action<string>? _reportStatus;

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

    public DependencyItemViewModel(
        DependencyStatus status, IDependencyChecker checker, ILogger logger,
        Action<string>? reportStatus = null)
    {
        Status = status;
        _checker = checker;
        _logger = logger;
        _reportStatus = reportStatus;
    }

    public override string ToString() => AnnouncementText;

    [RelayCommand]
    private async Task FixAsync(CancellationToken ct)
    {
        try
        {
            var opened = await _checker.FixAsync(Status.Dependency, ct);
            _reportStatus?.Invoke(opened
                ? $"Opened the download page for {Name} in your browser."
                : $"Couldn't open a download page for {Name} — it may have no safe https link, or no browser responded. " +
                  "Check the mod's page for install instructions.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fix dependency {DepId}", Name);
            _reportStatus?.Invoke($"Couldn't open the download page for {Name}. Check the log for details.");
        }
    }
}
