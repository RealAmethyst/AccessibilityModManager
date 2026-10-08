using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Patreon;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.LinuxApp;

internal sealed record LinuxAuthorEntry(CatalogSource Source, PluginRepoIndex? Index)
{
    public string Id => Source.PluginId;
    public string Author => Index?.Author?.DisplayName ?? Source.RegistryEntry?.Author ??
        Source.UserDisplayName ?? Id;
    public string Description => Index?.Author?.Bio ?? Source.RegistryEntry?.Description ?? "";
    public Uri? Website => Uri.TryCreate(Index?.Author?.WebsiteUrl, UriKind.Absolute, out var website)
        ? website : Source.RegistryEntry?.Website;
    public IReadOnlyDictionary<string, Uri> Links
    {
        get
        {
            var links = new Dictionary<string, Uri>(Source.RegistryEntry?.Links ?? []);
            Add("Discord", Index?.Author?.DiscordUrl);
            Add("Patreon", Index?.Author?.PatreonUrl);
            Add("GitHub", Index?.Author?.GitHubUrl);
            Add("Donate", Index?.Author?.DonationUrl);
            return links;

            void Add(string label, string? value)
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    links[label] = uri;
            }
        }
    }
}

internal sealed record LinuxModEntry(
    LinuxAuthorEntry Author, GameDefinition Game, IReadOnlyList<ModRelease> Releases,
    GameInstall? Install, string? InstalledVersion, string? InstalledTarget,
    ProtonSteamSetupState? OwnSetup, ProtonSteamSetupState? OtherSetup)
{
    public string ModName => !string.IsNullOrWhiteSpace(Game.ModName) ? Game.ModName! :
        Releases.Select(release => release.PackageUrl)
            .FirstOrDefault(url => url is not null && url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            ?.Segments.ElementAtOrDefault(2)?.TrimEnd('/') ?? "mod";

    public string Status => Install is null ? "Game not detected" :
        OtherSetup is { } other && other.PluginId == Author.Id
            ? $"v{other.Version} installed in an earlier local setup" :
        OtherSetup is not null ? $"v{OtherSetup.Version} installed from another author" :
        InstalledVersion is null ? "Not installed" : $"v{InstalledVersion} installed";

    public override string ToString() =>
        $"{ModName} by {Author.Author} for {Game.DisplayName}, {Status}";
}

internal sealed record LinuxCatalog(
    IReadOnlyList<LinuxModEntry> Mods, IReadOnlyList<LinuxAuthorEntry> Authors,
    IReadOnlyList<UserPluginSource> UserSources,
    IReadOnlyList<string> Unavailable, bool FromCache);

internal sealed class LinuxCatalogService(HttpClient httpClient, ILogger logger,
    PatreonService? patreon = null)
{
    public async Task<LinuxCatalog> LoadAsync(CancellationToken ct = default)
    {
        var config = await new ConfigService(logger).LoadAsync();
        var registry = await new PluginRegistryClient(httpClient, logger,
            new RegistrySignatureVerifier(RegistryTrustKey.PublicKeyPem, logger))
            .FetchRegistryAsync(new Uri(config.PluginRegistryUrl), ct);
        var repoClient = new PluginRepoClient(httpClient, logger);
        var detector = new SteamDetector(new GameVerifier(logger), logger);
        var receipts = new ReceiptStore(logger);
        var setupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibilityModManager", "proton-setups");
        var mods = new List<LinuxModEntry>();
        var authors = new List<LinuxAuthorEntry>();
        var unavailable = new List<string>();
        var fromCache = registry.FromCache;
        var accepted = UserPluginSourceValidation.Accept(config.UserPluginSources);
        var resolution = CatalogSourceResolver.Resolve(registry.Value.Plugins, accepted.Accepted);
        unavailable.AddRange(accepted.Rejected.Select(item => item.Describe + ": " + item.Reason));
        unavailable.AddRange(resolution.Refused.Select(item => item.Describe + ": " + item.Reason));

        foreach (var source in resolution.Sources)
        {
            try
            {
                var fetched = await repoClient.FetchPluginIndexAsync(source, ct);
                fromCache |= fetched.FromCache;
                var index = fetched.Value;
                var author = new LinuxAuthorEntry(source, index);
                authors.Add(author);
                var steamGames = index.Games.Where(game => !string.IsNullOrWhiteSpace(game.EffectiveSteamAppId)).ToArray();
                var installs = await detector.DetectInstalledGamesAsync(steamGames, source.PluginId, ct);
                var installByGame = installs.ToDictionary(install => install.Game.GameId);
                foreach (var game in index.Games)
                {
                    if (!index.ReleasesByGameId.TryGetValue(game.GameId, out var allReleases)) continue;
                    var releases = allReleases.Select(release => ReleasePackages.ForPlatform(release, linux: true)).Where(release =>
                            (release.PackageUrl is not null ||
                             release.Patreon is { } gate && patreon is not null &&
                             (patreon.IsCampaignOwner(gate.CampaignId) || patreon.IsEntitled(gate))) &&
                            ReleaseTarget.Normalize(release.TargetPlatform) is
                                ReleaseTarget.Windows or ReleaseTarget.Proton or ReleaseTarget.Linux or ReleaseTarget.WindowsLinux or ReleaseTarget.XivLauncher)
                        .OrderByDescending(release => release.Version, VersionComparer.Instance)
                        .ToArray();
                    if (releases.Length == 0) continue;
                    installByGame.TryGetValue(game.GameId, out var install);
                    if (install is null && config.KnownGameOverrides.TryGetValue(game.GameId, out var overridePath) &&
                        new GameVerifier(logger).VerifyInstallPath(game, overridePath))
                        install = new GameInstall
                        {
                            Game = game, PluginId = source.PluginId,
                            InstallPath = overridePath, IsValid = true
                        };
                    if (install is null && game.LinuxExeName is { } nativeExe &&
                        NativeGameInstaller.Available(game).Count > 0 &&
                        config.InstalledEmulators.TryGetValue("linux:" + nativeExe, out var emulatorPath) &&
                        File.Exists(PathSafety.CombineContained(emulatorPath, nativeExe)) &&
                        new GameVerifier(logger).VerifyInstallPath(game, emulatorPath))
                        install = new GameInstall { Game = game, PluginId = source.PluginId, InstallPath = emulatorPath, IsValid = true };
                    var receipt = await receipts.LoadAsync(game.GameId, source.PluginId);
                    var setups = install is null || string.IsNullOrWhiteSpace(game.EffectiveSteamAppId)
                        ? [] : ProtonSteamSetupLookup.Find(setupRoot, game.EffectiveSteamAppId, install.InstallPath);
                    var ownSetup = setups.FirstOrDefault(state =>
                        state.GameId == game.GameId && state.PluginId == source.PluginId);
                    var otherSetup = setups.FirstOrDefault(state =>
                        state.GameId != game.GameId || state.PluginId != source.PluginId);
                    if (receipt is null && ownSetup is null && otherSetup?.PluginId == source.PluginId)
                        receipt = await receipts.LoadAsync(otherSetup.GameId, source.PluginId);
                    mods.Add(new LinuxModEntry(author, game, releases, install,
                        receipt?.InstalledVersion, receipt?.TargetPlatform, ownSetup, otherSetup));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warning(ex, "Could not load catalog for {PluginId}", source.PluginId);
                unavailable.Add(source.PluginId);
                if (source.RegistryEntry is not null)
                    authors.Add(new LinuxAuthorEntry(source, null));
            }
        }

        logger.Information("Linux catalog prepared {ModCount} available mods from {AuthorCount} authors; {RefusedCount} catalogs unavailable",
            mods.Count, authors.Count, unavailable.Count);
        return new LinuxCatalog(
            mods.OrderBy(mod => mod.ModName, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            authors.OrderBy(author => author.Author, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            accepted.Accepted,
            unavailable, fromCache);
    }
}
