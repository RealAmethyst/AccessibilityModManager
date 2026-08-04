using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.Core.Models;
using Xunit;

namespace AccessibilityModManager.Tests.ViewModels;

/// <summary>
/// The developer-name resolver and the row text built on it. These pin the SPOKEN sentence, which
/// is the actual deliverable: a row that reads "by digimon-tools" instead of "by Amethyst" is the
/// bug this work exists to fix, and nothing else in the suite would catch it coming back.
/// </summary>
public class DeveloperNameTests
{
    private static PluginEntry Entry(string id, string author, string name) => new()
    {
        Id = id,
        Name = name,
        Author = author,
        Description = "listing blurb",
        RepoIndexUrl = new Uri("https://example.com/index.json")
    };

    private static PluginRepoIndex Index(string? displayName) => new()
    {
        PluginId = "p",
        RepoVersion = "1",
        GeneratedAt = DateTime.UnixEpoch,
        Games = [],
        ReleasesByGameId = [],
        Author = displayName is null ? null : new PluginAuthorInfo { DisplayName = displayName }
    };

    [Fact]
    public void PrefersTheDevelopersOwnDisplayName()
        => Assert.Equal(
            "Amethyst",
            DeveloperNames.Resolve(Index("Amethyst"), Entry("p", "Registry Author", "Listing"), "p"));

    [Fact]
    public void FallsBackToTheRegistryAuthorWhenTheIndexHasNoAuthorBlock()
        => Assert.Equal(
            "Registry Author",
            DeveloperNames.Resolve(Index(null), Entry("p", "Registry Author", "Listing"), "p"));

    [Fact]
    public void FallsBackToTheListingNameWhenThereIsNoAuthor()
        => Assert.Equal(
            "Listing",
            DeveloperNames.Resolve(Index(null), Entry("p", "", "Listing"), "p"));

    [Fact]
    public void FallsBackToThePluginIdWhenNothingElseNamesThem()
        => Assert.Equal(
            "p",
            DeveloperNames.Resolve(Index(null), Entry("p", "", ""), "p"));

    [Fact]
    public void FallsBackWhenThereIsNoIndexAtAll()
        => Assert.Equal(
            "Registry Author",
            DeveloperNames.Resolve(index: null, Entry("p", "Registry Author", "Listing"), "p"));

    [Fact]
    public void FallsBackWhenThereIsNoRegistryEntryEither()
        => Assert.Equal("p", DeveloperNames.Resolve(index: null, entry: null, "p"));

    /// <summary>
    /// Whitespace is not a name. Registry validation doesn't require author/name to be non-empty,
    /// so a plain null check would settle on " " and leave a row announcing a mod by nobody.
    /// </summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n  ")]
    public void TreatsWhitespaceAsAbsent(string blank)
        => Assert.Equal(
            "Registry Author",
            DeveloperNames.Resolve(Index(blank), Entry("p", "Registry Author", "Listing"), "p"));

    [Fact]
    public void TrimsTheNameItReturns()
        => Assert.Equal(
            "Amethyst",
            DeveloperNames.Resolve(Index("  Amethyst  "), entry: null, "p"));

    // ------------------------------------------------------------------ row text

    private static ModItemViewModel Row(bool detected, string? installedVersion, bool hasUpdate) => new()
    {
        GameId = "ff7",
        GameDisplayName = "Final Fantasy VII",
        ModName = "Blind Access FF7",
        PluginId = "amethyst-mods",
        DeveloperName = "Amethyst",
        IsDetected = detected,
        InstalledVersion = installedVersion,
        HasUpdate = hasUpdate
    };

    [Fact]
    public void RowNamesTheDeveloperRightAfterTheMod()
        => Assert.Equal(
            "Blind Access FF7 by Amethyst for Final Fantasy VII, v1.2 installed",
            Row(detected: true, "1.2", hasUpdate: false).AnnouncementText);

    [Fact]
    public void RowStillNamesTheDeveloperWhenNothingIsInstalled()
        => Assert.Equal(
            "Blind Access FF7 by Amethyst for Final Fantasy VII, Game not detected",
            Row(detected: false, null, hasUpdate: false).AnnouncementText);

    [Fact]
    public void RowStillNamesTheDeveloperWhenAnUpdateIsWaiting()
        => Assert.Equal(
            "Blind Access FF7 by Amethyst for Final Fantasy VII, v1.2 — update available",
            Row(detected: true, "1.2", hasUpdate: true).AnnouncementText);
}
