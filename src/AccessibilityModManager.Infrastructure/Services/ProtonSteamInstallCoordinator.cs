using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Keeps the package receipt and Steam launch-option ownership linked. Steam must be closed
/// before any mutation; an interrupted setup keeps its record so it can be completed or removed.
/// </summary>
public sealed class ProtonSteamInstallCoordinator(
    IInstallerEngine installer,
    IReceiptStore receipts,
    ProtonPackageInspector packageInspector,
    ProtonWindowsDesktopRuntime runtime,
    ProtonVisualCppRuntime visualCppRuntime,
    SteamLocalConfigStore steamConfig,
    string setupRoot,
    string wrapperPath,
    string protonExecutable,
    ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task InstallCompatibleWindowsReleaseAsync(
        GameInstall game, ModRelease release, string packagePath, CancellationToken ct = default,
        IDependencyHost? dependencyHost = null)
    {
        using var adapted = await new ProtonWindowsPackageAdapter(logger)
            .PrepareAsync(game, release, packagePath, ct);
        await InstallAsync(game, adapted.Release, adapted.Path, ct, dependencyHost);
    }

    public async Task UpdateCompatibleWindowsReleaseAsync(
        GameInstall game, ModRelease release, string packagePath, CancellationToken ct = default,
        IDependencyHost? dependencyHost = null)
    {
        using var adapted = await new ProtonWindowsPackageAdapter(logger)
            .PrepareAsync(game, release, packagePath, ct);
        await UpdateAsync(game, adapted.Release, adapted.Path, ct, dependencyHost);
    }

    public async Task InstallAsync(
        GameInstall game, ModRelease release, string packagePath, CancellationToken ct = default,
        IDependencyHost? dependencyHost = null)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Proton Steam setup requires Linux.");
        EnsureGame(game, release);
        LinuxDependencySupport.EnsureSupported(game.Game);
        steamConfig.EnsureSteamClosed();
        if (!File.Exists(wrapperPath) ||
            (File.GetUnixFileMode(wrapperPath) &
             (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new InvalidOperationException("The Linux Steam launch wrapper is missing or is not executable.");
        if (!File.Exists(protonExecutable))
            throw new InvalidOperationException("The selected Proton executable is missing.");
        // Parse the account settings before installing any Windows runtime in the prefix.
        await steamConfig.ReadLaunchOptionsAsync(game.Game.SteamAppId!, ct);
        using var gameLock = LockGame(game, release.PluginId);
        var setupPath = SetupPath(game.Game.GameId, release.PluginId);
        if (File.Exists(setupPath))
            throw new InvalidOperationException("A Proton setup record already exists. Complete or uninstall it before another install.");
        var otherOwner = ProtonSteamSetupLookup.Find(setupRoot, game.Game.SteamAppId!, game.InstallPath)
            .FirstOrDefault(state => state.GameId != game.Game.GameId || state.PluginId != release.PluginId);
        if (otherOwner is not null)
            throw new InvalidOperationException(
                $"Steam game {game.Game.SteamAppId} already has a mod launch setup owned by " +
                $"{otherOwner.PluginId}/{otherOwner.GameId}. Resolve that installation before adding another setup.");
        if (await receipts.LoadAsync(game.Game.GameId, release.PluginId) is not null)
            throw new InvalidOperationException("This mod already has an install receipt. Use update or uninstall.");

        using var package = await packageInspector.PrepareAsync(packagePath, release, ct);
        var launch = package.Manifest.ProtonLaunch!;
        if (launch.SteamAppId != game.Game.SteamAppId ||
            launch.GameExecutable != game.Game.ExeName!.Replace('\\', '/'))
            throw new InvalidDataException("The package's Steam App ID or game executable differs from the detected game.");
        if (!launch.WineDllProxyFromDependency) VerifyInstalledProxy(game, launch);
        if (launch.WindowsDesktopRuntimeVersion is { } requiredVersion)
        {
            var compatPath = Path.GetDirectoryName(game.ProtonPrefixPath!)!;
            await runtime.EnsureAsync(new ProtonContext(protonExecutable, game.SteamRootPath!,
                compatPath, game.Game.SteamAppId!), requiredVersion,
                launch.WindowsDesktopRuntimeSha512 ?? ProtonWindowsDesktopRuntime.InstallerSha512, ct);
        }
        if (package.RequiredVisualCppRuntimes != VisualCppArchitecture.None)
        {
            var compatPath = Path.GetDirectoryName(game.ProtonPrefixPath!)!;
            await visualCppRuntime.EnsureAsync(new ProtonContext(protonExecutable, game.SteamRootPath!,
                compatPath, game.Game.SteamAppId!), package.RequiredVisualCppRuntimes, ct);
        }

        var installed = false;
        try
        {
            await installer.InstallAsync(game, release, package.Path,
                dependencyHost: dependencyHost, ct: ct);
            installed = true;
            if (launch.WineDllProxyFromDependency) VerifyInstalledProxy(game, launch);

            var gameExe = PathSafety.CombineContained(game.InstallPath, game.Game.ExeName!.Replace('\\', '/'));
            var plan = await steamConfig.PlanAsync(game.Game.SteamAppId!, wrapperPath,
                gameExe, game.InstallPath, launch, ct);
            var reloadedFiles = launch.ReloadedRoot is null
                ? new List<OwnedProtonConfigFile>()
                : ProtonReloadedConfig.Plan(game, launch).ToList();
            var state = new ProtonSteamSetupState(
                game.Game.GameId, release.PluginId, release.Version, game.InstallPath,
                game.Game.SteamAppId!, game.ProtonPrefixPath!, plan, reloadedFiles);
            await SaveStateAsync(setupPath, state);
            ProtonReloadedConfig.Apply(game, state.ReloadedFiles);
            await steamConfig.InstallAsync(game.Game.SteamAppId!, plan, ct);
            logger.Information("Proton Steam setup complete for {PluginId}/{GameId}",
                release.PluginId, game.Game.GameId);
        }
        catch
        {
            if (installed && !File.Exists(setupPath))
            {
                // Before a setup record exists, Steam has not been changed. Restore package files.
                await installer.UninstallAsync(game, release.PluginId, ct: CancellationToken.None);
            }
            // After the setup record is saved, keep both it and the file receipt. Steam may
            // have changed; uninstall can inspect the exact current value and recover safely.
            throw;
        }
    }

    public async Task UpdateAsync(
        GameInstall game, ModRelease release, string packagePath, CancellationToken ct = default,
        IDependencyHost? dependencyHost = null)
    {
        EnsureGame(game, release);
        LinuxDependencySupport.EnsureSupported(game.Game);
        steamConfig.EnsureSteamClosed();
        using var gameLock = LockGame(game, release.PluginId);
        var state = await LoadStateAsync(game, release.PluginId);
        if (await receipts.LoadAsync(game.Game.GameId, release.PluginId) is null)
            throw new InvalidOperationException("The Proton setup record exists but its mod receipt is missing.");
        var current = await steamConfig.ReadLaunchOptionsAsync(state.SteamAppId, ct);
        if (current != state.Plan.Installed)
            throw new InvalidOperationException("Steam launch options changed after install. Restore them before updating.");

        using var package = await packageInspector.PrepareAsync(packagePath, release, ct);
        var launch = package.Manifest.ProtonLaunch!;
        if (launch.SteamAppId != state.SteamAppId ||
            launch.GameExecutable != game.Game.ExeName!.Replace('\\', '/'))
            throw new InvalidDataException("The update targets a different Steam game or executable.");
        VerifyInstalledProxy(game, launch);
        var gameExe = PathSafety.CombineContained(game.InstallPath, game.Game.ExeName!.Replace('\\', '/'));
        SteamLaunchOptionsPlan nextPlan;
        if (state.Plan.Installed.Contains("--amm-v1", StringComparison.Ordinal))
        {
            nextPlan = SteamLaunchOptionsPlan.Create(state.Plan.WasPresent ? state.Plan.Previous : null,
                wrapperPath, gameExe, game.InstallPath, launch);
        }
        else
        {
            if (launch.LaunchMode != "replaceExecutable" || launch.LauncherPath is null ||
                launch.BridgeDirectory is null || launch.WineDllOverrides.Count != 0)
                throw new InvalidOperationException("This update changes the legacy Steam launch setup. Uninstall and install it fresh.");
            nextPlan = SteamLaunchOptionsPlan.Create(state.Plan.WasPresent ? state.Plan.Previous : null,
                wrapperPath, gameExe,
                PathSafety.CombineContained(game.InstallPath, launch.LauncherPath),
                PathSafety.CombineContained(game.InstallPath, launch.BridgeDirectory));
        }
        if (nextPlan.Installed != state.Plan.Installed)
            throw new InvalidOperationException("This update changes Steam launch setup. Uninstall and install it fresh.");
        var nextReloadedFiles = launch.ReloadedRoot is null
            ? []
            : ProtonReloadedConfig.Plan(game, launch);
        if (nextReloadedFiles.Count != state.ReloadedFiles.Count ||
            nextReloadedFiles.Where((file, index) =>
                file.Path != state.ReloadedFiles[index].Path ||
                file.InstalledBase64 != state.ReloadedFiles[index].InstalledBase64).Any())
            throw new InvalidOperationException("This update changes Reloaded II prefix setup. Uninstall and install it fresh.");

        if (launch.WindowsDesktopRuntimeVersion is { } requiredVersion)
        {
            var compatPath = Path.GetDirectoryName(game.ProtonPrefixPath!)!;
            await runtime.EnsureAsync(new ProtonContext(protonExecutable, game.SteamRootPath!,
                compatPath, state.SteamAppId), requiredVersion,
                launch.WindowsDesktopRuntimeSha512 ?? ProtonWindowsDesktopRuntime.InstallerSha512, ct);
        }
        if (package.RequiredVisualCppRuntimes != VisualCppArchitecture.None)
        {
            var compatPath = Path.GetDirectoryName(game.ProtonPrefixPath!)!;
            await visualCppRuntime.EnsureAsync(new ProtonContext(protonExecutable, game.SteamRootPath!,
                compatPath, state.SteamAppId), package.RequiredVisualCppRuntimes, ct);
        }
        await installer.UpdateAsync(game, release, package.Path,
            dependencyHost: dependencyHost, ct: ct);
        await SaveStateAsync(SetupPath(game.Game.GameId, release.PluginId),
            state with { Version = release.Version });
        logger.Information("Proton mod updated to {Version} for {PluginId}/{GameId}",
            release.Version, release.PluginId, game.Game.GameId);
    }

    public async Task CompleteInterruptedInstallAsync(
        GameInstall game, string pluginId, CancellationToken ct = default)
    {
        steamConfig.EnsureSteamClosed();
        using var gameLock = LockGame(game, pluginId);
        var state = await LoadStateAsync(game, pluginId);
        if (await receipts.LoadAsync(game.Game.GameId, pluginId) is null)
            throw new InvalidOperationException("Proton setup record exists but the mod's install receipt is missing.");
        ProtonReloadedConfig.Apply(game, state.ReloadedFiles);
        var current = await steamConfig.ReadLaunchOptionsAsync(state.SteamAppId, ct);
        if (current == state.Plan.Installed) return;
        if (!IsPrevious(current, state.Plan))
            throw new InvalidOperationException("Steam launch options changed during the interrupted setup. Review them before continuing.");
        await steamConfig.InstallAsync(state.SteamAppId, state.Plan, ct);
    }

    public async Task UninstallAsync(GameInstall game, string pluginId, CancellationToken ct = default)
    {
        steamConfig.EnsureSteamClosed();
        using var gameLock = LockGame(game, pluginId);
        var state = await LoadStateAsync(game, pluginId);
        var current = await steamConfig.ReadLaunchOptionsAsync(state.SteamAppId, ct);
        if (current == state.Plan.Installed)
            await steamConfig.RestoreAsync(state.SteamAppId, state.Plan, ct);
        else if (!IsPrevious(current, state.Plan))
            throw new InvalidOperationException("Steam launch options changed after the mod was installed. Review them before uninstalling.");

        ProtonReloadedConfig.Restore(game, state.ReloadedFiles);
        if (await receipts.LoadAsync(game.Game.GameId, pluginId) is not null)
            await installer.UninstallAsync(game, pluginId, ct: ct);
        File.Delete(SetupPath(game.Game.GameId, pluginId));
    }

    private static void EnsureGame(GameInstall game, ModRelease release)
    {
        if (!OperatingSystem.IsLinux() || ReleaseTarget.Normalize(release.TargetPlatform) != ReleaseTarget.Proton ||
            game.Game.GameId != release.GameId || game.PluginId != release.PluginId ||
            string.IsNullOrWhiteSpace(game.Game.SteamAppId) || !game.Game.SteamAppId.All(char.IsAsciiDigit) ||
            string.IsNullOrWhiteSpace(game.Game.ExeName) ||
            string.IsNullOrWhiteSpace(game.SteamRootPath) || !Directory.Exists(game.SteamRootPath) ||
            string.IsNullOrWhiteSpace(game.ProtonPrefixPath) || !Directory.Exists(game.ProtonPrefixPath))
            throw new InvalidOperationException("Proton setup needs a detected Steam game, its prefix, and a matching Proton release.");
    }

    private static void VerifyInstalledProxy(GameInstall game, ProtonLaunchConfig launch)
    {
        if (launch.UseInstalledWineDllProxy && launch.WineDllProxyFromDependency)
            throw new InvalidDataException("A Wine proxy cannot be both preinstalled and dependency-owned.");
        if (!launch.UseInstalledWineDllProxy && !launch.WineDllProxyFromDependency) return;
        if (launch.LaunchMode != "direct" || launch.WineDllOverrides.Count != 1 ||
            game.Game.ExeName is null)
            throw new InvalidDataException("An installed Wine proxy needs one direct loader override.");
        var finding = ProtonLoaderDetector.Detect(game.InstallPath, game.Game.ExeName);
        if (finding is null || finding.DllOverride != launch.WineDllOverrides[0])
            throw new InvalidOperationException(
                "The existing supported loader proxy is missing or no longer matches the game executable.");
        var proxyName = finding.DllOverride.Split('=')[0];
        var requestedProxy = launch.WineDllProxyPaths.TryGetValue(proxyName, out var mapped)
            ? mapped : proxyName + ".dll";
        if (!requestedProxy.Equals(finding.ProxyPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed Wine proxy path differs from the recognized loader.");
    }

    private static bool IsPrevious(string? current, SteamLaunchOptionsPlan plan) =>
        plan.WasPresent ? current == plan.Previous : current is null;

    private string SetupPath(string gameId, string pluginId) =>
        PathSafety.CombineContained(setupRoot, pluginId, gameId + ".json");

    private FileStream LockGame(GameInstall game, string pluginId)
    {
        Directory.CreateDirectory(setupRoot);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(setupRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockPath = PathSafety.CombineContained(setupRoot, pluginId + "-" + game.Game.GameId + ".lock");
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Another Proton setup for this game is already running.", ex);
        }
    }

    private static async Task SaveStateAsync(string path, ProtonSteamSetupState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = JsonSerializer.Serialize(state, JsonOptions);
        await AtomicJson.WriteWrappedAsync(path, payload);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task<ProtonSteamSetupState> LoadStateAsync(GameInstall game, string pluginId)
    {
        var path = SetupPath(game.Game.GameId, pluginId);
        if (!File.Exists(path))
            throw new InvalidOperationException("No Proton setup record exists for this mod.");
        var wrapped = AtomicJson.TryReadWrapped(await File.ReadAllTextAsync(path));
        if (wrapped is not { HashValid: true })
            throw new InvalidDataException("Proton setup record is damaged. It was left untouched for recovery.");
        var state = JsonSerializer.Deserialize<ProtonSteamSetupState>(wrapped.Payload, JsonOptions)
            ?? throw new InvalidDataException("Proton setup record has no data.");
        if (state.GameId != game.Game.GameId || state.PluginId != pluginId ||
            state.SteamAppId != game.Game.SteamAppId || state.InstallPath != game.InstallPath ||
            state.ProtonPrefixPath != game.ProtonPrefixPath)
            throw new InvalidDataException("Proton setup record belongs to a different game installation.");
        return state;
    }
}

public sealed record ProtonSteamSetupState(
    string GameId, string PluginId, string Version, string InstallPath,
    string SteamAppId, string ProtonPrefixPath, SteamLaunchOptionsPlan Plan,
    List<OwnedProtonConfigFile> ReloadedFiles);
