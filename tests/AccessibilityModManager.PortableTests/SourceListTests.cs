using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.PortableTests;

public sealed class SourceListTests
{
    private static readonly PluginDirectoryEntry Buu = new("listing", "buu420", "Buu420", "buu420", 43735748,
        "buu420/buu-s-mods", "https://raw.githubusercontent.com/buu420/buu-s-mods/main/index.json", []);

    [Fact]
    public void Previously_added_identity_uses_remove_even_with_an_older_address()
    {
        var saved = new UserPluginSource { PluginId = "BUU420", DisplayName = "Buu",
            IndexUrl = "https://raw.githubusercontent.com/buu420/buu-s-mods/refs/heads/main/index.json" };
        var row = Assert.Single(SourceListItem.Build([Buu], [saved]));
        Assert.True(row.IsAdded);
        Assert.Equal("Remove", row.ActionLabel);
        Assert.Same(saved, row.Saved);
        Assert.Equal(saved.IndexUrl, row.IndexUrl);
        Assert.Contains("added", row.ToString());
        Assert.Equal("Add", Assert.Single(SourceListItem.Build([Buu], [])).ActionLabel);
    }

    [Fact]
    public void Sources_missing_from_directory_remain_removable_including_when_offline()
    {
        var saved = new UserPluginSource { PluginId = "old-author", DisplayName = "Older source",
            IndexUrl = "https://example.org/index.json" };
        var rows = SourceListItem.Build([Buu], [saved]);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Add", rows.Single(row => row.PluginId == "buu420").ActionLabel);
        Assert.Equal("Remove", rows.Single(row => row.PluginId == "old-author").ActionLabel);
        Assert.Same(saved, Assert.Single(SourceListItem.Build([], [saved])).Saved);
    }
}
