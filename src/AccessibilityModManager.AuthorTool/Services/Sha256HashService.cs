using System.IO;
using System.Security.Cryptography;

namespace AccessibilityModManager.AuthorTool.Services;

public sealed class Sha256HashService
{
    public async Task<string> ComputeAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
