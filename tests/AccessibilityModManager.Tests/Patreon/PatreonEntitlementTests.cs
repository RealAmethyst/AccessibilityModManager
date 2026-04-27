using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Patreon;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Patreon;

/// <summary>
/// Entitlement matching is the core of the gate — gets it wrong and either patrons get
/// locked out of content they paid for, or non-patrons see content they shouldn't. Tests
/// the any-of semantics (F2=A) and the not-signed-in / wrong-campaign / wrong-tier paths.
/// </summary>
public class PatreonEntitlementTests
{
    [Fact]
    public void IsEntitled_AnyMatchingTier_True()
    {
        var svc = MakeService();
        SetMemberships(svc, new PatreonMembership
        {
            CampaignId = "camp-A",
            CurrentlyEntitledTierIds = new List<string> { "tier-2" },
            CampaignDisplayName = "Foo Mods"
        });

        var gate = new PatreonGate
        {
            CampaignId = "camp-A",
            TierIds = new List<string> { "tier-1", "tier-2", "tier-3" },
            PostId = "post-1"
        };

        Assert.True(svc.IsEntitled(gate));
    }

    [Fact]
    public void IsEntitled_OnlyDifferentTier_False()
    {
        var svc = MakeService();
        SetMemberships(svc, new PatreonMembership
        {
            CampaignId = "camp-A",
            CurrentlyEntitledTierIds = new List<string> { "tier-cheap" },
            CampaignDisplayName = "Foo Mods"
        });

        var gate = new PatreonGate
        {
            CampaignId = "camp-A",
            TierIds = new List<string> { "tier-expensive" },
            PostId = "post-1"
        };

        Assert.False(svc.IsEntitled(gate));
    }

    [Fact]
    public void IsEntitled_DifferentCampaign_False()
    {
        var svc = MakeService();
        SetMemberships(svc, new PatreonMembership
        {
            CampaignId = "camp-other-author",
            CurrentlyEntitledTierIds = new List<string> { "tier-2" },
            CampaignDisplayName = "Other Author"
        });

        var gate = new PatreonGate
        {
            CampaignId = "camp-A",
            TierIds = new List<string> { "tier-2" },
            PostId = "post-1"
        };

        // Same tier id literal but different campaign — must not leak across.
        Assert.False(svc.IsEntitled(gate));
    }

    [Fact]
    public void IsEntitled_NotSignedIn_False()
    {
        var svc = MakeService();
        // Don't set any memberships.

        var gate = new PatreonGate
        {
            CampaignId = "camp-A",
            TierIds = new List<string> { "tier-2" },
            PostId = "post-1"
        };

        Assert.False(svc.IsEntitled(gate));
    }

    [Fact]
    public void IsEntitled_MembershipExistsButNoEntitledTiers_False()
    {
        // A user can be a member of a campaign but currently entitled to nothing
        // (e.g. cancelled subscription still in their history). Make sure that's denied.
        var svc = MakeService();
        SetMemberships(svc, new PatreonMembership
        {
            CampaignId = "camp-A",
            CurrentlyEntitledTierIds = new List<string>(),
            CampaignDisplayName = "Foo Mods"
        });

        var gate = new PatreonGate
        {
            CampaignId = "camp-A",
            TierIds = new List<string> { "tier-1" },
            PostId = "post-1"
        };

        Assert.False(svc.IsEntitled(gate));
    }

    private static PatreonService MakeService()
    {
        var http = new System.Net.Http.HttpClient();
        var client = new PatreonClient(http, PatreonAppRegistry.Manager, TestLogger.Create());
        return new PatreonService(
            client,
            new InMemoryAccountStore(),
            new PatreonEntitlementCache(),
            http,
            TestLogger.Create());
    }

    private static void SetMemberships(PatreonService svc, params PatreonMembership[] memberships)
    {
        // Reach into the cache directly — tests don't go through the API to set state.
        var cacheField = typeof(PatreonService).GetField("_cache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var cache = (Core.Interfaces.IPatreonEntitlementCache)cacheField!.GetValue(svc)!;
        cache.Set(memberships);
    }

    private sealed class InMemoryAccountStore : Core.Interfaces.IPatreonAccountStore
    {
        public Task<PatreonAccount?> LoadAsync() => Task.FromResult<PatreonAccount?>(null);
        public Task SaveAsync(PatreonAccount account) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }
}
