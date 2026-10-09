using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Checks download requirements enforced by the dependency installers, before publication.</summary>
public static class DependencyAuthoringValidation
{
    public static IEnumerable<string> Errors(IEnumerable<Dependency> dependencies)
    {
        foreach (var dependency in dependencies)
        {
            var install = dependency.Fix?.AutoInstall ?? dependency.Fix?.Xlm;
            if (install is null) continue;
            if (install.Sha256 is not { Length: 64 } || !install.Sha256.All(char.IsAsciiHexDigit))
                yield return $"Dependency '{dependency.Id}' needs the actual 64-character SHA-256 of its automatic download. The manager would reject this hash.";
            if (!Uri.TryCreate(dependency.Fix?.DownloadUrl, UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps)
                yield return $"Dependency '{dependency.Id}' needs an absolute HTTPS download URL for automatic installation.";
        }
    }
}
