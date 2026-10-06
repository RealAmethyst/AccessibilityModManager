using System.Reflection.PortableExecutable;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Reads a game's installed Windows loader files once when planning a Proton launch. A DLL
/// override is only suggested when a known proxy and its loader files are both present and
/// the proxy has the same PE architecture as the game executable.
/// </summary>
public static class ProtonLoaderDetector
{
    public sealed record Finding(string Loader, string DllOverride, string ProxyPath, string MarkerPath);

    public static Finding? Detect(string gameDirectory, string gameExecutable,
        IReadOnlyDictionary<string, string>? proposedFiles = null)
    {
        var executable = PathSafety.CombineContained(gameDirectory,
            gameExecutable.Replace('\\', '/'));
        PathSafety.EnsureNoReparseTraversal(gameDirectory, executable, "game executable");
        if (!File.Exists(executable))
            throw new FileNotFoundException("The game executable is missing.", executable);

        var gameMachine = ReadMachine(executable);
        if (gameMachine is not (Machine.Amd64 or Machine.I386))
            return null;

        var executableDirectory = Path.GetDirectoryName(executable)!;
        var candidates = new List<Finding>();
        Check("BepInEx", "winhttp.dll", "BepInEx/core/BepInEx.dll", "winhttp.dll=n,b");
        Check("MelonLoader", "version.dll", "MelonLoader/net6/MelonLoader.dll", "version=n,b");
        Check("MelonLoader", "version.dll", "MelonLoader/net35/MelonLoader.dll", "version=n,b");
        Check("DSCSModLoader", "freetype.dll", "DSCSModLoader.dll", "freetype=n,b");

        var distinct = candidates.DistinctBy(candidate => candidate.DllOverride).ToList();
        if (distinct.Count > 1)
            throw new InvalidOperationException(
                "More than one recognized proxy loader is installed beside the game. " +
                "Choose an explicit Proton launch setup for this game.");
        return distinct.SingleOrDefault();

        void Check(string loader, string proxyName, string marker, string dllOverride)
        {
            var proxy = PathSafety.CombineContained(executableDirectory, proxyName);
            var markerPath = PathSafety.CombineContained(executableDirectory, marker);
            PathSafety.EnsureNoReparseTraversal(gameDirectory, proxy, "loader proxy");
            PathSafety.EnsureNoReparseTraversal(gameDirectory, markerPath, "loader marker");
            var proxyRelative = Path.GetRelativePath(gameDirectory, proxy).Replace('\\', '/');
            var markerRelative = Path.GetRelativePath(gameDirectory, markerPath).Replace('\\', '/');
            var proxySource = proposedFiles is not null && proposedFiles.TryGetValue(proxyRelative, out var plannedProxy)
                ? plannedProxy : proxy;
            var markerSource = proposedFiles is not null && proposedFiles.TryGetValue(markerRelative, out var plannedMarker)
                ? plannedMarker : markerPath;
            if (!File.Exists(proxySource) || !File.Exists(markerSource)) return;
            if (ReadMachine(proxySource) != gameMachine)
                throw new InvalidOperationException(
                    $"{loader}'s {proxyName} has a different PE architecture from the game executable.");
            if (ReadMachine(markerSource) is null)
                throw new InvalidOperationException(
                    $"{loader}'s {marker} is not a valid Windows DLL.");
            candidates.Add(new Finding(loader, dllOverride, proxyRelative, markerRelative));
        }
    }

    private static Machine? ReadMachine(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.PEHeaders.CoffHeader.Machine;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
