using System.IO;
using System.Security.Cryptography;

namespace AccessibilityModManager.AuthorTool.Services;

public sealed class Sha256HashService
{
    public async Task<string> ComputeAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        return await ComputeAsync(stream, ct);
    }

    public async Task<string> ComputeAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new InvalidOperationException("The SHA256 input stream isn't readable.");

        var originalPosition = stream.CanSeek ? stream.Position : (long?)null;
        if (stream.CanSeek)
            stream.Position = 0;

        try
        {
            var hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            if (originalPosition.HasValue)
                stream.Position = originalPosition.Value;
        }
    }
}
