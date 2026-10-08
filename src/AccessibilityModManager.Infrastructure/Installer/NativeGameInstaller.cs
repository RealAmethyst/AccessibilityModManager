using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.Infrastructure.Installer;

/// <summary>Install a declared native portable game without executing it, backing up replaced files.</summary>
public sealed class NativeGameInstaller(DependencyAutoInstaller dependencies, GameDependencyReceiptStore? gameReceipts = null)
{
    public static IReadOnlyList<Dependency> Available(GameDefinition game) =>
        DependencyTargeting.ForTarget(game.Dependencies, ReleaseTarget.Linux)
            .Where(dependency => dependency.IsGameInstaller && dependency.Fix?.AutoInstall is ExtractAppAutoInstall)
            .ToArray();

    public async Task<string> InstallAsync(GameDefinition game, Dependency dependency, string installDirectory,
        IDependencyHost? host, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This installer is for native Linux apps.");
        if (!Available(game).Contains(dependency))
            throw new InvalidOperationException("Choose a native Linux portable-game dependency from this catalog.");
        if (string.IsNullOrWhiteSpace(game.LinuxExeName))
            throw new InvalidOperationException("The author needs to declare the native Linux executable before this game can be installed.");
        PathSafety.EnsureSafeId(game.GameId, "game id");
        var destination = Path.GetFullPath(installDirectory);
        await new DependencyUpdates(dependencies, Serilog.Log.Logger, gameReceipts).UpdatePortableAsync(
            new GameInstall { Game = game, PluginId = "", InstallPath = destination },
            dependency, ReleaseTarget.Linux, host, progress, ct);
        return destination;
    }
}
