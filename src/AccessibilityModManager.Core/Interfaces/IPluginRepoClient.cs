using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IPluginRepoClient
{
    /// <summary>
    /// Fetches and validates one plugin's index. When the network is unreachable the last
    /// accepted copy is served from the offline cache (revalidated against the signed registry
    /// entry) with <see cref="Fetched{T}.FromCache"/> set so the UI can say so.
    /// </summary>
    Task<Fetched<PluginRepoIndex>> FetchPluginIndexAsync(PluginEntry plugin, CancellationToken ct = default);
    Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
    Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default);
}
