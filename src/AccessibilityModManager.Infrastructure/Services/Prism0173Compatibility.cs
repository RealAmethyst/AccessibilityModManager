using System.Security.Cryptography;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>The audited Prism 0.17.3 C ABI adapter, paired only with pinned Prism 0.18.3.</summary>
internal static class Prism0173Compatibility
{
    internal const string Sha256 = "f2f234b8ec3b0f6c0df5f4e501232e8e9c20a1299c885ffc7e19ef90559ab6ec";
    private const string ResourceName =
        "AccessibilityModManager.Infrastructure.Assets.Prism0173Compat.prism_compat.dll";

    internal static async Task<string> ExtractAsync(string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, "prism-0173-compat.dll");
        await using var resource = typeof(Prism0173Compatibility).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The Prism 0.17.3 compatibility module is missing.");
        await using (var output = File.Create(path))
            await resource.CopyToAsync(output, ct);
        await using var verification = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(verification, ct));
        if (!actual.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Prism 0.17.3 compatibility module is damaged.");
        return path;
    }
}
