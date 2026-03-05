using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IPluginRegistryClient
{
    Task<PluginRegistry> FetchRegistryAsync(Uri registryUrl, CancellationToken ct = default);
}
