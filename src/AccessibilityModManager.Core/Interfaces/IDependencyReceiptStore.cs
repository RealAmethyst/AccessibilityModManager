using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

/// <summary>
/// Persistent store for <see cref="DependencyReceipt"/>s. Dep receipts are scoped per
/// (gameId, dependencyId), so multiple plugins sharing a loader (e.g. MelonLoader) share one
/// receipt with refcounted plugin ids. The store handles tamper-detection the same way
/// <see cref="IReceiptStore"/> does for install receipts.
/// </summary>
public interface IDependencyReceiptStore
{
    Task<DependencyReceipt?> LoadAsync(string gameId, string dependencyId);

    Task SaveAsync(DependencyReceipt receipt);

    Task DeleteAsync(string gameId, string dependencyId);

    Task<List<DependencyReceipt>> LoadAllForGameAsync(string gameId);

    /// <summary>
    /// Per-dep backup directory used by <c>DependencyAutoInstaller</c> to stash files it
    /// overwrites during install. Same restore semantics as the mod-install backup folder.
    /// </summary>
    string GetBackupDirectory(string gameId, string dependencyId);

    /// <summary>
    /// True when any dependency receipt file for this game exists on disk but cannot be trusted
    /// (corrupt, tampered, or missing its integrity data). Uninstall fails closed on this —
    /// releasing refcounts against a partial view would remove loaders other mods still need.
    /// </summary>
    Task<bool> AnyUnreadableForGameAsync(string gameId);
}
