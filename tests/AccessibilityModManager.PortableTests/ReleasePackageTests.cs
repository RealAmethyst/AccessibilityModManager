using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.PortableTests;

public sealed class ReleasePackageTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Platform_selection_uses_the_matching_hash_and_url_without_changing_version_or_access(bool gated)
    {
        var original = new ModRelease
        {
            GameId = "game", PluginId = "author", Version = "1.0", Channel = "stable", TargetPlatform = "windows",
            Notes = "Both platforms", Sha256 = new string('a', 64),
            PackageUrl = gated ? null : new Uri("https://example.org/windows.zip"),
            Patreon = gated ? new PatreonGate { CampaignId = "123", TierIds = ["456"], ServerUrl = "https://example.org/windows.zip" } : null,
            LinuxPackage = new ReleasePackage { TargetPlatform = "xivlauncher", Sha256 = new string('b', 64), PackageUrl = new Uri("https://example.org/linux.zip") }
        };
        var index = new PluginRepoIndex
        {
            PluginId = "author", RepoVersion = "1", GeneratedAt = DateTime.UnixEpoch, Games = [new GameDefinition { GameId = "game", DisplayName = "Game" }],
            ReleasesByGameId = new() { ["game"] = [original] }
        };
        var report = PluginIndexValidation.Validate("author", JsonSerializer.Serialize(index, Json));
        Assert.Empty(report.PublishBlockers);
        var release = Assert.Single(report.Index.ReleasesByGameId["game"]);
        var windows = ReleasePackages.ForPlatform(release, false);
        var linux = ReleasePackages.ForPlatform(release, true);
        Assert.Equal(original.Sha256, windows.Sha256);
        Assert.Equal(original.LinuxPackage.Sha256, linux.Sha256);
        Assert.Equal(windows.Version, linux.Version);
        Assert.Equal(windows.Channel, linux.Channel);
        Assert.Equal(windows.Notes, linux.Notes);
        Assert.Equal("xivlauncher", linux.TargetPlatform);
        Assert.Equal(gated ? null : original.LinuxPackage.PackageUrl, linux.PackageUrl);
        Assert.Equal(gated ? "https://example.org/linux.zip" : null, linux.Patreon?.ServerUrl);
        if (gated) Assert.Equal(windows.Patreon!.TierIds, linux.Patreon!.TierIds);
        Assert.Equal(2, ReleasePackages.All(release).Count());
    }

    [Theory]
    [InlineData("windows", "linux", "https://example.org/linux.zip", "short")]
    [InlineData("windows", "windows", "https://example.org/linux.zip", null)]
    [InlineData("windows", "xivlauncher", "http://example.org/linux.zip", null)]
    [InlineData("linux", "xivlauncher", "https://example.org/linux.zip", null)]
    public void Invalid_linux_payload_is_not_offered(string primaryTarget, string linuxTarget, string url, string? hash)
    {
        var release = new ModRelease
        {
            PluginId = "author", GameId = "game", Version = "1", Channel = "stable", TargetPlatform = primaryTarget,
            Sha256 = new string('a', 64), PackageUrl = new Uri("https://example.org/windows.zip"),
            LinuxPackage = new ReleasePackage { TargetPlatform = linuxTarget, PackageUrl = new Uri(url), Sha256 = hash ?? new string('b', 64) }
        };
        Assert.Throws<InvalidOperationException>(() => ReleasePackages.ForPlatform(release, true));
        Assert.Same(release, ReleasePackages.ForPlatform(release, false));
    }
}
