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

    /// <summary>
    /// Plugin ids for this game whose receipt file exists on disk but cannot be trusted (corrupt,
    /// tampered, or missing its integrity data). The engine fails closed on these: installs must
    /// not treat the mod as absent (its files would silently drop out of collision ownership) and
    /// uninstalls must not report "nothing to uninstall".
    /// </summary>
    Task<List<string>> UnreadablePluginIdsForGameAsync(string gameId);
}
