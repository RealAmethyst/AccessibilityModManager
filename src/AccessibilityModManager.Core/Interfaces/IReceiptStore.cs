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

    /// <summary>
    /// Every plugin id that has something installed under it, read from the receipt folder names.
    ///
    /// <para>Used to keep an identity reserved when no catalog offers it any more: a source removed
    /// while its mods stayed installed leaves its receipts behind, and a different source taking
    /// that id would inherit them. Deliberately reads the folder layout rather than the receipt
    /// contents — the answer is "is this id spoken for", which the directory name already carries,
    /// so an unreadable receipt still reserves its id instead of freeing it.</para>
    /// </summary>
    Task<List<string>> InstalledPluginIdsAsync();
}
