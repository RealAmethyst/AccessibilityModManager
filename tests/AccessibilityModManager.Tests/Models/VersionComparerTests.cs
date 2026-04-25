using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Tests.Models;

public class VersionComparerTests
{
    [Theory]
    [InlineData("1.2.0", "1.10.0")]
    [InlineData("1.10.0", "1.10.1")]
    [InlineData("1.10.1", "2.0.0")]
    [InlineData("0.9.0", "1.0.0")]
    [InlineData("1.0", "1.0.1")]
    public void Compare_NumericOrdering(string lower, string higher)
    {
        Assert.True(VersionComparer.Instance.Compare(lower, higher) < 0,
            $"Expected '{lower}' < '{higher}'");
        Assert.True(VersionComparer.Instance.Compare(higher, lower) > 0,
            $"Expected '{higher}' > '{lower}'");
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    public void Compare_PreReleaseSortsLowerThanStable(string pre, string stable)
    {
        Assert.True(VersionComparer.Instance.Compare(pre, stable) < 0);
    }

    [Fact]
    public void Compare_EqualVersions()
    {
        Assert.Equal(0, VersionComparer.Instance.Compare("1.2.3", "1.2.3"));
        Assert.Equal(0, VersionComparer.Instance.Compare("1.0.0-rc.1", "1.0.0-rc.1"));
    }

    [Fact]
    public void Compare_NullsHandled()
    {
        Assert.Equal(0, VersionComparer.Instance.Compare(null, null));
        Assert.True(VersionComparer.Instance.Compare(null, "1.0.0") < 0);
        Assert.True(VersionComparer.Instance.Compare("1.0.0", null) > 0);
    }

    [Fact]
    public void Compare_OrderByDescending_PicksLatest()
    {
        var versions = new[] { "1.2.0", "1.10.0", "1.10.1", "2.0.0", "0.9.0" };
        var sorted = versions.OrderByDescending(v => v, VersionComparer.Instance).ToList();

        Assert.Equal("2.0.0", sorted[0]);
        Assert.Equal("1.10.1", sorted[1]);
        Assert.Equal("1.10.0", sorted[2]);
        Assert.Equal("1.2.0", sorted[3]);
        Assert.Equal("0.9.0", sorted[4]);
    }
}
