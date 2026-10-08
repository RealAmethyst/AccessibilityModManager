using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed record XivLauncherSetupState(string GameId, string PluginId, string SteamAppId,
    string SteamRoot, string PayloadRoot, string AccountConfigPath, XivLauncherConfig Plugin,
    XivLauncherSettings.Registration Registration, string? PreviousCompatibility,
    SteamLaunchOptionsPlan LaunchOptions, string? OriginalDalamud, bool Ready);

/// <summary>Owns one game's XLM selection and one Dalamud plugin registration, with a durable recovery journal.</summary>
public sealed class XivLauncherSetup(InstallerEngine installer, IReceiptStore receipts,
    HttpClient httpClient, ILogger logger, string stateRoot, string? homeOverride = null,
    Action? ensureClosed = null, Action<string>? configureFrontend = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private void EnsureClosed() => (ensureClosed ?? XivLauncherFiles.EnsureClosed)();
    private static string[] MappingPath(string appId) => ["InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping", appId];
    private static string Mapping(string appId) => $"\"{appId}\"\n{{\n\"name\" \"xlm\"\n\"config\" \"\"\n\"priority\" \"250\"\n}}";

    public async Task InstallAsync(GameInstall game, ModRelease release, string packagePath,
        string accountConfigPath, bool update, IDependencyHost? host = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        EnsureClosed();
        var paths = XivLauncherPaths.Resolve(game, homeOverride);
        var appId = RequireAppId(game);
        var statePath = StatePath(game);
        using var operationLock = Lock();
        var manifest = await InspectAsync(game, release, packagePath, ct);
        var plugin = manifest.XivLauncher!;
        var dependency = RequireDependency(game, manifest);
        var config = XivLauncherFiles.Read(paths.GlobalSteamConfig)
            ?? throw new InvalidOperationException("Steam's configuration file is missing. Open Steam once before installing.");
        var accountText = XivLauncherFiles.Read(accountConfigPath)
            ?? throw new InvalidOperationException("The selected Steam account settings are missing.");
        PathSafety.EnsureContained(paths.SteamRoot, accountConfigPath, "Steam account settings");
        var state = await LoadAsync(game, paths);
        if (state is not null)
        {
            if (state.AccountConfigPath != accountConfigPath ||
                JsonSerializer.Serialize(state.Plugin, JsonOptions) != JsonSerializer.Serialize(plugin, JsonOptions))
                throw new InvalidOperationException("An update cannot change this plugin's registration, account, or bridge path. Uninstall the previous setup first.");
            if (!state.Ready)
            {
                await RecoverAsync(game, paths, state);
                state = null;
                config = XivLauncherFiles.Read(paths.GlobalSteamConfig)!;
                accountText = XivLauncherFiles.Read(accountConfigPath)!;
            }
        }
        if (state is not null)
        {
            VerifySettings(paths, state);
            if (!update) throw new InvalidOperationException("This XIVLauncher mod is already installed. Use Update.");
            configureFrontend?.Invoke(paths.SteamRoot);
            await installer.UpdateAsync(PayloadGame(game, paths), release, packagePath, dependencyHost: host, ct: ct);
            await RepairSpeechAsync(game, paths, plugin);
            return;
        }
        if (await receipts.LoadAsync(game.Game.GameId, game.PluginId) is not null)
            throw new InvalidOperationException("An installed mod already exists without this XIVLauncher setup record. Uninstall that release first.");
        if (Directory.Exists(paths.PayloadRoot) && Directory.EnumerateFileSystemEntries(paths.PayloadRoot).Any())
            throw new InvalidOperationException("The proposed plugin folder already contains untracked files. They were left untouched.");
        // One owner per Steam app. Never silently replace another managed launcher or plugin setup.
        foreach (var file in Directory.EnumerateFiles(stateRoot, "*.json", SearchOption.AllDirectories))
        {
            if (file == statePath) continue;
            var other = ReadState(await File.ReadAllTextAsync(file, ct));
            if (other.SteamRoot == paths.SteamRoot && other.SteamAppId == appId)
                throw new InvalidOperationException("Another mod already owns this game's XIVLauncher Steam setup. Remove it first.");
        }
        var existingMapping = SteamLocalConfigEditor.ReadBlock(config, MappingPath(appId));
        var originalDalamud = XivLauncherFiles.Read(paths.ConfigPath);
        var registration = XivLauncherSettings.Install(originalDalamud, paths.WinePath(plugin.PluginAssembly), plugin);
        var previousOptions = SteamLocalConfigEditor.Read(accountText, appId);
        var options = plugin.BridgeDirectory is null ? "" :
            "WINEDLLPATH=" + Quote(PathSafety.CombineContained(paths.PayloadRoot, plugin.BridgeDirectory)) +
            "${WINEDLLPATH:+:\"$WINEDLLPATH\"} %command%";
        var plan = new SteamLaunchOptionsPlan(previousOptions ?? "", options)
        {
            WasPresent = previousOptions is not null,
            AppWasPresent = SteamLocalConfigEditor.HasApp(accountText, appId)
        };
        state = new(game.Game.GameId, game.PluginId, appId, paths.SteamRoot, paths.PayloadRoot,
            accountConfigPath, plugin, registration.Registration, existingMapping, plan, originalDalamud, false);
        await SaveAsync(statePath, state);
        try
        {
            await new XlmInstaller(httpClient, logger, ensureClosed).EnsureInstalledAsync(paths.SteamRoot, dependency, progress, ct);
            configureFrontend?.Invoke(paths.SteamRoot);
            EnsureClosed();
            Directory.CreateDirectory(paths.PayloadRoot);
            progress?.Report(new ProgressInfo { Percentage = 100, StatusText = "Installing the verified XIVLauncher plugin." });
            await installer.InstallAsync(PayloadGame(game, paths), release, packagePath, dependencyHost: host, ct: ct);
            await RepairSpeechAsync(game, paths, plugin);
            EnsureClosed();
            var installedAssembly = PathSafety.CombineContained(paths.PayloadRoot, plugin.PluginAssembly);
            if (!File.Exists(installedAssembly) || !File.Exists(Path.ChangeExtension(installedAssembly, ".json")))
                throw new InvalidDataException("The installed package did not provide its declared plugin DLL and JSON.");
            if (plugin.BridgeDirectory is not null && !Directory.EnumerateFiles(
                    PathSafety.CombineContained(paths.PayloadRoot, plugin.BridgeDirectory), "*.dll.so").Any())
                throw new InvalidDataException("The installed plugin's Wine speech bridge is missing.");
            Change(paths.ConfigPath, originalDalamud, registration.Json);
            Change(paths.GlobalSteamConfig, config,
                SteamLocalConfigEditor.SetBlock(config, MappingPath(appId), Mapping(appId)));
            Change(accountConfigPath, accountText, SteamLocalConfigEditor.Install(accountText, appId, plan));
            await SaveAsync(statePath, state with { Ready = true, OriginalDalamud = null });
        }
        catch
        {
            // Recovery is idempotent. If a concurrent user edit prevents it, keep the journal
            // and installed files so Retry/Uninstall can finish without discarding evidence.
            await RecoverAsync(game, paths, state);
            throw;
        }
    }

    public async Task UninstallAsync(GameInstall game, CancellationToken ct = default)
    {
        EnsureClosed();
        using var operationLock = Lock();
        var paths = XivLauncherPaths.Resolve(game, homeOverride);
        var state = await LoadAsync(game, paths) ?? throw new InvalidOperationException("The XIVLauncher setup record is missing. Existing files were left untouched.");
        if (!state.Ready) { await RecoverAsync(game, paths, state); return; }
        VerifySettings(paths, state);
        var current = XivLauncherFiles.Read(paths.ConfigPath)!;
        var removed = XivLauncherSettings.Remove(current, state.Registration);
        // Persist the removal state before modifying settings or plugin files. Recovery can
        // complete a partially finished uninstall without restoring the old whole config.
        await SaveAsync(StatePath(game), state with { Ready = false, OriginalDalamud = removed });
        Change(paths.ConfigPath, current, removed);
        RestoreSteam(paths, state);
        await installer.UninstallAsync(PayloadGame(game, paths), game.PluginId, ct: ct);
        File.Delete(StatePath(game));
    }

    public async Task EnsureReadyAsync(GameInstall game)
    {
        var paths = XivLauncherPaths.Resolve(game, homeOverride);
        var state = await LoadAsync(game, paths) ?? throw new InvalidOperationException("This mod's XIVLauncher setup record is missing.");
        if (!state.Ready) throw new InvalidOperationException("XIVLauncher setup was interrupted. Close Steam and reinstall or uninstall this release to recover.");
        VerifySettings(paths, state);
        await RepairSpeechAsync(game, paths, state.Plugin);
        configureFrontend?.Invoke(paths.SteamRoot);
    }

    private async Task RepairSpeechAsync(GameInstall game, XivLauncherPaths paths, XivLauncherConfig plugin)
    {
        if (plugin.BridgeDirectory is null) return;
        var receipt = await receipts.LoadAsync(game.Game.GameId, game.PluginId)
            ?? throw new InvalidDataException("The installed plugin's receipt is missing. Reinstall the mod before playing.");
        XivLauncherSpeechRepair.Ensure(paths.PayloadRoot, plugin, receipt,
            Path.Combine(stateRoot, "backups", "speech"), ensureClosed);
    }

    private void VerifySettings(XivLauncherPaths paths, XivLauncherSetupState state)
    {
        XlmInstaller.Verify(paths.SteamRoot);
        var config = XivLauncherFiles.Read(paths.GlobalSteamConfig) ?? throw new InvalidDataException("Steam settings are missing.");
        if (!SteamLocalConfigEditor.BlocksEqual(SteamLocalConfigEditor.ReadBlock(config, MappingPath(state.SteamAppId)), Mapping(state.SteamAppId)))
            throw new InvalidOperationException("This game's Steam compatibility tool changed. Restore XLCore [XLM] or uninstall the managed setup before continuing.");
        var account = XivLauncherFiles.Read(state.AccountConfigPath) ?? throw new InvalidDataException("Steam account settings are missing.");
        state.LaunchOptions.Restore(SteamLocalConfigEditor.Read(account, state.SteamAppId) ?? "");
        XivLauncherSettings.Verify(XivLauncherFiles.Read(paths.ConfigPath) ?? throw new InvalidDataException("Dalamud configuration is missing."), state.Registration);
    }

    private async Task RecoverAsync(GameInstall game, XivLauncherPaths paths, XivLauncherSetupState state)
    {
        EnsureClosed();
        var current = XivLauncherFiles.Read(paths.ConfigPath);
        if (current != state.OriginalDalamud && current is not null && !XivLauncherSettings.IsAbsent(current, state.Registration))
        {
            var restored = XivLauncherSettings.Remove(current, state.Registration);
            // Preserve settings unrelated to this registration, including settings changed since a failure.
            Change(paths.ConfigPath, current, restored);
        }
        RestoreSteam(paths, state);
        if (await receipts.LoadAsync(game.Game.GameId, game.PluginId) is not null)
            await installer.UninstallAsync(PayloadGame(game, paths), game.PluginId);
        File.Delete(StatePath(game));
    }

    private void RestoreSteam(XivLauncherPaths paths, XivLauncherSetupState state)
    {
        var global = XivLauncherFiles.Read(paths.GlobalSteamConfig) ?? throw new InvalidDataException("Steam configuration is missing.");
        var mapping = SteamLocalConfigEditor.ReadBlock(global, MappingPath(state.SteamAppId));
        if (!SteamLocalConfigEditor.BlocksEqual(mapping, state.PreviousCompatibility))
        {
            if (!SteamLocalConfigEditor.BlocksEqual(mapping, Mapping(state.SteamAppId)))
                throw new InvalidOperationException("Steam's compatibility choice changed. The recovery record was preserved.");
            Change(paths.GlobalSteamConfig, global, SteamLocalConfigEditor.SetBlock(global,
                MappingPath(state.SteamAppId), state.PreviousCompatibility));
        }
        var account = XivLauncherFiles.Read(state.AccountConfigPath) ?? throw new InvalidDataException("Steam account settings are missing.");
        var options = SteamLocalConfigEditor.Read(account, state.SteamAppId);
        if ((options ?? "") == state.LaunchOptions.Previous && (options is not null) == state.LaunchOptions.WasPresent) return;
        Change(state.AccountConfigPath, account, SteamLocalConfigEditor.Restore(account, state.SteamAppId, state.LaunchOptions));
    }

    private void Change(string path, string? previous, string? next) => XivLauncherFiles.Write(path, previous, next,
        Path.Combine(stateRoot, "backups"), ensureClosed);

    private FileStream Lock()
    {
        Directory.CreateDirectory(stateRoot);
        try { return new FileStream(Path.Combine(stateRoot, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another XIVLauncher setup operation is already running."); }
    }

    private string StatePath(GameInstall game)
    {
        PathSafety.EnsureSafeId(game.PluginId, "Plugin ID");
        PathSafety.EnsureSafeId(game.Game.GameId, "Game ID");
        return PathSafety.CombineContained(stateRoot, game.PluginId, game.Game.GameId + ".json");
    }

    private static string RequireAppId(GameInstall game)
    {
        var appId = game.Game.EffectiveSteamAppId;
        if (appId is not ("39210" or "312060"))
            throw new InvalidOperationException("XIVLauncher setup requires the Steam ID for FINAL FANTASY XIV Online (39210) or its Free Trial (312060).");
        return appId;
    }

    private static GameInstall PayloadGame(GameInstall game, XivLauncherPaths paths) => new()
    {
        Game = new GameDefinition
        {
            GameId = game.Game.GameId, DisplayName = game.Game.DisplayName,
            Dependencies = game.Game.Dependencies.Where(d => d.Fix?.Xlm is null).ToList()
        },
        PluginId = game.PluginId, InstallPath = paths.PayloadRoot, IsValid = true
    };

    private async Task<XivLauncherSetupState?> LoadAsync(GameInstall game, XivLauncherPaths paths)
    {
        var path = StatePath(game);
        if (!File.Exists(path)) return null;
        var state = ReadState(await File.ReadAllTextAsync(path));
        if (state.GameId != game.Game.GameId || state.PluginId != game.PluginId ||
            state.SteamAppId != RequireAppId(game) || state.SteamRoot != paths.SteamRoot || state.PayloadRoot != paths.PayloadRoot)
            throw new InvalidDataException("The XIVLauncher setup record belongs to a different installation.");
        PathSafety.EnsureContained(paths.SteamRoot, state.AccountConfigPath, "recorded Steam account");
        return state;
    }

    private static XivLauncherSetupState ReadState(string json)
    {
        var wrapped = AtomicJson.TryReadWrapped(json);
        if (wrapped is not { HashValid: true }) throw new InvalidDataException("The XIVLauncher setup record is damaged and was preserved for recovery.");
        return JsonSerializer.Deserialize<XivLauncherSetupState>(wrapped.Payload, JsonOptions)
            ?? throw new InvalidDataException("The XIVLauncher setup record is empty.");
    }

    private static async Task SaveAsync(string path, XivLauncherSetupState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicJson.WriteWrappedAsync(path, JsonSerializer.Serialize(state, JsonOptions));
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static Dependency RequireDependency(GameInstall game, Manifest manifest)
    {
        var dependencies = DependencyTargeting.ForTarget(game.Game.Dependencies, ReleaseTarget.XivLauncher)
            .Where(d => d.Fix?.Xlm is not null).ToArray();
        if (dependencies.Length != 1) throw new InvalidDataException("Add exactly one XIVLauncher on Linux (XLM) dependency to this game's author entry.");
        var dependency = dependencies[0];
        var bundled = manifest.Dependencies.Where(d => d.Fix?.Xlm is not null).ToArray();
        if (bundled.Length != 1 || bundled[0].Fix?.DownloadUrl != dependency.Fix?.DownloadUrl ||
            bundled[0].Fix?.Xlm?.Sha256 != dependency.Fix?.Xlm?.Sha256)
            throw new InvalidDataException("The package's XLM dependency does not match the catalog. Rebuild the package after changing this dependency.");
        return dependency;
    }

    private static async Task<Manifest> InspectAsync(GameInstall game, ModRelease release, string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The XIVLauncher package failed SHA-256 verification.");
        stream.Position = 0;
        var report = PluginPackageValidation.Validate(stream, game.PluginId, game.Game.GameId, release.Version, Serilog.Log.Logger);
        if (!report.IsValid) throw new InvalidDataException(string.Join(" ", report.Errors));
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("The package manifest is missing.");
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("The package manifest is too large.");
        using var reader = new StreamReader(entry.Open());
        var manifest = new ManifestParser(Serilog.Log.Logger).Parse(await reader.ReadToEndAsync(ct));
        if (manifest.TargetPlatform != ReleaseTarget.XivLauncher || release.TargetPlatform != ReleaseTarget.XivLauncher)
            throw new InvalidDataException("Use a package explicitly built for XIVLauncher on Linux. Windows installer packages cannot register this Linux plugin.");
        return manifest;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
