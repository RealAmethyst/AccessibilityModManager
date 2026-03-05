using System.Security;

namespace AccessibilityModManager.Infrastructure.Security;

/// <summary>
/// Enforces HTTPS-only policy on all external URLs.
/// </summary>
public static class UrlValidator
{
    public static void RequireHttps(Uri url, string context)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new SecurityException($"HTTPS required for {context}, got: {url}");
    }

    public static void RequireHttps(string url, string context)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Invalid URL for {context}: {url}");

        RequireHttps(uri, context);
    }
}
