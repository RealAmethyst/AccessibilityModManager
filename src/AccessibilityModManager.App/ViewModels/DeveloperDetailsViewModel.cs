using System.Collections.ObjectModel;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.App.ViewModels;

/// <summary>
/// Filtered view of a single developer's mods. Loads the developer's plugin index, detects
/// each game on the user's machine, and lists one row per supported game. Pressing Enter on
/// a row hands off to the existing GameDetailsView for the install/update/uninstall flow,
/// so all action logic lives in one place.
/// </summary>
public partial class DeveloperDetailsViewModel : ObservableObject
{
    private readonly IPluginRepoClient _repoClient;
    private readonly IConfigService _configService;
    private readonly IReceiptStore _receiptStore;
    private readonly GameAggregator _gameAggregator;
    private readonly ILogger _logger;
    private readonly Action _navigateBack;
    private readonly Action<GameInstall, Dictionary<string, PluginRepoIndex>, string> _navigateToGameDetails;
    /// <summary>
    /// Opens Game Details for a not-detected game that can install itself (declares a
    /// game-installer dependency). Args: game definition, owning plugin id, scoped indexes.
    /// </summary>
    private readonly Action<GameDefinition, string, Dictionary<string, PluginRepoIndex>> _navigateToGameDetailsUninstalled;

    private readonly PluginEntry _plugin;

    // Cached after load so OpenMod can build the navigation payload.
    private PluginRepoIndex? _pluginIndex;
    private bool _indexFromCache;
    private DateTimeOffset? _indexCachedAtUtc;

    /// <summary>Set when the LIVE catalog was reached and refused, so this view is showing an older
    /// copy. Distinct from being offline, and it must be said rather than swallowed.</summary>
    private string? _indexRejectionReason;
    private List<GameInstall> _installs = [];

    public string PluginId => _plugin.Id;
    public string Author => _plugin.Author;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _displayName;

    [ObservableProperty]
    private string? _bio;

    [ObservableProperty]
    private string? _websiteUrl;

    [ObservableProperty]
    private string? _discordUrl;

    [ObservableProperty]
    private string? _patreonUrl;

    [ObservableProperty]
    private string? _gitHubUrl;

    [ObservableProperty]
    private string? _donationUrl;

    public bool HasBio => !string.IsNullOrWhiteSpace(Bio);
    public bool HasWebsite => !string.IsNullOrWhiteSpace(WebsiteUrl);
    public bool HasDiscord => !string.IsNullOrWhiteSpace(DiscordUrl);
    public bool HasPatreon => !string.IsNullOrWhiteSpace(PatreonUrl);
    public bool HasGitHub => !string.IsNullOrWhiteSpace(GitHubUrl);
    public bool HasDonation => !string.IsNullOrWhiteSpace(DonationUrl);

    /// <summary>
    /// True when the author hasn't set a bio or any social/donation links. Lets the view
    /// show a clear "no info published yet" line instead of empty space.
    /// </summary>
    public bool HasNoAuthorInfo =>
        !HasBio && !HasWebsite && !HasDiscord && !HasPatreon && !HasGitHub && !HasDonation;

    public ObservableCollection<DeveloperModItemViewModel> Mods { get; } = [];

    public DeveloperDetailsViewModel(
        PluginEntry plugin,
        IPluginRepoClient repoClient,
        IConfigService configService,
        IReceiptStore receiptStore,
        GameAggregator gameAggregator,
        ILogger logger,
        Action navigateBack,
        Action<GameInstall, Dictionary<string, PluginRepoIndex>, string> navigateToGameDetails,
        Action<GameDefinition, string, Dictionary<string, PluginRepoIndex>> navigateToGameDetailsUninstalled)
    {
        _plugin = plugin;
        _repoClient = repoClient;
        _configService = configService;
        _receiptStore = receiptStore;
        _gameAggregator = gameAggregator;
        _logger = logger;
        _navigateBack = navigateBack;
        _navigateToGameDetails = navigateToGameDetails;
        _navigateToGameDetailsUninstalled = navigateToGameDetailsUninstalled;

        // Seed DisplayName from the registry's PluginEntry so the header renders something
        // sensible before LoadAsync replaces it with the per-plugin index's author info.
        _displayName = _plugin.Author;

        _ = LoadAsync(refetchIndex: true);
    }

    /// <summary>
    /// Re-evaluate each mod's installed-state from receipts. Called by MainViewModel after a
    /// successful install/uninstall in the Game Details overlay so the user doesn't see stale
    /// "Not installed" rows when they navigate back. Skips the network round-trip — the index
    /// itself doesn't change as a result of a user-side install.
    /// </summary>
    public Task RefreshAsync() => LoadAsync(refetchIndex: false);

    private async Task LoadAsync(bool refetchIndex)
    {
        IsLoading = true;
        StatusMessage = "Loading mods...";

        try
        {
            if (refetchIndex || _pluginIndex == null)
            {
                var indexFetch = await _repoClient.FetchPluginIndexAsync(_plugin);
                _pluginIndex = indexFetch.Value;
                _indexFromCache = indexFetch.FromCache;
                _indexCachedAtUtc = indexFetch.CachedAtUtc;
                _indexRejectionReason = indexFetch.LiveRejectionReason;
            }

            // Surface author info from the index (preferred) or fall back to the registry's
            // PluginEntry. Either way the bio + social links light up the Authors view.
            DisplayName = _pluginIndex.Author?.DisplayName ?? _plugin.Author;
            Bio = _pluginIndex.Author?.Bio;
            WebsiteUrl = _pluginIndex.Author?.WebsiteUrl;
            DiscordUrl = _pluginIndex.Author?.DiscordUrl;
            PatreonUrl = _pluginIndex.Author?.PatreonUrl;
            GitHubUrl = _pluginIndex.Author?.GitHubUrl;
            DonationUrl = _pluginIndex.Author?.DonationUrl;
            OnPropertyChanged(nameof(HasBio));
            OnPropertyChanged(nameof(HasWebsite));
            OnPropertyChanged(nameof(HasDiscord));
            OnPropertyChanged(nameof(HasPatreon));
            OnPropertyChanged(nameof(HasGitHub));
            OnPropertyChanged(nameof(HasDonation));
            OnPropertyChanged(nameof(HasNoAuthorInfo));

            // Reuse GameAggregator with a single-entry dictionary so we get the same Steam
            // detection + manual-override behavior the Games tab uses, just scoped to this
            // developer's games.
            var indexes = new Dictionary<string, PluginRepoIndex> { [_plugin.Id] = _pluginIndex };
            var config = await _configService.LoadAsync();
            var detection = await _gameAggregator.DetectAllGamesAsync(
                indexes, config.KnownGameOverrides, config.InstalledEmulators);
            _installs = detection.Installs;

            // Same silent auto-heal persistence as the Mods tab (finding 32), with the same
            // fresh read-modify-write so a whole-document save can't clobber concurrent writes.
            if (detection.HealedOverrides.Count > 0)
            {
                try
                {
                    var latest = await _configService.LoadAsync();
                    foreach (var (gameId, path) in detection.HealedOverrides)
                        latest.KnownGameOverrides[gameId] = path;
                    await _configService.SaveAsync(latest);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Couldn't persist healed game overrides");
                }
            }

            Mods.Clear();
            foreach (var game in _pluginIndex.Games)
            {
                var install = _installs.FirstOrDefault(i => i.Game.GameId == game.GameId && i.IsValid);
                var receipt = await _receiptStore.LoadAsync(game.GameId, _plugin.Id);

                var latestVersion = _pluginIndex.ReleasesByGameId.TryGetValue(game.GameId, out var rels)
                    ? rels.Where(r => r.Channel == config.DefaultChannel)
                          .OrderByDescending(r => r.Version, VersionComparer.Instance)
                          .FirstOrDefault()?.Version
                    : null;

                var modName = !string.IsNullOrWhiteSpace(game.ModName)
                    ? game.ModName!
                    : DeriveModName(_pluginIndex, game.GameId);
                var hasUpdate = receipt != null && latestVersion != null &&
                                VersionComparer.Instance.Compare(latestVersion, receipt.InstalledVersion) > 0;

                Mods.Add(new DeveloperModItemViewModel
                {
                    GameId = game.GameId,
                    GameDisplayName = game.DisplayName,
                    ModName = modName,
                    PluginId = _plugin.Id,
                    IsDetected = install != null,
                    HasGameInstaller = game.Dependencies.Any(d => d.IsGameInstaller),
                    InstalledVersion = receipt?.InstalledVersion,
                    HasUpdate = hasUpdate
                });
            }

            var detected = Mods.Count(m => m.IsDetected);
            var summary = $"{Mods.Count} mod{(Mods.Count == 1 ? "" : "s")} ({detected} detected).";
            // The refusal leads, and it is NOT called "offline": the server answered, and what it
            // answered failed its checks. Saying only "Offline" here hid the security event
            // completely.
            StatusMessage = _indexRejectionReason is { } rejected
                ? $"This developer's live catalog was refused, so you're seeing the copy saved " +
                  $"{CatalogStatus.FormatCachedAt(_indexCachedAtUtc)}. {rejected} {summary}"
                : _indexFromCache
                ? $"Offline — showing the saved catalog from {CatalogStatus.FormatCachedAt(_indexCachedAtUtc)}. {summary}"
                : summary;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load developer details for {PluginId}", _plugin.Id);
            StatusMessage = "Couldn't load this developer's mods. " +
                            CatalogRefusedException.SpeakableReason(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenMod(DeveloperModItemViewModel? mod)
    {
        if (mod == null || _pluginIndex == null) return;

        var indexes = new Dictionary<string, PluginRepoIndex> { [_plugin.Id] = _pluginIndex };

        var install = _installs.FirstOrDefault(i => i.Game.GameId == mod.GameId && i.IsValid);
        if (install != null)
        {
            // Pass the plugin id so MainViewModel knows to return here when the user presses Back.
            _navigateToGameDetails(install, indexes, _plugin.Id);
            return;
        }

        // Not detected. If the game can install itself (declares a game-installer dependency),
        // open Game Details in the not-installed state so the user can install the game + mod
        // right here — the same flow the Games tab offers.
        if (mod.HasGameInstaller)
        {
            var def = _pluginIndex.Games.FirstOrDefault(g => g.GameId == mod.GameId);
            if (def != null)
            {
                _navigateToGameDetailsUninstalled(def, _plugin.Id, indexes);
                return;
            }
        }

        // Not detected and can't self-install — point them at the Games tab's Browse for Folder.
        StatusMessage = $"Cannot open {mod.GameDisplayName} — game not detected. Open it from the Games tab to use Browse for Folder.";
    }

    [RelayCommand]
    private void GoBack() => _navigateBack();

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        // Author social/website URLs are untrusted plugin metadata — only launch https links,
        // never a file:/custom-scheme URI that would trigger a shell action.
        if (!ExternalLink.TryOpen(url, _logger))
        {
            StatusMessage = "Couldn't open that link in your browser — it may not be a safe https address, or no browser responded.";
        }
    }

    /// <summary>
    /// Pull the GitHub repo segment out of the first release's package URL. Same logic as
    /// ModReleaseGroup.ModName so the announcement is consistent across views.
    /// </summary>
    private static string DeriveModName(PluginRepoIndex index, string gameId)
    {
        if (!index.ReleasesByGameId.TryGetValue(gameId, out var releases) || releases.Count == 0)
            return "mod";

        // Skip Patreon-gated releases — their PackageUrl is null and the Patreon post URL
        // isn't a stable place to derive a mod name from.
        var url = releases.Select(r => r.PackageUrl).FirstOrDefault(u => u is not null);
        if (url is not null && string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = url.Segments;
            if (segments.Length >= 4 &&
                segments[3].TrimEnd('/').Equals("releases", StringComparison.OrdinalIgnoreCase))
            {
                return segments[2].TrimEnd('/');
            }
        }
        return "mod";
    }
}

/// <summary>
/// One row in the Developer Details list — represents this developer's mod for one specific game.
/// </summary>
public partial class DeveloperModItemViewModel : ObservableObject
{
    public required string GameId { get; init; }
    public required string GameDisplayName { get; init; }
    public required string ModName { get; init; }
    public required string PluginId { get; init; }
    public required bool IsDetected { get; init; }
    /// <summary>True when the game can install itself (declares a game-installer dependency),
    /// so it's openable from the not-detected state.</summary>
    public bool HasGameInstaller { get; init; }
    public string? InstalledVersion { get; init; }
    public bool HasUpdate { get; init; }

    public string StatusText => (IsDetected, InstalledVersion, HasUpdate) switch
    {
        (false, _, _) => HasGameInstaller ? "Not installed" : "Game not detected",
        (true, null, _) => "Not installed",
        (true, _, true) => $"v{InstalledVersion} — update available",
        (true, _, false) => $"v{InstalledVersion} installed",
    };

    public string AnnouncementText => $"{ModName} for {GameDisplayName}, {StatusText}";

    public override string ToString() => AnnouncementText;
}
