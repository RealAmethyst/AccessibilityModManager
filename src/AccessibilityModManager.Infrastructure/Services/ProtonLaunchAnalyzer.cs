using System.Reflection.PortableExecutable;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Works out how a Windows mod's code starts, from the files it installs, so Proton can start it
/// the way Windows would. Windows loads a DLL beside the game before its own copy; Wine prefers a
/// built-in module, so each such DLL needs a native-first override; a DLL the game imports or
/// already ships loads under Wine without one. A bundled launcher that starts
/// a process and writes a module into it replaces the game executable, as a Windows redirect does.
/// Code with no recognizable way to load is refused rather than launched silently without the mod.
/// </summary>
public static class ProtonLaunchAnalyzer
{
    public sealed record Proxy(string Module, string Path, bool FromDependency)
    {
        public string Override => Module + "=n,b";
    }

    public sealed record Plan(string? LauncherPath, IReadOnlyList<Proxy> Proxies,
        IReadOnlyList<string> GameLoadedDlls, bool ReplacesGameFiles,
        ProtonLoaderDetector.Finding? InstalledLoader)
    {
        public string Description => LauncherPath is not null
            ? "launcher " + LauncherPath
            : Proxies.Count > 0
                ? "native-first " + string.Join(", ", Proxies.Select(proxy => proxy.Path))
                : GameLoadedDlls.Count > 0
                    ? "loaded by the game " + string.Join(", ", GameLoadedDlls)
                    : InstalledLoader is not null
                        ? "installed " + InstalledLoader.Loader
                        : ReplacesGameFiles ? "replaced game files" : "data files only";
    }

    // The manifest format accepts at most 16 Wine DLL rules.
    private const int MaxProxies = 16;
    private static readonly string[] ProcessCreation =
        ["CreateProcessW", "CreateProcessA", "CreateProcessAsUserW", "CreateProcessAsUserA"];
    private static readonly string[] RemoteExecution =
        ["CreateRemoteThread", "CreateRemoteThreadEx", "QueueUserAPC", "SetThreadContext"];

    public static Plan Analyze(string gameDirectory, string gameExecutable,
        IReadOnlyDictionary<string, string> packageFiles,
        IReadOnlyDictionary<string, string> dependencyFiles,
        IReadOnlySet<string> builtinModules, IReadOnlySet<string>? installedModFiles = null)
    {
        // On update, files the mod added earlier are on disk but are not part of the game.
        bool GameShips(string relative) => installedModFiles?.Contains(relative) != true &&
            File.Exists(PathSafety.CombineContained(gameDirectory, relative));
        var gameRelative = gameExecutable.Replace('\\', '/');
        var gamePath = PathSafety.CombineContained(gameDirectory, gameRelative);
        PathSafety.EnsureNoReparseTraversal(gameDirectory, gamePath, "game executable");
        var game = PeImage.Read(gamePath) ??
            throw new InvalidOperationException("The game executable is not a readable Windows program.");
        if (game.Machine is not (Machine.Amd64 or Machine.I386))
            throw new InvalidOperationException("Only x86 and x64 Windows games can be set up for Proton.");
        var gameFolder = Folder(gameRelative);

        var launchers = new List<string>();
        var otherLaunchers = new List<string>();
        var codeFiles = 0;
        foreach (var (target, source) in packageFiles)
        {
            if (!IsCode(target)) continue;
            codeFiles++;
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                target.Equals(gameRelative, StringComparison.OrdinalIgnoreCase))
                continue;
            if (PeImage.Read(source) is not { } image || !IsInjector(image)) continue;
            if (image.Machine == game.Machine) launchers.Add(target);
            else otherLaunchers.Add(target);
        }
        if (launchers.Count == 0 && otherLaunchers.Count > 0)
            throw new InvalidOperationException(
                $"The mod launcher '{otherLaunchers[0]}' has a different architecture from the game.");
        if (launchers.Count > 1)
            throw new InvalidOperationException(
                "The mod contains more than one launcher that starts the game: " +
                string.Join(", ", launchers) + ". Publish a Proton release that names one.");

        var proxies = new List<Proxy>();
        var gameLoaded = new List<string>();
        foreach (var (target, fromDependency) in packageFiles.Keys.Select(path => (path, false))
                     .Concat(dependencyFiles.Keys.Select(path => (path, true))))
        {
            if (!target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                !Folder(target).Equals(gameFolder, StringComparison.OrdinalIgnoreCase))
                continue;
            var module = Path.GetFileNameWithoutExtension(target);
            var builtin = builtinModules.Contains(module) && !IsRedistributableRuntime(module);
            // The game loads a DLL it imports by name, or one replacing a file it already ships.
            var loadedByGame = game.Imports.ContainsKey(Path.GetFileName(target)) || GameShips(target);
            if (!builtin && !loadedByGame) continue;
            var source = fromDependency ? dependencyFiles[target] : packageFiles[target];
            var image = PeImage.Read(source) ??
                throw new InvalidOperationException($"'{target}' is not a valid Windows DLL.");
            if (image.Machine != game.Machine)
                throw new InvalidOperationException(
                    $"'{target}' has a different architecture from the game executable.");
            if (builtin) proxies.Add(new Proxy(module.ToLowerInvariant(), target, fromDependency));
            else gameLoaded.Add(target);
        }

        var replacesGameFiles = packageFiles.Keys.Any(target => IsCode(target) && GameShips(target));
        var installedLoader = launchers.Count == 0 && proxies.Count == 0
            ? ProtonLoaderDetector.Detect(gameDirectory, gameExecutable)
            : null;
        if (codeFiles > 0 && launchers.Count == 0 && proxies.Count == 0 && gameLoaded.Count == 0 &&
            !replacesGameFiles && installedLoader is null)
            throw new InvalidOperationException(
                "The mod's code has no launcher, loader DLL beside the game, or loader it depends on. " +
                "A Proton release is needed for this mod.");
        if (proxies.Count > MaxProxies)
            throw new InvalidOperationException(
                $"The mod places {proxies.Count} DLLs that replace Proton components beside the game. " +
                "A Proton release is needed for this mod.");
        return new Plan(launchers.SingleOrDefault(), proxies, gameLoaded, replacesGameFiles, installedLoader);
    }

    /// <summary>A program that starts another process and runs code inside it.</summary>
    public static bool IsInjector(PeImage image) =>
        ProcessCreation.Any(image.ImportsFunction) &&
        image.ImportsFunction("WriteProcessMemory") &&
        RemoteExecution.Any(image.ImportsFunction);

    /// <summary>
    /// Microsoft's C and C++ runtime DLLs shipped beside a game are runtime copies, not loaders.
    /// Proton supplies working versions, and the manager installs the official redistributable
    /// when the mod's imports need it.
    /// </summary>
    private static bool IsRedistributableRuntime(string module) =>
        module.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) ||
        module.Equals("ucrtbase", StringComparison.OrdinalIgnoreCase) ||
        module.StartsWith("msvcp", StringComparison.OrdinalIgnoreCase) ||
        module.StartsWith("msvcr", StringComparison.OrdinalIgnoreCase) ||
        module.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase) ||
        module.StartsWith("vcomp", StringComparison.OrdinalIgnoreCase) ||
        module.StartsWith("concrt", StringComparison.OrdinalIgnoreCase) ||
        module.StartsWith("vccorlib", StringComparison.OrdinalIgnoreCase);

    private static bool IsCode(string path) =>
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".asi", StringComparison.OrdinalIgnoreCase);

    private static string Folder(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return slash < 0 ? "" : relative[..slash];
    }
}
