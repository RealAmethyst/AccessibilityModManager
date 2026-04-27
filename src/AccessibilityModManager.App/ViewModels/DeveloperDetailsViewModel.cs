using System.Collections.ObjectModel;
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

    private readonly PluginEntry _plugin;

    // Cached after load so OpenMod can build the navigation payload.
    private PluginRepoIndex? _pluginIndex;
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
        Action<GameInstall, Dictionary<string, PluginRepoIndex>, string> navigateToGameDetails)
    {
        _plugin = plugin;
        _repoClient = repoClient;
        _configService = configService;
        _receiptStore = receiptStore;
        _gameAggregator = gameAggregator;
        _logger = logger;
        _navigateBack = navigateBack;
        _navigateToGameDetails = navigateToGameDetails;

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
                _pluginIndex = await _repoClient.FetchPluginIndexAsync(_plugin);

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
            _installs = await _gameAggregator.DetectAllGamesAsync(indexes, config.KnownGameOverrides);

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
                    InstalledVersion = receipt?.InstalledVersion,
                    HasUpdate = hasUpdate
                });
            }

            var detected = Mods.Count(m => m.IsDetected);
            StatusMessage = $"{Mods.Count} mod{(Mods.Count == 1 ? "" : "s")} ({detected} detected).";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load developer details for {PluginId}", _plugin.Id);
            StatusMessage = $"Failed to load: {ex.Message}";
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

        var install = _installs.FirstOrDefault(i => i.Game.GameId == mod.GameId && i.IsValid);
        if (install == null)
        {
            // Game isn't detected — Game Details view requires a GameInstall, so we can't open
            // it. Tell the user.
            StatusMessage = $"Cannot open {mod.GameDisplayName} — game not detected. Open it from the Games tab to use Browse for Folder.";
            return;
        }

        var indexes = new Dictionary<string, PluginRepoIndex> { [_plugin.Id] = _pluginIndex };
        // Pass the plugin id so MainViewModel knows to return here when the user presses Back.
        _navigateToGameDetails(install, indexes, _plugin.Id);
    }

    [RelayCommand]
    private void GoBack() => _navigateBack();

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to open URL {Url}", url);
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
    public string? InstalledVersion { get; init; }
    public bool HasUpdate { get; init; }

    public string StatusText => (IsDetected, InstalledVersion, HasUpdate) switch
    {
        (false, _, _) => "Game not detected",
        (true, null, _) => "Not installed",
        (true, _, true) => $"v{InstalledVersion} — update available",
        (true, _, false) => $"v{InstalledVersion} installed",
    };

    public string AnnouncementText => $"{ModName} for {GameDisplayName}, {StatusText}";

    public override string ToString() => AnnouncementText;
}
