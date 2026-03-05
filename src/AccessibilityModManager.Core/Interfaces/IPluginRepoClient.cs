using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IPluginRepoClient
{
    Task<PluginRepoIndex> FetchPluginIndexAsync(PluginEntry plugin, CancellationToken ct = default);
    Task<string> DownloadPackageAsync(Uri packageUrl, string destFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
    Task<bool> VerifySha256Async(string filePath, string expectedHash, CancellationToken ct = default);
}
