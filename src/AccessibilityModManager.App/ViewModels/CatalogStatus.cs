namespace AccessibilityModManager.App.ViewModels;

/// <summary>
/// Shared wording for the offline-catalog status lines so every tab announces cached data the
/// same way over a screen reader.
/// </summary>
internal static class CatalogStatus
{
    /// <summary>Local-time stamp for "showing the saved catalog from …". Null-safe: an envelope
    /// from a pre-timestamp cache still reads sensibly.</summary>
    public static string FormatCachedAt(DateTimeOffset? cachedAtUtc) =>
        cachedAtUtc?.ToLocalTime().ToString("g") ?? "an earlier session";
}
