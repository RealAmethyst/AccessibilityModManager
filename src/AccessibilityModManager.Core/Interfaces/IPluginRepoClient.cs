using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IPluginRepoClient
{
    /// <summary>
    /// Fetches and validates one plugin's index. When the network is unreachable the last
    /// accepted copy is served from the offline cache (revalidated against the signed registry
    /// entry) with <see cref="Fetched{T}.FromCache"/> set so the UI can say so.
    /// </summary>
    /// <summary>
    /// Fetches one catalog. The <see cref="CatalogSource"/> carries both where it came from and what
    /// trust applies to it, decided together at construction — so a caller cannot hand a user-added
    /// source registry trust, or the reverse.
    /// </summary>
    Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(CatalogSource source, CancellationToken ct = default);

    /// <summary>
    /// Convenience for a registry-listed plugin, which is always a registry source. Exists so the
    /// common call site stays short; it is not a second path — it builds the same
    /// <see cref="CatalogSource"/> the other overload takes.
    /// </summary>
    Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(PluginEntry plugin, CancellationToken ct = default)
        => FetchPluginIndexAsync(CatalogSource.FromRegistry(plugin), ct);
    Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
    Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default);
}
