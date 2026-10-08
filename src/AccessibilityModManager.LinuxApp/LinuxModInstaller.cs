using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Patreon;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.LinuxApp;

internal sealed class LinuxModInstaller(HttpClient httpClient, ILogger logger)
{
    public static IReadOnlyList<SteamAccountConfig> FindAccounts(GameInstall game) =>
        SteamLocalConfigLocator.Find(game.SteamRootPath
            ?? throw new InvalidOperationException("Steam did not report its installation folder."));

    public static IReadOnlyList<string> FindProtonVersions(GameInstall game)
    {
        var steamRoot = game.SteamRootPath
            ?? throw new InvalidOperationException("Steam did not report its installation folder.");
        string[] roots =
        [
            "/usr/share/steam/compatibilitytools.d",
            Path.Combine(steamRoot, "compatibilitytools.d"),
            Path.Combine(steamRoot, "steamapps", "common")
        ];
        return roots.Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(directory => Path.Combine(directory, "proton"))
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task InstallAsync(LinuxModEntry mod, ModRelease release,
        SteamAccountConfig? account, string? proton, bool update,
        DependencyDialogHost dependencyHost, PatreonService patreon,
        string? localPackagePath = null, IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var target = ReleaseTarget.ForRuntime(release.TargetPlatform);
        var game = target == ReleaseTarget.XivLauncher ? RequireInstall(mod) :
            target == ReleaseTarget.Linux ? RequireNativeGame(mod) : RequireProtonGameWithExecutable(mod);
        var packagePath = Path.Combine(Path.GetTempPath(),
            "amm-catalog-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var repo = new PluginRepoClient(httpClient, logger);
            if (release.Patreon is { } gate)
            {
                if (!patreon.IsCampaignOwner(gate.CampaignId) &&
                    (!await patreon.RefreshEntitlementsAsync(ct) || !patreon.IsEntitled(gate)))
                    throw new InvalidOperationException("Your Patreon access to this release could not be confirmed.");
                if (!string.IsNullOrWhiteSpace(gate.ServerUrl))
                    await patreon.DownloadFromServerAsync(gate.ServerUrl, packagePath, progress, ct);
                else if (localPackagePath is not null)
                    File.Copy(localPackagePath, packagePath);
                else
                    throw new InvalidOperationException("Choose the Patreon package ZIP before installing.");
            }
            else if (release.PackageUrl is not null)
                await repo.DownloadPackageAsync(release.PackageUrl, packagePath, progress, ct);
            else
                throw new InvalidOperationException("This release has no package address.");
            if (!await repo.VerifySha256Async(packagePath, release.Sha256, ct))
                throw new InvalidDataException("The downloaded package does not match its catalog SHA-256.");
            progress?.Report(new ProgressInfo
            {
                Percentage = 100,
                StatusText = target == ReleaseTarget.Linux
                    ? "Preparing the verified native package, then installing the mod."
                    : "Preparing the verified loader and speech bridge, then installing the mod."
            });
            if (target == ReleaseTarget.XivLauncher)
            {
                await CreateXivLauncherSetup().InstallAsync(game, release, packagePath,
                    account?.ConfigPath ?? throw new InvalidOperationException("Choose a Steam account."),
                    update, dependencyHost, progress, ct);
                return;
            }
            if (target == ReleaseTarget.Linux)
            {
                var installer = CreateInstallerEngine();
                if (update)
                    await installer.UpdateAsync(game, release, packagePath, dependencyHost: dependencyHost, ct: ct);
                else
                    await installer.InstallAsync(game, release, packagePath, dependencyHost: dependencyHost, ct: ct);
                return;
            }

            var coordinator = CreateCoordinator(account ?? throw new InvalidOperationException("Choose a Steam account."),
                proton ?? throw new InvalidOperationException("Choose a Proton version."));
            if (target == ReleaseTarget.Windows)
            {
                if (update)
                    await coordinator.UpdateCompatibleWindowsReleaseAsync(game, release, packagePath, ct, dependencyHost);
                else
                    await coordinator.InstallCompatibleWindowsReleaseAsync(game, release, packagePath, ct, dependencyHost);
            }
            else if (update)
                await coordinator.UpdateAsync(game, release, packagePath, ct, dependencyHost);
            else
                await coordinator.InstallAsync(game, release, packagePath, ct, dependencyHost);
        }
        finally
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
        }
    }

    public Task UninstallAsync(LinuxModEntry mod, SteamAccountConfig? account, string? proton,
        CancellationToken ct = default)
    {
        if (mod.InstalledTarget == ReleaseTarget.XivLauncher)
            return CreateXivLauncherSetup().UninstallAsync(RequireInstall(mod), ct);
        if (mod.InstalledTarget == ReleaseTarget.Linux)
            return CreateInstallerEngine().UninstallAsync(RequireNativeGame(mod), mod.Author.Id, ct: ct);
        if (mod.OwnSetup is null)
            throw new InvalidOperationException("The installed mod has no verified Steam setup record. It was left untouched.");
        return CreateCoordinator(account ?? throw new InvalidOperationException("Choose a Steam account."),
            proton ?? throw new InvalidOperationException("Choose a Proton version."))
            .UninstallAsync(RequireProtonGame(mod), mod.Author.Id, ct);
    }

    public Task CompleteInterruptedAsync(LinuxModEntry mod, SteamAccountConfig account, string proton,
        CancellationToken ct = default) =>
        CreateCoordinator(account, proton).CompleteInterruptedInstallAsync(RequireProtonGame(mod), mod.Author.Id, ct);

    private static GameInstall RequireProtonGameWithExecutable(LinuxModEntry mod)
    {
        return SteamGameExecutableResolver.Resolve(RequireProtonGame(mod));
    }

    private static GameInstall RequireInstall(LinuxModEntry mod) => mod.Install ??
        throw new InvalidOperationException($"{mod.Game.DisplayName} was not detected. Choose its game folder first.");

    private static GameInstall RequireNativeGame(LinuxModEntry mod)
    {
        var game = RequireInstall(mod);
        if (string.IsNullOrWhiteSpace(game.Game.LinuxExeName))
            throw new InvalidOperationException("This native Linux game needs a declared Linux executable in its catalog.");
        var executable = PathSafety.CombineContained(game.InstallPath,
            game.Game.LinuxExeName.Replace('\\', '/'));
        if (!File.Exists(executable))
            throw new InvalidOperationException("The declared native Linux game executable was not found.");
        return game;
    }

    private static GameInstall RequireProtonGame(LinuxModEntry mod)
    {
        var game = RequireInstall(mod);
        if (game.ProtonPrefixPath is null)
            throw new InvalidOperationException("The game's Proton prefix is missing. Launch it once from Steam first.");
        return game;
    }

    public XivLauncherSetup CreateXivLauncherSetup() => new(CreateInstallerEngine(), new ReceiptStore(logger),
        httpClient, logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibilityModManager", "xivlauncher-setups"),
        configureFrontend: steamRoot => XivLauncherFrontendInstaller.EnsureInstalled(steamRoot));

    private ProtonSteamInstallCoordinator CreateCoordinator(SteamAccountConfig account, string proton)
    {
        var receipts = new ReceiptStore(logger);
        var installer = CreateInstallerEngine(receipts);
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibilityModManager");
        return new ProtonSteamInstallCoordinator(installer, receipts,
            new ProtonPackageInspector(logger),
            new ProtonWindowsDesktopRuntime(httpClient, Path.Combine(dataRoot, "downloads")),
            new ProtonVisualCppRuntime(httpClient, Path.Combine(dataRoot, "downloads")),
            new SteamLocalConfigStore(account.ConfigPath), Path.Combine(dataRoot, "proton-setups"),
            Path.Combine(AppContext.BaseDirectory, "steam-proton-launch"), proton, logger);
    }

    private InstallerEngine CreateInstallerEngine(ReceiptStore? receipts = null)
    {
        var backup = new BackupManager(logger);
        return new InstallerEngine(backup, new InstallActionExecutor(backup, logger),
            new InstallVerifier(logger), new ManifestParser(logger), new SafeZipExtractor(logger),
            receipts ?? new ReceiptStore(logger), new DependencyChecker(logger),
            new LifecycleScriptRunner(logger),
            new DependencyAutoInstaller(httpClient, new DependencyReceiptStore(logger), logger),
            new GameVerifier(logger), logger);
    }
}
