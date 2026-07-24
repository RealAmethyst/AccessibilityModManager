using System.Diagnostics;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Security;

/// <summary>
/// Opens author-supplied external URLs (changelog links, release-note markdown links, author
/// social links, manual-dependency pages) in the user's browser — but ONLY when the URL is an
/// absolute <c>https</c> URL. Release notes and index metadata are untrusted plugin-author
/// content; handing an arbitrary URI to <c>ShellExecute</c> would let a crafted <c>file:</c>,
/// <c>ms-*</c>, or other registered protocol trigger local shell actions. Anything that isn't a
/// well-formed https URL is refused (and, for rendered markdown, shown as plain text instead of a
/// live link). This mirrors the app-wide "HTTPS enforced on all plugin URLs" invariant for the
/// launch path.
/// </summary>
public static class ExternalLink
{
    /// <summary>True if the string is an absolute https URL we're willing to open.</summary>
    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowed(uri);

    /// <summary>True if the URI is absolute and https.</summary>
    public static bool IsAllowed(Uri? uri) =>
        uri is not null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Opens <paramref name="url"/> in the default browser when it is an https URL. Returns false
    /// (opening nothing) for any other scheme or a malformed URL. Never throws.
    /// </summary>
    public static bool TryOpen(string? url, ILogger? logger = null)
    {
        if (!IsAllowed(url))
        {
            logger?.Warning("Refused to open non-https external URL: {Url}", url);
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "Failed to open external URL {Url}", url);
            return false;
        }
    }
}
