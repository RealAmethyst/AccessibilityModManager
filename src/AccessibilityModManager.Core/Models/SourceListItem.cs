namespace AccessibilityModManager.Core.Models;

public sealed record SourceListItem(PluginDirectoryEntry? Listing, UserPluginSource? Saved)
{
    public string PluginId => Saved?.PluginId ?? Listing!.PluginId;
    public string IndexUrl => Saved?.IndexUrl ?? Listing!.IndexUrl;
    public string DisplayName => Listing?.DisplayName ?? Saved?.DisplayName ?? PluginId;
    public bool IsAdded => Saved is not null;
    public string ActionLabel => IsAdded ? "Remove" : "Add";
    public override string ToString() => $"{DisplayName}, {(IsAdded ? "added" : "not added")}" +
        (Listing is null ? $", from {new Uri(IndexUrl).Host}" : $", GitHub {Listing.GitHubLogin}, {Listing.Games.Count} games");

    public static IReadOnlyList<SourceListItem> Build(IEnumerable<PluginDirectoryEntry> listings,
        IEnumerable<UserPluginSource> saved)
    {
        var remaining = saved.ToList();
        var result = new List<SourceListItem>();
        foreach (var listing in listings)
        {
            // Identity survives older raw URL spellings, including refs/heads/main.
            var existing = remaining.FirstOrDefault(source =>
                SafeId.Canonical(source.PluginId).Equals(SafeId.Canonical(listing.PluginId), StringComparison.OrdinalIgnoreCase));
            if (existing is not null) remaining.Remove(existing);
            result.Add(new(listing, existing));
        }
        // Previously added sources remain removable even if they leave the public directory.
        result.AddRange(remaining.Select(source => new SourceListItem(null, source)));
        return result.OrderBy(source => source.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
