using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IPluginRegistryClient
{
    /// <summary>
    /// Fetches, verifies, and validates the signed registry. When the network is unreachable the
    /// last accepted copy is served from the offline cache (revalidated in full) with
    /// <see cref="Fetched{T}.FromCache"/> set so the UI can say so.
    /// </summary>
    Task<Fetched<PluginRegistry>> FetchRegistryAsync(Uri registryUrl, CancellationToken ct = default);
}
