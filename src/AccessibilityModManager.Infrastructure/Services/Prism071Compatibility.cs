using System.Security.Cryptography;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// The Prism 0.7.1 C ABI adapter, paired only with pinned Prism 0.18.3. Prism 0.7.1 consumers
/// receive PrismConfig version 1 by value; 0.18.3 returns the larger version 4 struct.
/// </summary>
internal static class Prism071Compatibility
{
    internal const string Sha256 = "43581ed862e787cf1ff4b3f67e3076022e63090f8116138ab136e21effefa475";
    private const string ResourceName =
        "AccessibilityModManager.Infrastructure.Assets.Prism071Compat.prism_compat.dll";

    internal static async Task<string> ExtractAsync(string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, "prism-071-compat.dll");
        await using var resource = typeof(Prism071Compatibility).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The Prism 0.7.1 compatibility module is missing.");
        await using (var output = File.Create(path))
            await resource.CopyToAsync(output, ct);
        await using var verification = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(verification, ct));
        if (!actual.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Prism 0.7.1 compatibility module is damaged.");
        return path;
    }
}
