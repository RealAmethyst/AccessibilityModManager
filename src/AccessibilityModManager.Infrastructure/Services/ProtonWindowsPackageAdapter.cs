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
    ProtonLaunchAnalyzer.Plan launch) : IDisposable
{
    public string Path { get; } = path;
    public ModRelease Release { get; } = release;
    public ProtonLaunchAnalyzer.Plan Launch { get; } = launch;

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
    string? legacyTolkDllSha256 = null, string? tolkShimSha256 = null,
    string? legacyPrismDllSha256 = null)
{
    private static readonly HttpClient SharedHttpClient = new();
    private const string OfficialPrismDllSha256 =
        "7c7d09c8c7306e8e1a0c46d603ccb2a391c194c77843400c11b746e7dd56a467";
    // Official Prism 0.7.1 prism-windows-x64.zip release/bin/prism.dll.
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
        GameInstall game, ModRelease release, string packagePath, string protonExecutable,
        CancellationToken ct = default, IReadOnlySet<string>? installedModFiles = null)
    {
        if (!OperatingSystem.IsLinux() || ReleaseTarget.Normalize(release.TargetPlatform) != ReleaseTarget.Windows)
            throw new InvalidOperationException("Automatic Proton conversion needs a Windows release on Linux.");
        if (game.Game.GameId != release.GameId || game.PluginId != release.PluginId ||
            string.IsNullOrWhiteSpace(game.Game.EffectiveSteamAppId) ||
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
            var gameMachine = GameMachine(game);
            var dependencyFiles = await InspectFrameworkDependenciesAsync(game, manifest, directory, ct);
            foreach (var path in dependencyFiles.Keys.Where(proposed.ContainsKey))
                throw new InvalidDataException($"The package and a dependency both provide '{path}'.");
            var analysis = ProtonLaunchAnalyzer.Analyze(game.InstallPath, game.Game.ExeName, proposed,
                dependencyFiles, ProtonBuiltinModules.Read(protonExecutable, gameMachine),
                installedModFiles);
            logger.Information("Proton launch analysis for {PluginId}/{GameId}: {Plan}",
                release.PluginId, release.GameId, analysis.Description);
            if (manifest.PreInstall is not null || manifest.PostUninstall is not null ||
                manifest.PostInstall is not null &&
                (analysis.LauncherPath is null || !IsReplacedIfeoScript(manifest.PostInstall, extracted)))
                throw new InvalidOperationException(
                    "This Windows release has a lifecycle script without a verified Proton equivalent.");
            var runtimeMinimum = RequiredDotNetRuntime(proposed);
            if (runtimeMinimum is not null && gameMachine != Machine.Amd64)
                throw new InvalidOperationException(
                    "This mod needs the Windows .NET runtime, which the manager installs only for x64 games.");
            (string Version, string Sha512)? runtime = runtimeMinimum is null ? null
                : await ProtonWindowsDesktopRuntime.ResolveAsync(httpClient ?? SharedHttpClient,
                    runtimeMinimum, ct);
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
            if ((prismTargets.Length > 0 || tolkTargets.Length > 0) &&
                gameMachine != Machine.Amd64)
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

            var proxies = analysis.Proxies.Select(proxy => (proxy.Override, proxy.Path)).ToList();
            if (analysis.InstalledLoader is { } installed)
                proxies.Add((installed.DllOverride, installed.ProxyPath));
            // The mod's own launcher keeps writing its loader configuration on each start, as on
            // Windows, so the manager does not take ownership of those files.
            var launch = new ProtonLaunchConfig
            {
                SteamAppId = game.Game.EffectiveSteamAppId!,
                GameDisplayName = game.Game.DisplayName,
                GameExecutable = game.Game.ExeName.Replace('\\', '/'),
                LaunchMode = analysis.LauncherPath is null ? "direct" : "replaceExecutable",
                LauncherPath = analysis.LauncherPath,
                BridgeDirectory = prismAssets?.BridgeDirectory,
                WineDllOverrides = proxies.Select(proxy => proxy.Override).ToList(),
                UseInstalledWineDllProxy = analysis.InstalledLoader is not null,
                WineDllProxyFromDependency = analysis.Proxies.Any(proxy => proxy.FromDependency),
                WineDllProxyPaths = proxies
                    .Where(proxy => !proxy.Path.Equals(WineDllProxy.ModuleName(proxy.Override) + ".dll",
                        StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(proxy => WineDllProxy.ModuleName(proxy.Override), proxy => proxy.Path),
                WindowsDesktopRuntimeVersion = runtime?.Version,
                WindowsDesktopRuntimeSha512 = runtime?.Sha512
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
            return new AdaptedProtonPackage(directory, adapted, adaptedRelease, analysis);
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

    /// <summary>
    /// Reads every .NET runtime configuration the mod installs and returns the newest required
    /// runtime version, or null when no shared .NET runtime is needed. Mod components load into
    /// their host's runtime, so the newest requirement is the one the game process hosts.
    /// </summary>
    private static string? RequiredDotNetRuntime(IReadOnlyDictionary<string, string> proposed)
    {
        System.Version? required = null;
        var hosts = new List<System.Version>();
        foreach (var (target, source) in proposed)
        {
            if (!target.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (new FileInfo(source).Length > 1024 * 1024)
                throw new InvalidDataException($"'{target}' is too large.");
            using var document = JsonDocument.Parse(File.ReadAllText(source));
            if (!document.RootElement.TryGetProperty("runtimeOptions", out var options)) continue;
            var frameworks = new List<JsonElement>();
            if (options.TryGetProperty("framework", out var single)) frameworks.Add(single);
            if (options.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array)
                frameworks.AddRange(many.EnumerateArray());
            foreach (var framework in frameworks)
            {
                var name = framework.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                if (name is not ("Microsoft.NETCore.App" or "Microsoft.WindowsDesktop.App"))
                    throw new InvalidOperationException(
                        $"'{target}' needs the .NET framework '{name}', which the manager cannot install under Proton.");
                if (!framework.TryGetProperty("version", out var versionValue) ||
                    !System.Version.TryParse(versionValue.GetString(), out var version) || version.Build < 0)
                    throw new InvalidOperationException($"'{target}' has an unreadable .NET version.");
                version = new System.Version(version.Major, version.Minor, version.Build);
                if (required is null || version > required) required = version;
                // A configuration beside its own .exe starts a separate process with its own runtime.
                if (proposed.ContainsKey(target[..^".runtimeconfig.json".Length] + ".exe"))
                    hosts.Add(version);
            }
        }
        if (required is not null && hosts.Any(host =>
                host.Major != required.Major || host.Minor != required.Minor))
            throw new InvalidOperationException(
                "The mod's programs need more than one .NET runtime version. A Proton release is needed for this mod.");
        return required?.ToString(3);
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

    private static Machine GameMachine(GameInstall game)
    {
        var path = PathSafety.CombineContained(game.InstallPath,
            game.Game.ExeName!.Replace('\\', '/'));
        PathSafety.EnsureNoReparseTraversal(game.InstallPath, path, "game executable");
        return PeImage.Read(path)?.Machine ??
            throw new InvalidOperationException("The game executable is not a readable Windows program.");
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
                (cyberSleuthPrism || !IsLegacyPrism071(hash)))
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
        // An older Prism ABI keeps its own prism.dll interface through a verified adapter that
        // loads Prism 0.18.3 as prism-core.dll from the same folder.
        string? prism0173Adapter = null, prism071Adapter = null;
        for (var index = 0; index < prismTargets.Length; index++)
        {
            var (target, source) = prismTargets[index];
            var hash = prismHashes[target];
            string? adapter = null;
            if (cyberSleuthPrism)
                adapter = prism0173Adapter ??= await Prism0173Compatibility.ExtractAsync(extracted, ct);
            else if (IsLegacyPrism071(hash))
                adapter = prism071Adapter ??= await Prism071Compatibility.ExtractAsync(extracted, ct);
            if (adapter is null && hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(Path.Combine(extracted, Manifest.PackageFilesFolder), source)
                .Replace('\\', '/');
            replacements["files/" + relative] = adapter ?? bundle.WindowsPrismDll;
            if (adapter is null) continue;
            var folder = Path.GetDirectoryName(target.Replace('/', Path.DirectorySeparatorChar))?
                .Replace('\\', '/') ?? "";
            files.Add(new BridgeFile(bundle.WindowsPrismDll, $"files/amm-prism/core/{index}/prism-core.dll",
                folder.Length == 0 ? "prism-core.dll" : folder + "/prism-core.dll"));
        }
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

    /// <summary>The official Prism 0.7.1 Windows x64 release DLL, which uses PrismConfig version 1.</summary>
    private bool IsLegacyPrism071(string hash) =>
        hash.Equals(legacyPrismDllSha256 ?? LegacyPrismDllSha256, StringComparison.OrdinalIgnoreCase);

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
        var gameFolder = (Path.GetDirectoryName(game.Game.ExeName!.Replace('\\', '/')) ?? "")
            .Replace('\\', '/');
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
                // Only a DLL beside the game executable can change how the game starts.
                if (!relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    !(Path.GetDirectoryName(relative) ?? "").Replace('\\', '/')
                        .Equals(gameFolder, StringComparison.OrdinalIgnoreCase))
                    continue;
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
