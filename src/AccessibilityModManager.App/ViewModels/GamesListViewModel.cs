using System.Collections.ObjectModel;
using System.Diagnostics;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Patreon;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.App.ViewModels;

public partial class GamesListViewModel : ObservableObject
{
    private readonly IPluginRegistryClient _registryClient;
    private readonly IPluginRepoClient _repoClient;
    private readonly IConfigService _configService;
    private readonly IReceiptStore _receiptStore;
    private readonly IGameVerifier _gameVerifier;
    private readonly GameAggregator _gameAggregator;
    private readonly PatreonService _patreon;
    private readonly ILogger _logger;
    /// <summary>
    /// Opens Game Details. Args: the install, the scoped indexes, and the registry entry of the
    /// developer whose ROW was chosen — passed explicitly because the install can belong to a
    /// different plugin that happened to detect the same game folder.
    /// </summary>
    private readonly Action<GameInstall, Dictionary<string, PluginRepoIndex>, PluginEntry?> _navigateToDetails;
    /// <summary>
    /// Opens Game Details for a game that isn't installed yet but declares a game-installer
    /// dependency (so the user can install the game + mod in one flow). Args: game definition,
    /// owning plugin id, scoped indexes, owning registry entry. Wired in App.xaml.cs.
    /// </summary>
    private readonly Action<GameDefinition, string, Dictionary<string, PluginRepoIndex>, PluginEntry?> _navigateToDetailsUninstalled;
    /// <summary>
    /// Returns the user-selected folder, or null if cancelled. The string param is an optional
    /// initial directory. Wired to <c>Microsoft.Win32.OpenFolderDialog</c> in App.xaml.cs.
    /// </summary>
    private readonly Func<string?, string?> _browseForFolder;

    // Cached after last refresh, so navigation to details can use them
    private Dictionary<string, PluginRepoIndex> _lastActiveIndexes = [];
    private List<GameInstall> _lastInstalls = [];
    private Dictionary<string, List<(string PluginId, GameDefinition Game)>> _lastGameMap = [];

    /// <summary>
    /// The accepted registry entries from the last refresh, by plugin id. Needed for two things a
    /// row alone can't answer: the developer's fallback name when their index has no author block,
    /// and the entry Game Details needs to open that developer's page. It is NOT read off the
    /// GameInstall — detection can hand back another plugin's install for the same game, which
    /// would name the wrong developer.
    /// </summary>
    private Dictionary<string, PluginEntry> _lastPluginEntries = [];

    /// <summary>
    /// Display names for user-added sources this refresh, by plugin id. A source has no registry
    /// listing to fall back to, so without this a source the user saved as "Buu" announces as the
    /// slug <c>buu420</c> whenever its index carries no author block.
    /// </summary>
    private Dictionary<string, string> _lastUserSourceNames = [];

    /// <summary>Plugin ids that came from a source the user added, for this refresh.</summary>
    private HashSet<string> _lastUserSourceIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Author filter ids the user has selected that aren't in the current filter list, because that
    /// developer's catalog failed to load or is fully gated this refresh. Held so persisting the
    /// filters doesn't silently drop a selection the user never cleared.
    /// </summary>
    private readonly HashSet<string> _hiddenSelectedAuthors = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Set only when the status line is worth interrupting for — a refused catalog, an offline
    /// catalog, a failure. Left null for the routine "found N mods", which is shown but not spoken.
    ///
    /// <para>Amethyst, after hearing the first build: a refresh announced a filter count, a mod
    /// count and a developer count one after another, and clicking a mod said them again. Counts
    /// are information you can go and read; a refused catalog is not.</para>
    /// </summary>
    [ObservableProperty]
    private string? _statusAnnouncement;

    /// <summary>Shown beside the filters, never announced — see <see cref="StatusAnnouncement"/>.</summary>
    [ObservableProperty]
    private string? _matchCountText;

    /// <summary>
    /// Show a message AND say it. For problems, and for the result of something the user just did
    /// deliberately. Routine counts assign <see cref="StatusMessage"/> directly and stay quiet.
    /// </summary>
    private void ReportSpoken(string message)
    {
        StatusMessage = message;
        StatusAnnouncement = message;
    }

    public ObservableCollection<ModItemViewModel> Mods { get; } = [];

    /// <summary>Source-of-truth list. <see cref="Mods"/> is the filtered view bound to UI.</summary>
    private readonly List<ModItemViewModel> _allMods = [];

    public ObservableCollection<TagFilterItem> TagFilters { get; } = [];
    public ObservableCollection<LanguageFilterItem> LanguageFilters { get; } = [];
    public ObservableCollection<AuthorFilterItem> AuthorFilters { get; } = [];

    /// <summary>
    /// Drives the Clear button. Counts author selections being held for developers with no checkbox
    /// this refresh: without them the Clear button goes disabled while a saved selection is still
    /// in effect, so the user has no way to get rid of a filter that will silently come back when
    /// that developer's catalog loads again.
    /// </summary>
    public bool HasAnyFilterSelected =>
        TagFilters.Any(f => f.IsSelected) ||
        LanguageFilters.Any(f => f.IsSelected) ||
        AuthorFilters.Any(f => f.IsSelected) ||
        _hiddenSelectedAuthors.Count > 0;

    public GamesListViewModel(
        IPluginRegistryClient registryClient,
        IPluginRepoClient repoClient,
        IConfigService configService,
        IReceiptStore receiptStore,
        IGameVerifier gameVerifier,
        GameAggregator gameAggregator,
        PatreonService patreon,
        ILogger logger,
        Action<GameInstall, Dictionary<string, PluginRepoIndex>, PluginEntry?> navigateToDetails,
        Action<GameDefinition, string, Dictionary<string, PluginRepoIndex>, PluginEntry?> navigateToDetailsUninstalled,
        Func<string?, string?> browseForFolder)
    {
        _registryClient = registryClient;
        _repoClient = repoClient;
        _configService = configService;
        _receiptStore = receiptStore;
        _gameVerifier = gameVerifier;
        _gameAggregator = gameAggregator;
        _patreon = patreon;
        _logger = logger;
        _navigateToDetails = navigateToDetails;
        _navigateToDetailsUninstalled = navigateToDetailsUninstalled;
        _browseForFolder = browseForFolder;

        // When Patreon membership data finishes loading at startup (or refreshes after a
        // sign-in/sign-out), re-render the catalog so newly-visible gated releases show
        // up without requiring the user to click Refresh manually.
        _patreon.SignInStateChanged += OnPatreonStateChanged;
    }

    private void OnPatreonStateChanged()
    {
        // RefreshGamesCommand re-fetches the full registry which is overkill for "just
        // recompute visibility." But the existing aggregation is fast and this only fires
        // a few times per session, so the simplicity wins.
        if (RefreshGamesCommand.CanExecute(null))
        {
            _ = RefreshGamesCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task RefreshGamesAsync(CancellationToken ct)
    {
        IsLoading = true;
        StatusMessage = "Detecting mods...";
        // Re-armed for this pass: clearing guarantees the next notable message counts as a change
        // even when it repeats the last one word for word.
        StatusAnnouncement = null;

        try
        {
            var config = await _configService.LoadAsync();
            var registryFetch = await _registryClient.FetchRegistryAsync(new Uri(config.PluginRegistryUrl), ct);
            var registry = registryFetch.Value;

            // Offline marking (finding 33): if the registry or any index came from the local
            // cache, the status line says so — cached data must never masquerade as live.
            var anyFromCache = registryFetch.FromCache;
            var oldestCachedAt = registryFetch.CachedAtUtc;

            // Every registry-listed plugin is active. We don't filter by an "enabled" state
            // anymore — registry membership IS the gate.
            var activeIndexes = new Dictionary<string, PluginRepoIndex>();

            // Every developer whose catalog could not be loaded, and why. A refusal used to be
            // logged and then followed by a perfectly ordinary "Found N mods", so a plugin
            // disappearing from the catalog — because it was refused, tampered with, or simply
            // unreachable — was indistinguishable from that developer having published nothing.
            // Verification makes refusals real, so they have to be said out loud.
            var unavailable = new List<string>();

            // Sources caught presenting a reserved developer name this refresh. Collected while the
            // rows are built and said once at the end, rather than once per mod.
            var impersonators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The registry and the user's own sources become one ordered list here, and this is the
            // only place that happens. The resolver seeds the registry's identities first, so a
            // source can never publish under a plugin id the signed catalog already uses.
            // Carry over any developer who has left the registry while their mods are still
            // installed, BEFORE the sources are resolved, so their catalog loads on this same
            // refresh rather than the next one.
            //
            // Reached only with an ACCEPTED registry: the fetch above throws otherwise, and this
            // must never run against a registry that failed to load — every plugin would look
            // absent at once and an outage would become a pile of user sources.
            var installedPluginIds = await _receiptStore.InstalledPluginIdsAsync();
            var carried = RegistryDepartureMigration.FindDepartures(
                registry.Plugins, config.UserPluginSources, installedPluginIds,
                config.KnownPluginAddresses, DateTimeOffset.UtcNow);

            if (carried.Count > 0)
            {
                config = await _configService.UpdateAsync(c =>
                {
                    // Re-checked inside the lock against the freshly read settings: another window
                    // may have carried the same developer over, or the user may have added them
                    // back by hand, while this refresh was running.
                    var stillMissing = RegistryDepartureMigration.FindDepartures(
                        registry.Plugins, c.UserPluginSources, installedPluginIds,
                        c.KnownPluginAddresses, DateTimeOffset.UtcNow);

                    foreach (var departure in stillMissing)
                        c.UserPluginSources.Add(departure.Source);
                });

                foreach (var departure in carried)
                {
                    unavailable.Add(
                        $"{departure.Describe} is no longer in the built-in catalog, but you have their mods " +
                        "installed — so they have been kept as a source you can manage on the Developers tab.");
                }
            }

            var accepted = UserPluginSourceValidation.Accept(config.UserPluginSources);
            foreach (var dropped in accepted.Rejected)
                unavailable.Add($"The source {dropped.Describe} wasn't loaded because {dropped.Reason}.");

            var resolution = CatalogSourceResolver.Resolve(registry.Plugins, accepted.Accepted);
            foreach (var refusal in resolution.Refused)
                unavailable.Add($"{refusal.Describe} isn't being shown because {refusal.Reason}.");

            // Written down while the signed registry still names it. This is the only durable
            // record of where a developer's catalog lives once they leave the registry — the index
            // cache holds it too, but clearing a cache is a routine recovery step and must not be
            // able to strand a developer whose mods are installed.
            var addresses = registry.Plugins
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .ToDictionary(p => p.Id, p => p.RepoIndexUrl.AbsoluteUri, StringComparer.OrdinalIgnoreCase);

            if (addresses.Any(a => !config.KnownPluginAddresses.TryGetValue(a.Key, out var known) ||
                                   !string.Equals(known, a.Value, StringComparison.Ordinal)))
            {
                await _configService.UpdateAsync(c =>
                {
                    foreach (var (id, url) in addresses) c.KnownPluginAddresses[id] = url;
                });
            }

            _lastUserSourceIds = resolution.Sources
                .Where(s => s.IsUserAdded)
                .Select(s => s.PluginId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _lastUserSourceNames = resolution.Sources
                .Where(s => s.IsUserAdded && !string.IsNullOrWhiteSpace(s.UserDisplayName))
                .ToDictionary(s => s.PluginId, s => s.UserDisplayName!, StringComparer.OrdinalIgnoreCase);

            foreach (var plugin in resolution.Sources)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var indexFetch = await _repoClient.FetchPluginIndexAsync(plugin, ct);
                    activeIndexes[plugin.PluginId] = indexFetch.Value;

                    if (indexFetch.LiveRejectionReason is { } rejected)
                    {
                        // Said separately from "offline", and NOT counted as offline: the server was
                        // reached, it answered, and what it answered failed its checks. Folding this
                        // into the offline flag made the line claim both at once — "the live catalog
                        // was refused" followed by "Offline" — and only one of them was true.
                        unavailable.Add(
                            $"{DescribeDeveloper(plugin)}'s live catalog was refused, so you're seeing the " +
                            $"copy saved {CatalogStatus.FormatCachedAt(indexFetch.CachedAtUtc)}. {rejected}");
                    }
                    else if (indexFetch.FromCache)
                    {
                        anyFromCache = true;
                        if (oldestCachedAt is null || indexFetch.CachedAtUtc < oldestCachedAt)
                            oldestCachedAt = indexFetch.CachedAtUtc;
                    }
                }
                catch (OperationCanceledException)
                {
                    // The user cancelled, or the whole refresh is being torn down. That is not this
                    // developer's catalog failing, and announcing it as one would be a lie the user
                    // then has to investigate.
                    throw;
                }
                catch (Exception ex)
                {
                    // The full exception goes to the log, where its type names, JSON paths and byte
                    // offsets are useful. What gets SPOKEN is the reason alone — a screen reader
                    // reading out framework text is not a message, it is noise the user then has to
                    // decode.
                    _logger.Warning(ex, "Failed to fetch index for plugin {PluginId}", plugin.PluginId);
                    unavailable.Add($"{DescribeDeveloper(plugin)}'s mods couldn't be loaded. " +
                                    CatalogRefusedException.SpeakableReason(ex));
                }
            }

            var detection = await _gameAggregator.DetectAllGamesAsync(
                activeIndexes, config.KnownGameOverrides, config.InstalledEmulators, ct);
            var installs = detection.Installs;

            // Persist silently-healed overrides (finding 32) so the recovery survives restarts.
            // Fresh read-modify-write on purpose: the config loaded before the (slow) network
            // phase can be stale, and saving it whole would clobber anything written meanwhile —
            // a junction adoption from an install, emulator records, filter changes.
            if (detection.HealedOverrides.Count > 0)
            {
                try
                {
                    await _configService.UpdateAsync(latest =>
                    {
                        foreach (var (gameId, path) in detection.HealedOverrides)
                            latest.KnownGameOverrides[gameId] = path;
                    });
                    _logger.Information("Persisted {Count} healed game override(s)", detection.HealedOverrides.Count);
                }
                catch (Exception ex)
                {
                    // Non-fatal: detection already used the healed paths for this pass; the heal
                    // just won't be remembered until a save succeeds.
                    _logger.Warning(ex, "Couldn't persist healed game overrides");
                }
            }

            _lastActiveIndexes = activeIndexes;
            _lastInstalls = installs;
            _lastPluginEntries = registry.Plugins.ToDictionary(p => p.Id, StringComparer.Ordinal);
            _lastGameMap = GameAggregator.GetGamesByGameId(activeIndexes);
            _allMods.Clear();
            // Mods itself is NOT cleared here: ApplyFilters below rebuilds it, and clearing it twice
            // destroys the focused row twice. Every rebuild costs a screen-reader user an
            // announcement, so the visible collection is touched once, and only when it changed.

            // One row per mod = one row per (developer, game) pair. Same shape as Developer Details.
            var rows = new List<ModItemViewModel>();
            foreach (var (pluginId, index) in activeIndexes)
            {
                foreach (var game in index.Games)
                {
                    // Skip games the developer has declared but hasn't published any release for
                    // yet — there's no "mod" to install, so listing them would be confusing.
                    if (!index.ReleasesByGameId.TryGetValue(game.GameId, out var releases) ||
                        releases.Count == 0)
                    {
                        continue;
                    }

                    // Q3=A: hide the entire row if every release is Patreon-gated and the
                    // user can't see any of them. Mods with at least one public (or
                    // entitled-Patreon) release stay visible — only fully-locked mods
                    // disappear from the catalog.
                    if (!releases.Any(r => IsReleaseVisibleToUser(r)))
                    {
                        continue;
                    }

                    var install = installs.FirstOrDefault(i => i.Game.GameId == game.GameId && i.IsValid);
                    var receipt = await _receiptStore.LoadAsync(game.GameId, pluginId);

                    var latestVersion = releases
                        .Where(r => r.Channel == config.DefaultChannel)
                        .OrderByDescending(r => r.Version, VersionComparer.Instance)
                        .FirstOrDefault()?.Version;

                    var hasUpdate = receipt != null && latestVersion != null &&
                                    VersionComparer.Instance.Compare(latestVersion, receipt.InstalledVersion) > 0;

                    var modName = !string.IsNullOrWhiteSpace(game.ModName)
                        ? game.ModName!
                        : DeriveModName(releases);

                    rows.Add(new ModItemViewModel
                    {
                        GameId = game.GameId,
                        GameDisplayName = game.DisplayName,
                        ModName = modName,
                        PluginId = pluginId,
                        DeveloperName = ResolveDeveloperName(pluginId, index, impersonators),
                        IsDetected = install != null,
                        HasGameInstaller = game.Dependencies.Any(d => d.IsGameInstaller),
                        InstallPath = install?.InstallPath,
                        InstalledVersion = receipt?.InstalledVersion,
                        HasUpdate = hasUpdate,
                        Tags = game.Tags.ToList(),
                        Languages = game.Languages.ToList()
                    });
                }
            }

            // Sort alphabetically by game, then by mod name within a game.
            foreach (var row in rows
                .OrderBy(r => r.GameDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.ModName, StringComparer.OrdinalIgnoreCase))
            {
                _allMods.Add(row);
            }

            // Said once per refresh, not once per mod. A source presenting a reserved name is the
            // user's business — they chose to add it, and it just tried to pass itself off as
            // someone else — so this leads the status line like any other refusal.
            foreach (var pluginId in impersonators.OrderBy(id => id, StringComparer.Ordinal))
            {
                unavailable.Add(
                    $"The source \"{pluginId}\" tried to use a developer name it isn't allowed to use, " +
                    "so it's being shown by its id instead.");
            }

            RebuildFilters(config);
            ApplyFilters();

            var detected = _allMods.Count(m => m.IsDetected);
            var summary = $"Found {_allMods.Count} mod{(_allMods.Count == 1 ? "" : "s")} ({detected} detected).";
            if (anyFromCache)
                summary = $"Offline — showing the saved catalog from {CatalogStatus.FormatCachedAt(oldestCachedAt)}. {summary}";

            // The problem first, then the count. A screen reader speaks this line straight through,
            // and a warning that arrives after "Found 12 mods" is a warning about a list the listener
            // has already accepted as complete.
            StatusMessage = unavailable.Count > 0
                ? string.Join(" ", unavailable) + " " + summary
                : summary;

            // Spoken only when something is actually wrong. A plain count is shown and left alone.
            StatusAnnouncement = unavailable.Count > 0 || anyFromCache ? StatusMessage : null;
        }
        catch (OperationCanceledException)
        {
            // The user cancelled; they know. Shown, not announced.
            StatusMessage = "Detection cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to detect mods");
            ReportSpoken("Couldn't load the catalog. " + CatalogRefusedException.SpeakableReason(ex));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// The install this row will operate on, always described by THIS row's developer.
    ///
    /// <para>Several developers may define the same game — that is supported, and now includes
    /// sources the user added, which nothing vouches for. The install this returns carries a
    /// <see cref="GameDefinition"/>, and that definition supplies the dependencies, the setup
    /// scripts and the text of the consent prompts for everything done next. Handing back another
    /// developer's detection therefore hands them the operation: an unsigned source that declared a
    /// registry game id could supply the dependency installer — possibly an elevated one — used
    /// while installing the registry developer's mod, with the screen naming the registry developer
    /// throughout.</para>
    ///
    /// <para>So another developer's detection contributes only the FOLDER. The definition always
    /// comes from the row's own index, and the borrowed path is re-verified against it before it is
    /// used. That also tightens the pre-existing case where one registry plugin could supply the
    /// definition for another's row.</para>
    /// </summary>
    private GameInstall? ResolveOwnedInstall(ModItemViewModel mod, PluginRepoIndex pluginIndex)
    {
        // The row's own developer detected it: nothing is borrowed.
        var own = _lastInstalls.FirstOrDefault(i =>
            i.Game.GameId == mod.GameId && i.PluginId == mod.PluginId && i.IsValid);
        if (own != null) return own;

        // Someone else detected the same game. Take the location only.
        var elsewhere = _lastInstalls.FirstOrDefault(i => i.Game.GameId == mod.GameId && i.IsValid);
        if (elsewhere == null) return null;

        var ownDefinition = pluginIndex.Games.FirstOrDefault(g => g.GameId == mod.GameId);
        if (ownDefinition == null) return null;

        // The borrowed folder has to satisfy THIS developer's idea of the game, not the one whose
        // detection found it. Without this, a source could point a row at any folder its own probe
        // rules happened to match.
        if (!_gameVerifier.VerifyInstallPath(ownDefinition, elsewhere.InstallPath))
        {
            _logger.Information(
                "Not using {OtherPlugin}'s detection of {GameId} for {OwnPlugin}: the folder doesn't verify against {OwnPlugin}'s own definition",
                elsewhere.PluginId, mod.GameId, mod.PluginId);
            return null;
        }

        return new GameInstall
        {
            Game = ownDefinition,
            PluginId = mod.PluginId,
            InstallPath = elsewhere.InstallPath,
            IsValid = true,
            DetectedVersion = elsewhere.DetectedVersion
        };
    }

    /// <summary>
    /// The developer name for one row. A user-added source may not present itself under a reserved
    /// name; anything from the signed registry is left exactly as it is.
    /// </summary>
    private string ResolveDeveloperName(
        string pluginId, PluginRepoIndex index, HashSet<string> impersonators)
    {
        if (!_lastUserSourceIds.Contains(pluginId))
        {
            return DeveloperNames.Resolve(index, _lastPluginEntries.GetValueOrDefault(pluginId), pluginId);
        }

        var name = DeveloperNames.ResolveUserSource(
            index, _lastUserSourceNames.GetValueOrDefault(pluginId), pluginId, out var wasReserved);

        if (wasReserved) impersonators.Add(pluginId);
        return name;
    }

    /// <summary>
    /// How a developer is named when their catalog can't be loaded. Their index is exactly what
    /// failed to arrive, so there is no display name to prefer here — the registry entry is all
    /// there is, which is what <see cref="DeveloperNames"/> falls back to.
    /// </summary>
    private static string DescribeDeveloper(PluginEntry plugin)
        => DeveloperNames.Resolve(index: null, plugin, plugin.Id);

    /// <summary>
    /// Same question for a catalog that may have come from the registry or from the user. A user
    /// source has no registry listing, so its saved display name stands in before the bare id.
    /// </summary>
    private static string DescribeDeveloper(CatalogSource source)
        => DeveloperNames.Resolve(index: null, source.RegistryEntry, source.UserDisplayName, source.PluginId);

    /// <summary>
    /// Q3=A: a release is visible if it isn't Patreon-gated, or if the user is currently
    /// entitled to one of the gate's tiers. Per Q6=C the gate is purely per-release —
    /// channel-default schema was dropped. Creators who own the gate's campaign also see
    /// their own gated releases so they can install via the local-file path.
    /// </summary>
    private bool IsReleaseVisibleToUser(ModRelease release)
    {
        if (release.Patreon == null) return true;
        if (_patreon.IsCampaignOwner(release.Patreon.CampaignId)) return true;
        return _patreon.IsEntitled(release.Patreon);
    }

    /// <summary>
    /// Best-effort human name for a mod, parsed from its first release's package URL. For
    /// GitHub-hosted releases this is the source repo name (e.g. "DigimonNOAccess"). Falls back
    /// to "mod" when the URL doesn't match a github.com release pattern. Same logic as
    /// ModReleaseGroup.ModName / DeveloperDetailsViewModel — keep them in sync.
    /// </summary>
    private static string DeriveModName(IReadOnlyList<ModRelease> releases)
    {
        if (releases.Count == 0) return "mod";
        // Skip Patreon-gated releases — their PackageUrl is null. Falls back to "mod" if all
        // releases are gated, which is the desired behavior for a Patron-only mod anyway.
        var url = releases.Select(r => r.PackageUrl).FirstOrDefault(u => u is not null);
        return DeriveModNameFromUrl(url);
    }

    private static string DeriveModNameFromUrl(Uri? url)
    {
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

    [RelayCommand]
    private void OpenGameDetails(ModItemViewModel? mod)
    {
        if (mod == null) return;
        // Detected games open normally; an undetected game opens only if it can install itself.
        if (!mod.IsDetected && !mod.HasGameInstaller) return;

        // Scope the navigation payload to this mod's developer so Game Details shows just one
        // mod card (not every developer's mods for that game). Matches Developer Details flow.
        if (!_lastActiveIndexes.TryGetValue(mod.PluginId, out var pluginIndex)) return;
        var scoped = new Dictionary<string, PluginRepoIndex> { [mod.PluginId] = pluginIndex };
        // THIS row's developer. Taken from the row, never from the install found below, which may
        // have been detected under a different plugin that supports the same game.
        var owner = _lastPluginEntries.GetValueOrDefault(mod.PluginId);

        if (mod.IsDetected)
        {
            var install = ResolveOwnedInstall(mod, pluginIndex);
            if (install == null) return;
            _navigateToDetails(install, scoped, owner);
        }
        else
        {
            // Not installed yet, but the game declares a game-installer dependency: open Game
            // Details in the not-installed state so the user can pick a version and Install —
            // which runs the game installer first, then the mod.
            var def = pluginIndex.Games.FirstOrDefault(g => g.GameId == mod.GameId);
            if (def == null) return;
            _navigateToDetailsUninstalled(def, mod.PluginId, scoped, owner);
        }
    }

    [RelayCommand]
    private void OpenGameFolder(ModItemViewModel? mod)
    {
        if (string.IsNullOrEmpty(mod?.InstallPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = mod.InstallPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to open game folder");
            ReportSpoken("Couldn't open the game folder. Check the log for details.");
        }
    }

    /// <summary>
    /// Browse to a folder and use it as the install path for an undetected game.
    /// The folder must pass the game's probe rules (exe presence, etc.) — otherwise the
    /// override is rejected so users can't accidentally point at the wrong game.
    /// </summary>
    [RelayCommand]
    private async Task BrowseForGameAsync(ModItemViewModel? mod)
    {
        if (mod == null) return;
        if (!_lastGameMap.TryGetValue(mod.GameId, out var pluginGames) || pluginGames.Count == 0)
        {
            ReportSpoken("Game definition not loaded — try refreshing first.");
            return;
        }

        var pickedPath = _browseForFolder(null);
        if (string.IsNullOrEmpty(pickedPath)) return;

        // Verify against any plugin's definition for this game; the first one that accepts the
        // path wins. (All definitions for the same gameId should agree on probe rules in practice.)
        var validatingDefinition = pluginGames
            .Select(pg => pg.Game)
            .FirstOrDefault(g => _gameVerifier.VerifyInstallPath(g, pickedPath));

        if (validatingDefinition == null)
        {
            var firstName = pluginGames[0].Game.DisplayName;
            ReportSpoken($"That folder doesn't look like a {firstName} install — " +
                         "expected files were not found. Override not saved.");
            _logger.Warning("Browse rejected for {GameId} at {Path}: probe rules failed", mod.GameId, pickedPath);
            return;
        }

        try
        {
            await _configService.UpdateAsync(config => config.KnownGameOverrides[mod.GameId] = pickedPath);
            _logger.Information("Saved manual override for {GameId}: {Path}", mod.GameId, pickedPath);

            // The user just picked this folder; confirming it took is the answer to their action.
            ReportSpoken($"Saved location for {validatingDefinition.DisplayName}. Refreshing...");
            await RefreshGamesCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save game override");
            ReportSpoken("Couldn't save that game location. Check the log for details.");
        }
    }

    private void RebuildFilters(AppConfig config)
    {
        var savedTags = new HashSet<string>(config.SelectedTagFilters, StringComparer.OrdinalIgnoreCase);
        var savedLangs = new HashSet<string>(config.SelectedLanguageFilters, StringComparer.OrdinalIgnoreCase);
        var savedAuthors = new HashSet<string>(config.SelectedAuthorFilters, StringComparer.OrdinalIgnoreCase);

        TagFilters.Clear();
        foreach (var tag in TagCatalog.Core)
        {
            TagFilters.Add(new TagFilterItem(
                tag.Id, tag.Label, tag.Category,
                isSelected: savedTags.Contains(tag.Id),
                onToggle: OnFilterToggled));
        }
        // Custom tags found across loaded mods get appended after the core list.
        var customTagIds = _allMods
            .SelectMany(m => m.Tags)
            .Where(id => TagCatalog.FindById(id) == null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        foreach (var customId in customTagIds)
        {
            TagFilters.Add(new TagFilterItem(
                customId, customId, "Custom",
                isSelected: savedTags.Contains(customId),
                onToggle: OnFilterToggled));
        }

        LanguageFilters.Clear();
        // Always show the curated language catalog so users can pick filters even when no
        // loaded mod has declared a language yet. Mirrors the Tags section where the core
        // tag list is always available regardless of what mods have selected.
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in LanguageCatalog.All)
        {
            LanguageFilters.Add(new LanguageFilterItem(
                lang.Code, lang.Label,
                isSelected: savedLangs.Contains(lang.Code),
                onToggle: OnFilterToggled));
            seenCodes.Add(lang.Code);
        }
        // Append any extra codes that some mod declared but the catalog doesn't list, so we
        // never silently hide a filter the user might be looking for.
        var extras = _allMods
            .SelectMany(m => m.Languages)
            .Where(c => !seenCodes.Contains(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase);
        foreach (var code in extras)
        {
            LanguageFilters.Add(new LanguageFilterItem(
                code, LanguageCatalog.LabelFor(code),
                isSelected: savedLangs.Contains(code),
                onToggle: OnFilterToggled));
        }

        AuthorFilters.Clear();
        // Labelled with the developer's name; still keyed by plugin id, which is what filtering
        // and the saved config use. Sorted by what the user reads, not by the internal id.
        var presentAuthors = _allMods
            .GroupBy(m => m.PluginId, StringComparer.OrdinalIgnoreCase)
            .Select(g => (PluginId: g.Key, Label: g.First().DeveloperName))
            .OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.PluginId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var (pluginId, label) in presentAuthors)
        {
            AuthorFilters.Add(new AuthorFilterItem(
                pluginId,
                label,
                isSelected: savedAuthors.Contains(pluginId),
                onToggle: OnFilterToggled));
        }

        // A developer whose catalog was refused or is fully gated has no rows this pass, so they
        // have no checkbox — but the user never unticked them. Remembered here and merged back on
        // save, otherwise the next unrelated toggle would quietly rewrite the saved list without
        // them and the filter would come back changed.
        _hiddenSelectedAuthors.Clear();
        var present = presentAuthors.Select(a => a.PluginId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var saved in savedAuthors.Where(s => !present.Contains(s)))
            _hiddenSelectedAuthors.Add(saved);

        // Rebuilding changes what is selected, so the Clear button's enabled state has to be
        // re-evaluated — it is computed, and nothing else notifies it from here.
        OnPropertyChanged(nameof(HasAnyFilterSelected));
    }

    private bool _suppressFilterChanges;

    private void OnFilterToggled()
    {
        if (_suppressFilterChanges) return;
        ApplyFilters();
        _ = PersistFiltersAsync();
        OnPropertyChanged(nameof(HasAnyFilterSelected));
    }

    private void ApplyFilters()
    {
        var selectedTags = TagFilters.Where(f => f.IsSelected).Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedLangs = LanguageFilters.Where(f => f.IsSelected).Select(f => f.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedAuthors = AuthorFilters.Where(f => f.IsSelected).Select(f => f.PluginId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filtered = _allMods.Where(m =>
            (selectedTags.Count == 0 || m.Tags.Any(t => selectedTags.Contains(t))) &&
            (selectedLangs.Count == 0 || m.Languages.Any(l => selectedLangs.Contains(l))) &&
            (selectedAuthors.Count == 0 || selectedAuthors.Contains(m.PluginId))
        ).ToList();

        // Rebuilt only when the result actually differs. A refresh that changes nothing — the
        // routine one when the Patreon membership load finishes and asks every view to re-render —
        // would otherwise clear the list, destroy the row the user is focused on, and make the
        // screen reader announce it all over again for no reason.
        if (!SameRows(filtered, Mods))
        {
            Mods.Clear();
            foreach (var m in filtered) Mods.Add(m);
        }

        MatchCountText = filtered.Count == _allMods.Count
            ? $"{_allMods.Count} mod{(_allMods.Count == 1 ? "" : "s")} shown."
            : $"{filtered.Count} of {_allMods.Count} mods shown.";
    }

    /// <summary>
    /// Whether two row sequences are the same AS THE USER EXPERIENCES THEM — not the same objects.
    /// A refresh builds fresh view models every time, so comparing references would report every
    /// refresh as a change and rebuild the list regardless. What matters is the identity of each row
    /// and the sentence the screen reader would read out for it, which is exactly what changes when
    /// a mod is installed, updated, or appears.
    /// </summary>
    private static bool SameRows(IReadOnlyList<ModItemViewModel> a, IList<ModItemViewModel> b)
    {
        if (a.Count != b.Count) return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].PluginId, b[i].PluginId, StringComparison.Ordinal) ||
                !string.Equals(a[i].GameId, b[i].GameId, StringComparison.Ordinal) ||
                !string.Equals(a[i].AnnouncementText, b[i].AnnouncementText, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task PersistFiltersAsync()
    {
        try
        {
            await _configService.UpdateAsync(config =>
            {
                config.SelectedTagFilters = TagFilters.Where(f => f.IsSelected).Select(f => f.Id).ToList();
                config.SelectedLanguageFilters = LanguageFilters.Where(f => f.IsSelected).Select(f => f.Code).ToList();
                config.SelectedAuthorFilters = AuthorFilters
                    .Where(f => f.IsSelected)
                    .Select(f => f.PluginId)
                    .Concat(_hiddenSelectedAuthors)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist filter selections");
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressFilterChanges = true;
        try
        {
            foreach (var f in TagFilters) f.IsSelected = false;
            foreach (var f in LanguageFilters) f.IsSelected = false;
            foreach (var f in AuthorFilters) f.IsSelected = false;
            // Clear means clear: this is the explicit act that also drops selections being held
            // for developers who have no checkbox right now.
            _hiddenSelectedAuthors.Clear();
        }
        finally { _suppressFilterChanges = false; }

        ApplyFilters();
        _ = PersistFiltersAsync();
        OnPropertyChanged(nameof(HasAnyFilterSelected));
    }
}

public sealed partial class TagFilterItem : ObservableObject
{
    private readonly Action _onToggle;
    public string Id { get; }
    public string Label { get; }
    public string Category { get; }

    [ObservableProperty]
    private bool _isSelected;

    public TagFilterItem(string id, string label, string category, bool isSelected, Action onToggle)
    {
        Id = id;
        Label = label;
        Category = category;
        _isSelected = isSelected;
        _onToggle = onToggle;
    }

    partial void OnIsSelectedChanged(bool value) => _onToggle();
    public override string ToString() => Label;
}

public sealed partial class LanguageFilterItem : ObservableObject
{
    private readonly Action _onToggle;
    public string Code { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public LanguageFilterItem(string code, string label, bool isSelected, Action onToggle)
    {
        Code = code;
        Label = label;
        _isSelected = isSelected;
        _onToggle = onToggle;
    }

    partial void OnIsSelectedChanged(bool value) => _onToggle();
    public override string ToString() => Label;
}

public sealed partial class AuthorFilterItem : ObservableObject
{
    private readonly Action _onToggle;

    /// <summary>Identity: what filtering compares and what the config persists. Never displayed.</summary>
    public string PluginId { get; }

    /// <summary>What the user sees and hears — the developer's name, not the id slug.</summary>
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public AuthorFilterItem(string pluginId, string label, bool isSelected, Action onToggle)
    {
        PluginId = pluginId;
        Label = label;
        _isSelected = isSelected;
        _onToggle = onToggle;
    }

    partial void OnIsSelectedChanged(bool value) => _onToggle();
    public override string ToString() => Label;
}

/// <summary>
/// One row in the Mods tab — represents a single developer's mod for a single game. Same shape
/// and announcement format as DeveloperModItemViewModel so the screen reader reads consistently.
/// </summary>
public partial class ModItemViewModel : ObservableObject
{
    public required string GameId { get; init; }
    public required string GameDisplayName { get; init; }
    public required string ModName { get; init; }
    public required string PluginId { get; init; }

    /// <summary>
    /// Who made this mod, as a person's name rather than the plugin id slug. Resolved through
    /// <see cref="DeveloperNames"/> so every place that names a developer agrees.
    /// </summary>
    public required string DeveloperName { get; init; }

    public required bool IsDetected { get; init; }
    /// <summary>True when the game declares a game-installer dependency, so it can be installed
    /// from the not-detected state.</summary>
    public bool HasGameInstaller { get; init; }
    public string? InstallPath { get; init; }
    public string? InstalledVersion { get; init; }
    public bool HasUpdate { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Languages { get; init; } = [];

    public string StatusText => (IsDetected, InstalledVersion, HasUpdate) switch
    {
        (false, _, _) => HasGameInstaller ? "Not installed" : "Game not detected",
        (true, null, _) => "Not installed",
        (true, _, true) => $"v{InstalledVersion} — update available",
        (true, _, false) => $"v{InstalledVersion} installed",
    };

    /// <summary>
    /// "Blind Access FF7 by Amethyst for Final Fantasy VII, v1.2 installed". The developer sits
    /// with the mod rather than next to the status: the mod and who made it are one thought, and
    /// the game is what the user scans past when hunting down the list.
    /// </summary>
    public string AnnouncementText => $"{ModName} by {DeveloperName} for {GameDisplayName}, {StatusText}";

    public override string ToString() => AnnouncementText;
}
