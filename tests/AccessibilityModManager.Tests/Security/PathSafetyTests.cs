using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Tests.Security;

public class PathSafetyTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "ammtest_pathsafety");

    [Fact]
    public void CombineContained_AllowsNormalSegments()
    {
        var result = PathSafety.CombineContained(Root, "plugin-a", "game-1", "receipt.json");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Root, "plugin-a", "game-1", "receipt.json")),
            result);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\escape")]
    [InlineData("a/../../escape")]
    public void CombineContained_RejectsTraversal(string evilSegment)
    {
        Assert.Throws<InvalidOperationException>(
            () => PathSafety.CombineContained(Root, evilSegment, "receipt.json"));
    }

    [Fact]
    public void CombineContained_RejectsAbsoluteSegment()
    {
        var absoluteElsewhere = Path.Combine(Path.GetTempPath(), "somewhere-else");
        Assert.Throws<InvalidOperationException>(
            () => PathSafety.CombineContained(Root, absoluteElsewhere));
    }

    [Fact]
    public void IsContained_TrueForChild_FalseForOutside()
    {
        Assert.True(PathSafety.IsContained(Root, Path.Combine(Root, "child", "f.txt")));
        Assert.False(PathSafety.IsContained(Root, Path.Combine(Path.GetTempPath(), "other-root", "f.txt")));
    }
}
