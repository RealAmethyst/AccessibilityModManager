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
    public void CombineContained_RootWithTrailingSeparator_StillAcceptsChildren()
    {
        // Regression for the doubled-separator prefix bug: a root written "...\dir\" used to
        // reject every legitimate child.
        var result = PathSafety.CombineContained(Root + Path.DirectorySeparatorChar, "plugin-a");
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, "plugin-a")), result);
    }

    [Fact]
    public void IsContained_TrueForChild_FalseForOutside()
    {
        Assert.True(PathSafety.IsContained(Root, Path.Combine(Root, "child", "f.txt")));
        Assert.False(PathSafety.IsContained(Root, Path.Combine(Path.GetTempPath(), "other-root", "f.txt")));
    }

    [Fact]
    public void IsContained_IsCaseInsensitive_LikeWindowsPaths()
    {
        Assert.True(PathSafety.IsContained(Root.ToUpperInvariant(), Path.Combine(Root.ToLowerInvariant(), "child")));
    }

    [Fact]
    public void IsContained_RootWithTrailingSeparator_TrueForChild()
    {
        Assert.True(PathSafety.IsContained(Root + Path.DirectorySeparatorChar, Path.Combine(Root, "child")));
    }

    [Fact]
    public void IsContained_BareDriveRoot_TrueForChild()
    {
        // "D:\" trims to itself (still ends in a separator) — the prefix logic must not double it.
        var driveRoot = Path.GetPathRoot(Root)!;
        Assert.True(PathSafety.IsContained(driveRoot, Path.Combine(driveRoot, "anything")));
    }

    [Fact]
    public void IsContained_SiblingWithSharedPrefix_False()
    {
        // "...\game2" must not count as inside "...\game" just because the strings share a prefix.
        Assert.False(PathSafety.IsContained(Root, Root + "2"));
    }

    [Fact]
    public void EnsureContained_ReturnsFullPath_OrThrowsWithDescription()
    {
        var child = Path.Combine(Root, "child");
        Assert.Equal(Path.GetFullPath(child), PathSafety.EnsureContained(Root, child, "test target"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => PathSafety.EnsureContained(Root, Path.Combine(Path.GetTempPath(), "elsewhere"), "test target"));
        Assert.Contains("test target", ex.Message);
    }

    [Theory]
    [InlineData("/Updater/1.5.0/", "Updater\\1.5.0")] // the live PTCG value — leading + trailing slash
    [InlineData("Updater/1.5.0", "Updater\\1.5.0")]
    [InlineData("Updater/1.5.0/", "Updater\\1.5.0")]
    [InlineData("\\Updater\\", "Updater")]
    [InlineData("a//b", "a\\b")]
    [InlineData("a\\b\\", "a\\b")]
    [InlineData("./x/.", "x")]
    [InlineData(".", "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NormalizeRelativeDir_HealsAuthoringNoise(string? input, string expected)
    {
        Assert.Equal(expected, PathSafety.NormalizeRelativeDir(input, "targetDir"));
    }

    [Theory]
    [InlineData("C:\\evil")]
    [InlineData("C:evil")]
    [InlineData("\\\\server\\share")]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("a/..")]
    public void NormalizeRelativeDir_RejectsAbsoluteAndTraversal(string input)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PathSafety.NormalizeRelativeDir(input, "targetDir"));
        Assert.Contains("targetDir", ex.Message);
    }

    [Theory]
    [InlineData("tool.dll", "tool.dll")]
    [InlineData("  tool.dll  ", "tool.dll")]
    public void EnsureLeafFileName_AcceptsPlainNames(string input, string expected)
    {
        Assert.Equal(expected, PathSafety.EnsureLeafFileName(input, "file name"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("sub\\tool.dll")]
    [InlineData("sub/tool.dll")]
    [InlineData("C:\\tool.dll")]
    [InlineData("tool.dll:payload")] // NTFS alternate data stream syntax
    public void EnsureLeafFileName_RejectsNonLeafForms(string? input)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PathSafety.EnsureLeafFileName(input, "file name"));
        Assert.Contains("file name", ex.Message);
    }

    [Fact]
    public void TryNormalizeRelativeDir_BadValue_ReturnsFalseAndEchoesOriginal()
    {
        Assert.False(PathSafety.TryNormalizeRelativeDir("C:\\evil", out var kept));
        Assert.Equal("C:\\evil", kept);

        Assert.True(PathSafety.TryNormalizeRelativeDir("/Updater/1.5.0/", out var healed));
        Assert.Equal("Updater\\1.5.0", healed);
    }
}
