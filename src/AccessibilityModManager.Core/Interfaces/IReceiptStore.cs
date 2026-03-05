using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IReceiptStore
{
    Task<InstallReceipt?> LoadAsync(string gameId, string pluginId);
    Task SaveAsync(InstallReceipt receipt);
    Task DeleteAsync(string gameId, string pluginId);
    Task<List<InstallReceipt>> LoadAllForGameAsync(string gameId);
}
