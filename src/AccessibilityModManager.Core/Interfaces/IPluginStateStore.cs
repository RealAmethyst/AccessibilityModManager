using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IPluginStateStore
{
    Task<List<PluginState>> LoadAllAsync();
    Task SaveAsync(PluginState state);
}
