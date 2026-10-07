using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.Infrastructure.Installer;

/// <summary>Install a declared native portable game without executing it or replacing existing files.</summary>
public sealed class NativeGameInstaller(DependencyAutoInstaller dependencies, GameDependencyReceiptStore? gameReceipts = null)
{
    public static IReadOnlyList<Dependency> Available(GameDefinition game) =>
        DependencyTargeting.ForTarget(game.Dependencies, ReleaseTarget.Linux)
            .Where(dependency => dependency.IsGameInstaller && dependency.Fix?.AutoInstall is ExtractAppAutoInstall)
            .ToArray();

    public async Task<string> InstallAsync(GameDefinition game, Dependency dependency, string parentDirectory,
        IDependencyHost? host, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This installer is for native Linux apps.");
        if (!Available(game).Contains(dependency))
            throw new InvalidOperationException("Choose a native Linux portable-game dependency from this catalog.");
        if (string.IsNullOrWhiteSpace(game.LinuxExeName))
            throw new InvalidOperationException("The author needs to declare the native Linux executable before this game can be installed.");
        PathSafety.EnsureSafeId(game.GameId, "game id");
        var destination = PathSafety.CombineContained(parentDirectory, game.GameId);
        if (Path.Exists(destination))
            throw new InvalidOperationException("That game folder already exists. Browse to its installation or choose another parent folder.");
        var staging = Path.Combine(parentDirectory, ".amm-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await dependencies.ExtractPortableAppAsync(dependency, staging, host, ct, progress);
            var relativeExecutable = game.LinuxExeName.Replace('\\', '/');
            PathSafety.CombineContained(staging, relativeExecutable);
            var root = PortableAppLayout.ResolveInstallRoot(staging, relativeExecutable)
                ?? throw new InvalidDataException("The archive does not contain the declared Linux executable.");
            var executable = PathSafety.CombineContained(root, relativeExecutable);
            PathSafety.EnsureNoReparseTraversal(staging, executable, "native executable");
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
            var rootRelative = Path.GetRelativePath(staging, root);
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            var installed = rootRelative == "." ? destination : Path.Combine(destination, rootRelative);
            await new DependencyUpdates(dependencies, Serilog.Log.Logger, gameReceipts).RecordPortableAsync(installed, dependency, ct);
            return installed;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
