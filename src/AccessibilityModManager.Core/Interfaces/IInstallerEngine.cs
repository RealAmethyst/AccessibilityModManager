using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IInstallerEngine
{
    /// <summary>
    /// Install a fresh mod. <paramref name="scriptHost"/> is required when the manifest
    /// declares any lifecycle script. <paramref name="dependencyHost"/> is required when the
    /// game has any auto-installable or manual-only dependency. Tests and code paths that
    /// don't deal with those features can pass <c>null</c>.
    /// </summary>
    Task<InstallReceipt> InstallAsync(GameInstall game, ModRelease release, string packageZipPath, IScriptHost? scriptHost = null, IDependencyHost? dependencyHost = null, CancellationToken ct = default);

    Task<InstallReceipt> UpdateAsync(GameInstall game, ModRelease release, string packageZipPath, IScriptHost? scriptHost = null, IDependencyHost? dependencyHost = null, CancellationToken ct = default);

    Task UninstallAsync(GameInstall game, string pluginId, IScriptHost? scriptHost = null, CancellationToken ct = default);

    Task RollbackAsync(GameInstall game, InstallReceipt receipt, CancellationToken ct = default);
}
