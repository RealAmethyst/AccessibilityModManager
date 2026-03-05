using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IInstallerEngine
{
    Task<InstallReceipt> InstallAsync(GameInstall game, ModRelease release, string packageZipPath, CancellationToken ct = default);
    Task<InstallReceipt> UpdateAsync(GameInstall game, ModRelease release, string packageZipPath, CancellationToken ct = default);
    Task UninstallAsync(GameInstall game, string pluginId, CancellationToken ct = default);
    Task RollbackAsync(GameInstall game, InstallReceipt receipt, CancellationToken ct = default);
}
