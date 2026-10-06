using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed class AdaptedProtonPackage(string directory, string path, ModRelease release,
    ProtonLoaderDetector.Finding loader) : IDisposable
{
    public string Path { get; } = path;
    public ModRelease Release { get; } = release;
    public ProtonLoaderDetector.Finding Loader { get; } = loader;

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

/// <summary>
/// Converts a verified Windows package into a temporary Proton package. The published artifact
/// remains unchanged. Only setup work with a verified Proton equivalent is removed.
/// </summary>
public sealed class ProtonWindowsPackageAdapter(
    ILogger logger, HttpClient? httpClient = null, string? bridgeCacheRoot = null,
    PrismBridgeRelease? prismRelease = null, string? prismDllSha256 = null,
    string? legacyTolkDllSha256 = null, string? tolkShimSha256 = null)
{
    private static readonly HttpClient SharedHttpClient = new();
    private const string OfficialPrismDllSha256 =
        "7c7d09c8c7306e8e1a0c46d603ccb2a391c194c77843400c11b746e7dd56a467";
    private const string LegacyPrismDllSha256 =
        "3b02d1d9b2d44066604be1a6acf751c8c3c7f109c19035646aab21542083b02f";
    private const string CyberSleuthPrism0173DllSha256 =
        "287cd79e5d6cac5204605ba23e73bd043f48192ade54ddadf50ac4fcd9b94aae";
    private const string CyberSleuthPluginSha256 =
        "0197ba2894e9e5fef8bbd25931d3cfb3f0044bd85faf894590259a3024482156";
    // The audited native installer only writes the game's IFEO Debugger value. Steam's
    // reversible launch option replaces that operation under Proton; arbitrary EXEs do not.
    private const string IfeoOnlyInstallerSha256 =
        "64c7721bba7a2a1070b3fceb80e8b7b28c45fba223213a720a705d80af8f0c2e";
    private const string VerifiedLegacyTolkDllSha256 =
        "c4fb11d3ed236f27532c7ab8370ebde75133f322a069450f17e38d7548197225";
    private const string OfficialTolkShimSha256 =
        "7072a9e5cc6ccae8cfbcdd15f3e66ff5d55584bf9eb56c743ea5886fd5b14a8f";
    private const long MaxDependencyArchiveBytes = 512L * 1024 * 1024;
    private const long MaxLoaderDllBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<AdaptedProtonPackage> PrepareAsync(
        GameInstall game, ModRelease release, string packagePath, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || ReleaseTarget.Normalize(release.TargetPlatform) != ReleaseTarget.Windows)
            throw new InvalidOperationException("Automatic Proton conversion needs a Windows release on Linux.");
        if (game.Game.GameId != release.GameId || game.PluginId != release.PluginId ||
            string.IsNullOrWhiteSpace(game.Game.SteamAppId) ||
            string.IsNullOrWhiteSpace(game.Game.ExeName) ||
            string.IsNullOrWhiteSpace(game.ProtonPrefixPath) ||
            !Directory.Exists(game.ProtonPrefixPath))
            throw new InvalidOperationException("The Windows release needs a matching Steam game and Proton prefix.");

        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "amm-proton-adapt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var original = System.IO.Path.Combine(directory, "original.zip");
            await using (var source = File.OpenRead(packagePath))
            await using (var destination = File.Create(original))
                await source.CopyToAsync(destination, ct);

            await using (var stream = File.OpenRead(original))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The Windows package SHA-256 does not match the catalog release.");
                stream.Position = 0;
                var report = PluginPackageValidation.Validate(stream, release.PluginId,
                    release.GameId, release.Version, logger);
                if (!report.IsValid)
                    throw new InvalidDataException("The Windows package is invalid: " + string.Join(" ", report.Errors));
            }

            Manifest manifest;
            using (var archive = ZipFile.OpenRead(original))
            {
                var entry = archive.GetEntry(PluginPackageValidation.ManifestEntryName)!;
                if (entry.Length > 1024 * 1024)
                    throw new InvalidDataException("The package manifest is too large.");
                using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, true));
                manifest = new ManifestParser(logger).Parse(await reader.ReadToEndAsync(ct));
            }
            if (ReleaseTarget.Normalize(manifest.TargetPlatform) != ReleaseTarget.Windows)
                throw new InvalidOperationException("Automatic Proton conversion needs a Windows package.");

            LinuxDependencySupport.EnsureSupported(game.Game);
            EnsurePackageDependenciesMatchCatalog(game.Game, manifest);

            var extracted = System.IO.Path.Combine(directory, "extracted");
            await new SafeZipExtractor(logger).ExtractAsync(original, extracted, ct);
            var proposed = PlannedFiles(game.InstallPath, extracted, manifest);
            var reloaded = InspectReloadedPackage(game, proposed);
            if (manifest.PreInstall is not null || manifest.PostUninstall is not null ||
                manifest.PostInstall is not null &&
                (reloaded is null || !IsReplacedIfeoScript(manifest.PostInstall, extracted)))
                throw new InvalidOperationException(
                    "This Windows release has a lifecycle script without a verified Proton equivalent.");
            var tolkTargets = proposed.Where(pair =>
                    pair.Key.Equals("tolk.dll", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.EndsWith("/tolk.dll", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (tolkTargets.Length == 0 && proposed.Keys.Any(path =>
                    Path.GetFileName(path).StartsWith("nvdaControllerClient",
                        StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "This Windows package includes NVDA speech files without a verified Tolk bridge. " +
                    "Publish a Proton package that speaks through Orca.");
            var prismTargets = proposed.Where(pair =>
                    pair.Key.Equals("prism.dll", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.EndsWith("/prism.dll", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (prismTargets.Length > 0 && tolkTargets.Length > 0)
                throw new InvalidOperationException(
                    "This Windows package mixes Prism and Tolk speech files. " +
                    "Publish an explicit Proton package for that speech layout.");
            var finding = reloaded is null
                ? ProtonLoaderDetector.Detect(game.InstallPath, game.Game.ExeName, proposed)
                : new ProtonLoaderDetector.Finding("Reloaded II", "", "", "");
            var dependencyFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (finding is null)
            {
                dependencyFiles = await InspectFrameworkDependenciesAsync(game, manifest,
                    directory, ct);
                var withDependencies = new Dictionary<string, string>(proposed, StringComparer.OrdinalIgnoreCase);
                foreach (var (path, source) in dependencyFiles)
                    if (!withDependencies.TryAdd(path, source))
                        throw new InvalidDataException(
                            $"The package and a dependency both provide loader asset '{path}'.");
                finding = ProtonLoaderDetector.Detect(game.InstallPath, game.Game.ExeName, withDependencies);
            }
            if (finding is null) throw new InvalidOperationException(
                    "No supported bundled, installed, or dependency-provided BepInEx, MelonLoader, or DSCSModLoader proxy was found. " +
                    "A Proton release is needed for this mod.");
            var bundledProxy = reloaded is null && proposed.ContainsKey(finding.ProxyPath);
            var bundledMarker = proposed.ContainsKey(finding.MarkerPath);
            var dependencyProxy = dependencyFiles.ContainsKey(finding.ProxyPath);
            var dependencyMarker = dependencyFiles.ContainsKey(finding.MarkerPath);
            if (bundledProxy != bundledMarker || dependencyProxy != dependencyMarker ||
                bundledProxy && dependencyProxy)
                throw new InvalidOperationException(
                    "The package replaces only part of an existing loader. Publish an explicit Proton release.");
            var installedProxy = reloaded is null && !bundledProxy && !dependencyProxy;

            if ((prismTargets.Length > 0 || tolkTargets.Length > 0) &&
                !IsAmd64Game(game))
                throw new InvalidOperationException(
                    "The pinned Prism and Tolk Wine bridge is x64, but this game executable is not x64.");
            var prismAssets = prismTargets.Length == 0 && tolkTargets.Length == 0
                ? null : await PreparePrismBridgeAsync(game, prismTargets, tolkTargets, extracted, ct);
            if (prismAssets is not null)
                foreach (var asset in prismAssets.Files.Where(asset => asset.Target is not null))
                    if (proposed.ContainsKey(asset.Target!))
                        throw new InvalidOperationException(
                            $"The Windows package already installs '{asset.Target}'. " +
                            "Publish an explicit Proton package for this Prism layout.");

            var proxyName = WineDllProxy.ModuleName(finding.DllOverride);
            var launch = new ProtonLaunchConfig
            {
                SteamAppId = game.Game.SteamAppId!,
                GameDisplayName = game.Game.DisplayName,
                GameExecutable = game.Game.ExeName.Replace('\\', '/'),
                LaunchMode = reloaded is null ? "direct" : "replaceExecutable",
                LauncherPath = reloaded?.LauncherPath,
                BridgeDirectory = prismAssets?.BridgeDirectory,
                WineDllOverrides = reloaded is null ? [finding.DllOverride] : [],
                UseInstalledWineDllProxy = installedProxy,
                WineDllProxyFromDependency = dependencyProxy,
                WineDllProxyPaths = reloaded is not null || finding.ProxyPath.Equals(proxyName + ".dll", StringComparison.OrdinalIgnoreCase)
                    ? [] : new Dictionary<string, string> { [proxyName] = finding.ProxyPath },
                WindowsDesktopRuntimeVersion = reloaded is null ? null : ProtonWindowsDesktopRuntime.Version,
                ReloadedRoot = reloaded?.Root,
                ReloadedModId = reloaded?.ModId
            };

            var node = JsonSerializer.SerializeToNode(manifest, JsonOptions)!.AsObject();
            node["targetPlatform"] = ReleaseTarget.Proton;
            node["protonLaunch"] = JsonSerializer.SerializeToNode(launch, JsonOptions);
            node["postInstall"] = null;
            node["dependencies"] = JsonSerializer.SerializeToNode(
                DependencyTargeting.ForTarget(game.Game.Dependencies, ReleaseTarget.Proton), JsonOptions);
            if (prismAssets is not null)
            {
                var actions = node["installActions"]!.AsArray();
                foreach (var asset in prismAssets.Files.Where(asset => asset.Target is not null))
                    actions.Add(new JsonObject
                    {
                        ["type"] = "copyFile", ["source"] = asset.Entry["files/".Length..],
                        ["target"] = asset.Target
                    });
            }
            var adapted = System.IO.Path.Combine(directory, "adapted.zip");
            using (var input = ZipFile.OpenRead(original))
            using (var output = ZipFile.Open(adapted, ZipArchiveMode.Create))
            {
                if (prismAssets is not null)
                {
                    var originals = input.Entries.Select(entry => entry.FullName)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var asset in prismAssets.Files)
                        if (originals.Contains(asset.Entry))
                            throw new InvalidDataException(
                                $"The Windows package already contains bridge asset '{asset.Entry}'.");
                }
                var manifestEntry = output.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
                    await writer.WriteAsync(node.ToJsonString(JsonOptions).AsMemory(), ct);
                foreach (var entry in input.Entries.Where(e =>
                             !e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
                {
                    ct.ThrowIfCancellationRequested();
                    // The Windows AppConfig contains its author's C: game path. The prefix
                    // owner creates the correct Z: config and restores it on uninstall.
                    if (reloaded is not null && entry.FullName.Equals(
                            "files/" + reloaded.AppConfigPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var copy = output.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                    await using var source = prismAssets is not null &&
                        prismAssets.Replacements.TryGetValue(entry.FullName, out var replacement)
                            ? File.OpenRead(replacement) : entry.Open();
                    await using var destination = copy.Open();
                    await source.CopyToAsync(destination, ct);
                }
                if (prismAssets is not null)
                    foreach (var asset in prismAssets.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entry = output.CreateEntry(asset.Entry, CompressionLevel.Optimal);
                        await using var source = File.OpenRead(asset.Source);
                        await using var destination = entry.Open();
                        await source.CopyToAsync(destination, ct);
                    }
            }

            string adaptedHash;
            await using (var stream = File.OpenRead(adapted))
                adaptedHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            var adaptedRelease = new ModRelease
            {
                GameId = release.GameId, PluginId = release.PluginId, Version = release.Version,
                Channel = release.Channel, TargetPlatform = ReleaseTarget.Proton,
                Sha256 = adaptedHash
            };
            using (await new ProtonPackageInspector(logger).PrepareAsync(adapted, adaptedRelease, ct)) { }
            return new AdaptedProtonPackage(directory, adapted, adaptedRelease, finding);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private sealed record BridgeFile(string Source, string Entry, string? Target);
    private sealed record PrismAssets(string BridgeDirectory, List<BridgeFile> Files,
        Dictionary<string, string> Replacements);
    private sealed record ReloadedPackage(string Root, string ModId, string LauncherPath,
        string AppConfigPath);

    private static void EnsurePackageDependenciesMatchCatalog(GameDefinition game, Manifest manifest)
    {
        var catalog = DependencyTargeting.ForTarget(game.Dependencies, ReleaseTarget.Proton)
            .ToDictionary(dependency => dependency.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in manifest.Dependencies.Where(item =>
                     item.Required && !item.IsGameInstaller &&
                     DependencyTargeting.AppliesTo(item, ReleaseTarget.Proton)))
        {
            if (!catalog.TryGetValue(dependency.Id, out var supported) ||
                supported.Type != dependency.Type || !supported.Required ||
                dependency.Check?.FilePath is { } packageCheck &&
                !string.Equals(packageCheck, supported.Check?.FilePath,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Package dependency '{dependency.Id}' has no matching Proton catalog dependency.");

            if (dependency.Fix is null) continue;
            if (dependency.Fix.AutoInstall is not ExtractZipAutoInstall packageFix ||
                supported.Fix?.AutoInstall is not ExtractZipAutoInstall catalogFix ||
                !string.Equals(dependency.Fix.DownloadUrl, supported.Fix.DownloadUrl,
                    StringComparison.Ordinal) ||
                !string.Equals(packageFix.Sha256, catalogFix.Sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(packageFix.TargetDir, catalogFix.TargetDir,
                    StringComparison.Ordinal) ||
                !(packageFix.Blocklist ?? []).SequenceEqual(catalogFix.Blocklist ?? []))
                throw new InvalidDataException(
                    $"Package dependency '{dependency.Id}' differs from the verified Proton catalog.");
        }
    }

    private static ReloadedPackage? InspectReloadedPackage(GameInstall game,
        IReadOnlyDictionary<string, string> proposed)
    {
        const string root = "Reloaded-II";
        if (!proposed.Keys.Any(path => path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (!IsAmd64Game(game))
            throw new InvalidOperationException("This Reloaded II package requires an x64 game executable.");
        var launcher = proposed.Keys.Where(path =>
            !path.Contains('/') && path.EndsWith("_Launcher.exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (launcher.Length != 1 ||
            !proposed.ContainsKey(root + "/Loader/X64/Bootstrapper/Reloaded.Mod.Loader.Bootstrapper.dll") ||
            !proposed.ContainsKey(root + "/Loader/X64/Reloaded.Mod.Loader.dll"))
            throw new InvalidOperationException(
                "Reloaded II conversion needs one bundled launcher, bootstrapper and x64 loader.");
        using (var stream = File.OpenRead(proposed[launcher[0]]))
        using (var pe = new PEReader(stream))
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
                throw new InvalidOperationException("The Reloaded II launcher is not x64.");

        var appConfig = root + "/Apps/" + Path.GetFileName(game.Game.ExeName!).ToLowerInvariant() +
                        "/AppConfig.json";
        if (!proposed.TryGetValue(appConfig, out var appConfigPath))
            throw new InvalidOperationException("The Reloaded II package has no matching game configuration.");
        using var app = JsonDocument.Parse(File.ReadAllText(appConfigPath));
        if (!app.RootElement.TryGetProperty("EnabledMods", out var enabled) ||
            enabled.ValueKind != JsonValueKind.Array || enabled.GetArrayLength() != 1)
            throw new InvalidOperationException("Reloaded II conversion needs one enabled accessibility mod.");
        var modId = enabled[0].GetString();
        if (string.IsNullOrWhiteSpace(modId) ||
            modId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) ||
            !proposed.TryGetValue(root + "/Mods/" + modId + "/ModConfig.json", out var modConfigPath))
            throw new InvalidOperationException("The Reloaded II mod configuration is missing or ambiguous.");
        using var mod = JsonDocument.Parse(File.ReadAllText(modConfigPath));
        if (!mod.RootElement.TryGetProperty("ModId", out var declaredId) ||
            declaredId.GetString() != modId ||
            !mod.RootElement.TryGetProperty("ModDll", out var dllName) ||
            string.IsNullOrWhiteSpace(dllName.GetString()) ||
            !proposed.ContainsKey(root + "/Mods/" + modId + "/" + dllName.GetString()))
            throw new InvalidOperationException("The Reloaded II mod DLL does not match its configuration.");
        var runtimePath = proposed[root + "/Loader/X64/Reloaded.Mod.Loader.runtimeconfig.json"];
        using var runtime = JsonDocument.Parse(File.ReadAllText(runtimePath));
        var frameworks = runtime.RootElement.GetProperty("runtimeOptions").GetProperty("frameworks");
        var required = frameworks.EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!,
            item => item.GetProperty("version").GetString()!, StringComparer.Ordinal);
        if (required.Count != 2 ||
            !CompatibleRuntime("Microsoft.NETCore.App") ||
            !CompatibleRuntime("Microsoft.WindowsDesktop.App"))
            throw new InvalidOperationException(
                "The bundled Reloaded II loader needs a Windows Desktop Runtime that this manager has not pinned.");
        if (!proposed.Keys.Any(path => path.StartsWith(root + "/Mods/" + modId + "/", StringComparison.OrdinalIgnoreCase) &&
                                       path.EndsWith("/prism.dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The Reloaded II mod has no Prism build verified for Orca.");
        return new ReloadedPackage(root, modId, launcher[0], appConfig);

        bool CompatibleRuntime(string name) => required.TryGetValue(name, out var text) &&
            System.Version.TryParse(text, out var version) && version.Major == 9 &&
            version.Minor == 0 && version <= new System.Version(9, 0, 20);
    }

    private static bool IsReplacedIfeoScript(LifecycleScript script, string extracted)
    {
        if (!script.NeedsAdmin || !script.FailureFatal || !script.RunFromGameFolder ||
            !script.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var path = PathSafety.CombineContained(extracted, script.Executable.Replace('\\', '/'));
        return File.Exists(path) && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
            .Equals(IfeoOnlyInstallerSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAmd64Game(GameInstall game)
    {
        var path = PathSafety.CombineContained(game.InstallPath,
            game.Game.ExeName!.Replace('\\', '/'));
        PathSafety.EnsureNoReparseTraversal(game.InstallPath, path, "game executable");
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        return pe.PEHeaders.CoffHeader.Machine == Machine.Amd64;
    }

    private async Task<PrismAssets> PreparePrismBridgeAsync(
        GameInstall game, KeyValuePair<string, string>[] prismTargets,
        KeyValuePair<string, string>[] tolkTargets, string extracted, CancellationToken ct)
    {
        var prismHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, source) in prismTargets)
        {
            await using var stream = File.OpenRead(source);
            prismHashes[target] = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        }
        var cyberSleuthPrism = prismDllSha256 is null && prismHashes.Count > 0 &&
            prismHashes.Values.All(hash => hash.Equals(
                CyberSleuthPrism0173DllSha256, StringComparison.OrdinalIgnoreCase));
        if (cyberSleuthPrism && !IsCyberSleuthPrismConsumer(game, prismTargets))
            throw new InvalidOperationException(
                "This Prism 0.17.3 package has no verified Orca compatibility adapter. " +
                "Publish a Proton release using Prism 0.18.3 and its current C ABI.");
        var release = prismRelease ?? PrismBridgeRelease.V0183X64;
        var expected = prismDllSha256 ?? (cyberSleuthPrism
            ? CyberSleuthPrism0173DllSha256 : OfficialPrismDllSha256);
        foreach (var (target, hash) in prismHashes)
        {
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase) &&
                (cyberSleuthPrism || prismDllSha256 is not null ||
                 !hash.Equals(LegacyPrismDllSha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Prism at '{target}' is not the pinned build with a verified Wine bridge. " +
                    "Publish a Proton package with a matching Prism bridge for Orca.");
        }
        foreach (var (target, source) in tolkTargets)
        {
            await using var stream = File.OpenRead(source);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (!hash.Equals(legacyTolkDllSha256 ?? VerifiedLegacyTolkDllSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Tolk at '{target}' is not the verified legacy build. " +
                    "Publish a Proton package with a matching Orca speech bridge.");
        }
        if (release.Architecture != "x64")
            throw new InvalidOperationException("Automatic Prism bridging currently needs an x64 release.");
        var cache = bridgeCacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibilityModManager", "prism-bridges");
        var bundle = await new PrismBridgeProvisioner(httpClient ?? SharedHttpClient, cache)
            .ProvisionAsync(release, ct);
        await using (var stream = File.OpenRead(bundle.WindowsPrismDll))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            var bundleExpected = cyberSleuthPrism ? OfficialPrismDllSha256 : expected;
            if (!hash.Equals(bundleExpected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The pinned Prism archive does not match this Windows package.");
        }
        if (tolkTargets.Length > 0)
        {
            await using var stream = File.OpenRead(bundle.WindowsTolkDll);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (!hash.Equals(tolkShimSha256 ?? OfficialTolkShimSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The pinned Prism archive has an unexpected Tolk shim.");
        }

        var bridgeDirectory = $".amm/prism-{release.Version}-{release.Architecture}/wine";
        var files = new List<BridgeFile>();
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var compatibilityDll = cyberSleuthPrism
            ? await Prism0173Compatibility.ExtractAsync(extracted, ct) : null;
        foreach (var (_, source) in prismTargets)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source)));
            if (compatibilityDll is not null ||
                !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(Path.Combine(extracted, Manifest.PackageFilesFolder), source)
                    .Replace('\\', '/');
                replacements["files/" + relative] = compatibilityDll ?? bundle.WindowsPrismDll;
            }
        }
        if (compatibilityDll is not null)
            files.Add(new BridgeFile(bundle.WindowsPrismDll,
                "files/amm-prism/prism-core.dll", "resources/plugins/prism-core.dll"));
        if (tolkTargets.Length > 0)
        {
            files.Add(new BridgeFile(bundle.WindowsPrismDll,
                "files/amm-prism/prism.dll", "prism.dll"));
            var filesDirectory = Path.Combine(extracted, Manifest.PackageFilesFolder);
            foreach (var (_, source) in tolkTargets)
            {
                var relative = Path.GetRelativePath(filesDirectory, source).Replace('\\', '/');
                replacements["files/" + relative] = bundle.WindowsTolkDll;
            }
        }
        var placeholderDirectories = prismTargets.Select(pair =>
                Path.GetDirectoryName(pair.Key.Replace('/', Path.DirectorySeparatorChar))?
                    .Replace('\\', '/') ?? "")
            .Concat(tolkTargets.Length > 0 ? [""] : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var directoryIndex = 0; directoryIndex < placeholderDirectories.Length; directoryIndex++)
        {
            var directory = placeholderDirectories[directoryIndex];
            foreach (var name in release.BridgeNames)
            {
                var placeholder = name + ".dll";
                files.Add(new BridgeFile(
                    Path.Combine(bundle.WindowsPlaceholdersDirectory, placeholder),
                    $"files/amm-prism/placeholders/{directoryIndex}/{placeholder}",
                    directory.Length == 0 ? placeholder : directory + "/" + placeholder));
            }
        }
        foreach (var name in release.BridgeNames)
        {
            var host = name + ".dll.so";
            files.Add(new BridgeFile(
                Path.Combine(bundle.HostModulesDirectory, host),
                "files/" + bridgeDirectory + "/" + host, bridgeDirectory + "/" + host));
        }
        foreach (var source in Directory.EnumerateFiles(bundle.NoticesDirectory, "*",
                     SearchOption.AllDirectories))
            files.Add(new BridgeFile(source,
                "notices/Prism/" + Path.GetRelativePath(bundle.NoticesDirectory, source)
                    .Replace('\\', '/'), null));
        return new PrismAssets(bridgeDirectory, files, replacements);
    }

    private static bool IsCyberSleuthPrismConsumer(GameInstall game,
        KeyValuePair<string, string>[] prismTargets)
    {
        if (game.Game.GameId != "dscs" || game.PluginId != "amethyst" ||
            prismTargets.Length != 1 ||
            !prismTargets[0].Key.Equals("resources/plugins/prism.dll",
                StringComparison.OrdinalIgnoreCase))
            return false;
        var plugin = Path.Combine(Path.GetDirectoryName(prismTargets[0].Value)!,
            "CyberSleuthAccessibility.dll");
        return File.Exists(plugin) &&
               Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(plugin)))
                   .Equals(CyberSleuthPluginSha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, string>> InspectFrameworkDependenciesAsync(
        GameInstall game, Manifest manifest, string staging, CancellationToken ct)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dependencies = DependencyTargeting.ForTarget(game.Game.Dependencies, ReleaseTarget.Proton)
            .Where(dep => dep.Required && dep.Type == "framework" &&
                          dep.Fix?.AutoInstall is ExtractZipAutoInstall)
            .ToArray();
        for (var index = 0; index < dependencies.Length; index++)
        {
            var dependency = dependencies[index];
            var fix = (ExtractZipAutoInstall)dependency.Fix!.AutoInstall!;
            var blocks = fix.Blocklist ?? [];
            var packageDependency = manifest.Dependencies.FirstOrDefault(item =>
                item.Id == dependency.Id);
            if (packageDependency is not null &&
                (packageDependency.Fix?.AutoInstall is not ExtractZipAutoInstall packageFix ||
                 !string.Equals(packageDependency.Fix.DownloadUrl, dependency.Fix.DownloadUrl,
                     StringComparison.Ordinal) ||
                 !string.Equals(packageFix.Sha256, fix.Sha256, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(packageFix.TargetDir, fix.TargetDir, StringComparison.Ordinal) ||
                 (packageFix.Blocklist ?? []).Count != blocks.Count ||
                 (packageFix.Blocklist ?? []).Where((item, i) => item != blocks[i]).Any()))
                continue;
            if (blocks.Count > 0) continue;
            if (string.IsNullOrWhiteSpace(dependency.Fix.DownloadUrl) ||
                fix.Sha256.Length != 64 || !fix.Sha256.All(char.IsAsciiHexDigit))
                continue;
            var url = new Uri(dependency.Fix.DownloadUrl);
            UrlValidator.RequireHttps(url, "framework dependency");
            var archivePath = System.IO.Path.Combine(staging, $"dependency-{index}.zip");
            await DownloadDependencyAsync(url, archivePath, ct);
            await using (var stream = File.OpenRead(archivePath))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                if (!hash.Equals(fix.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Framework dependency '{dependency.Id}' has a SHA-256 mismatch.");
            }
            var targetDir = PathSafety.NormalizeRelativeDir(fix.TargetDir,
                "framework dependency targetDir");
            using var archive = ZipFile.OpenRead(archivePath);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains(':') ||
                    name.Split('/').Any(part => part == ".."))
                    throw new InvalidDataException(
                        $"Framework dependency '{dependency.Id}' has an unsafe ZIP path.");
                if (name.EndsWith('/')) continue;
                var path = PathSafety.CombineContained(game.InstallPath,
                    targetDir.Length == 0 ? name : targetDir + "/" + name);
                var relative = System.IO.Path.GetRelativePath(game.InstallPath, path).Replace('\\', '/');
                if (!seen.Add(relative))
                    throw new InvalidDataException(
                        $"Framework dependency '{dependency.Id}' has a duplicate ZIP path.");
                if (!IsLoaderAsset(relative)) continue;
                if (entry.Length == 0 || entry.Length > MaxLoaderDllBytes)
                    throw new InvalidDataException(
                        $"Framework dependency '{dependency.Id}' has an invalid loader DLL size.");
                var stagedPath = System.IO.Path.Combine(staging, "dependency-files", index.ToString(),
                    Guid.NewGuid().ToString("N") + ".dll");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(stagedPath)!);
                await using (var source = entry.Open())
                await using (var dest = File.Create(stagedPath))
                {
                    var buffer = new byte[81920];
                    long size = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) != 0)
                    {
                        size += read;
                        if (size > MaxLoaderDllBytes)
                            throw new InvalidDataException(
                                $"Framework dependency '{dependency.Id}' has an oversized loader DLL.");
                        await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }
                if (!found.TryAdd(relative, stagedPath))
                    throw new InvalidDataException(
                        $"More than one dependency provides loader asset '{relative}'.");
            }
        }
        return found;
    }

    private async Task DownloadDependencyAsync(Uri url, string path, CancellationToken ct)
    {
        using var response = await (httpClient ?? SharedHttpClient)
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps ||
            response.Content.Headers.ContentLength > MaxDependencyArchiveBytes)
            throw new InvalidDataException("Framework dependency did not resolve to a bounded HTTPS download.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            size += read;
            if (size > MaxDependencyArchiveBytes)
                throw new InvalidDataException("Framework dependency archive exceeds the size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static bool IsLoaderAsset(string path) =>
        path.EndsWith("/version.dll", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("version.dll", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/winhttp.dll", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/freetype.dll", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("freetype.dll", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/DSCSModLoader.dll", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("DSCSModLoader.dll", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/BepInEx/core/BepInEx.dll", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("BepInEx/core/BepInEx.dll", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/MelonLoader/net6/MelonLoader.dll", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/MelonLoader/net35/MelonLoader.dll", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("MelonLoader/net6/MelonLoader.dll", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("MelonLoader/net35/MelonLoader.dll", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> PlannedFiles(string gameDirectory,
        string extractedDirectory, Manifest manifest)
    {
        var filesDirectory = System.IO.Path.Combine(extractedDirectory, Manifest.PackageFilesFolder);
        var proposed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in manifest.InstallActions)
        {
            switch (action)
            {
                case CopyFileAction copy:
                    Add(copy.Source, copy.Target);
                    break;
                case ReplaceFileAction replace:
                    Add(replace.Source, replace.Target);
                    break;
                case CopyFolderAction folder:
                    var sourceFolder = PathSafety.CombineContained(filesDirectory,
                        folder.SourceDir.Replace('\\', '/'));
                    foreach (var source in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(sourceFolder, source).Replace('\\', '/');
                        var target = string.IsNullOrEmpty(folder.TargetDir)
                            ? relative : folder.TargetDir.Replace('\\', '/').TrimEnd('/') + "/" + relative;
                        AddPath(source, target);
                    }
                    break;
            }
        }
        return proposed;

        void Add(string source, string target) => AddPath(
            PathSafety.CombineContained(filesDirectory, source.Replace('\\', '/')), target);

        void AddPath(string source, string target)
        {
            var targetPath = PathSafety.CombineContained(gameDirectory, target.Replace('\\', '/'));
            var relative = Path.GetRelativePath(gameDirectory, targetPath).Replace('\\', '/');
            if (!proposed.TryAdd(relative, source))
                throw new InvalidDataException($"More than one package action writes '{relative}'.");
        }
    }
}
