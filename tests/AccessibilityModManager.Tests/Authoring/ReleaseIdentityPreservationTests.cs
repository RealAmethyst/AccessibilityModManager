using System.Text;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Tests.Authoring;

public sealed class ReleaseIdentityPreservationTests
{
    [Fact]
    public void MissingExistingReleaseIsNamedAsAClobber()
    {
        var before = CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex();
        before.ReleasesByGameId[CatalogWorkflowTests.CatalogFixture.SecondaryGameId]
            .Add(Release(CatalogWorkflowTests.CatalogFixture.SecondaryGameId, "1.0.0", "stable"));
        var after = CatalogWorkflowTests.CatalogFixture.Clone(before)!;
        after.ReleasesByGameId[CatalogWorkflowTests.CatalogFixture.PrimaryGameId].Clear();

        var failure = ReleaseIdentityPreservation.ValidateTransition(Bytes(before), Bytes(after));

        Assert.NotNull(failure);
        Assert.Contains("ff7 1.0.0 (stable)", failure);
    }

    [Fact]
    public void ReplacingDetailsAndAddingAnIdentityPreservesTheReleaseSet()
    {
        var before = CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex();
        var after = CatalogWorkflowTests.CatalogFixture.Clone(before)!;
        var existing = after.ReleasesByGameId[CatalogWorkflowTests.CatalogFixture.PrimaryGameId][0];
        after.ReleasesByGameId[CatalogWorkflowTests.CatalogFixture.PrimaryGameId][0] = new ModRelease
        {
            GameId = existing.GameId,
            PluginId = existing.PluginId,
            Version = existing.Version,
            Channel = existing.Channel,
            PackageUrl = new Uri("https://downloads.example.invalid/replaced.zip"),
            Sha256 = new string('f', 64)
        };
        after.ReleasesByGameId[CatalogWorkflowTests.CatalogFixture.SecondaryGameId]
            .Add(Release(CatalogWorkflowTests.CatalogFixture.SecondaryGameId, "2.0.0", "beta"));

        Assert.Null(ReleaseIdentityPreservation.ValidateTransition(Bytes(before), Bytes(after)));
    }

    private static ModRelease Release(string gameId, string version, string channel) => new()
    {
        GameId = gameId,
        PluginId = CatalogWorkflowTests.CatalogFixture.PluginId,
        Version = version,
        Channel = channel,
        PackageUrl = new Uri($"https://downloads.example.invalid/{gameId}/{version}.zip"),
        Sha256 = new string('d', 64)
    };

    private static byte[] Bytes(PluginRepoIndex index) =>
        Encoding.UTF8.GetBytes(CatalogWorkflowTests.CatalogFixture.Serialize(index));
}
