using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IReceiptStore
{
    Task<InstallReceipt?> LoadAsync(string gameId, string pluginId);
    Task SaveAsync(InstallReceipt receipt);
    Task DeleteAsync(string gameId, string pluginId);
    Task<List<InstallReceipt>> LoadAllForGameAsync(string gameId);

    /// <summary>
    /// Absolute path to the per-receipt directory for a (plugin, game) pair. Used by the
    /// installer to cache post-uninstall scripts alongside the receipt so they survive long
    /// enough to run when the user later uninstalls.
    /// </summary>
    string GetReceiptDirectory(string gameId, string pluginId);
}
