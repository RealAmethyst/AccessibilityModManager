namespace AccessibilityModManager.Core.Models;

public static class PluginDirectoryProtocol
{
    public const string Path = "/owner/plugin-directory/v1";
    public const string Url = "https://accessibilitymods.com" + Path;
}

public sealed record DirectoryGame(string GameId, string DisplayName);
public sealed record PluginDirectoryEntry(string Id, string PluginId, string DisplayName,
    string GitHubLogin, long GitHubAccountId, string Repository, string IndexUrl,
    IReadOnlyList<DirectoryGame> Games)
{
    public override string ToString() => $"{DisplayName}, GitHub {GitHubLogin}, {Games.Count} games";
}
public sealed record PluginDirectorySnapshot(int Version, IReadOnlyList<PluginDirectoryEntry> Plugins);
public sealed record PluginDirectorySubmission(string Repository, string IndexPath, string PluginId, string? IndexUrl = null);
public sealed record PluginAvailability(bool Available);
