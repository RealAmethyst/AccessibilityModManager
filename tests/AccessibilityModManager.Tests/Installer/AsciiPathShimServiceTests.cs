using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Installer;

public class AsciiPathShimServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AsciiPathShimService _service;
    private readonly List<string> _junctionsToRemove = new();

    public AsciiPathShimServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_shim_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _service = new AsciiPathShimService(TestLogger.Create());
    }

    public void Dispose()
    {
        // Remove junctions non-recursively first so temp cleanup never follows a link into a target.
        foreach (var j in _junctionsToRemove)
        {
            try { if (Directory.Exists(j)) Directory.Delete(j, recursive: false); } catch { }
        }
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void GetJunctionPath_PutsJunctionAtDriveRootWithName()
    {
        var shim = new AsciiPathShim { JunctionName = "PokemonTCGLive", Reason = "x" };
        var real = Path.Combine(_tempRoot, "Some Folder", "Game Live"); // need not exist

        var junction = _service.GetJunctionPath(shim, real);

        var expectedRoot = Path.GetPathRoot(Path.GetFullPath(real))!;
        Assert.Equal(Path.Combine(expectedRoot, "PokemonTCGLive"), junction);
    }

    [Fact]
    public void JunctionPathExists_TrueForExistingDir_FalseOtherwise()
    {
        var existing = Path.Combine(_tempRoot, "exists");
        Directory.CreateDirectory(existing);

        Assert.True(_service.JunctionPathExists(existing));
        Assert.False(_service.JunctionPathExists(Path.Combine(_tempRoot, "nope")));
    }

    [Fact]
    public async Task CreateJunctionAsync_MakesUsableLinkToTarget()
    {
        var target = Path.Combine(_tempRoot, "real");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "marker.txt"), "hello");

        var junction = Path.Combine(_tempRoot, "AsciiLink");
        _junctionsToRemove.Add(junction);

        await _service.CreateJunctionAsync(junction, target);

        Assert.True(Directory.Exists(junction));
        // Files in the target are visible through the junction — same physical directory.
        Assert.Equal("hello", File.ReadAllText(Path.Combine(junction, "marker.txt")));
    }

    [Fact]
    public async Task CreateJunctionAsync_PathWithSpacesAndUnicode_Succeeds()
    {
        // Mirrors the real PTCGL case: the target path has spaces and an accented character,
        // which exercises the argument quoting in the mklink invocation.
        var target = Path.Combine(_tempRoot, "Pokémon Trading Card Game Live");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Pokemon TCG Live.exe"), "exe");

        var junction = Path.Combine(_tempRoot, "PokemonTCGLive");
        _junctionsToRemove.Add(junction);

        await _service.CreateJunctionAsync(junction, target);

        Assert.True(File.Exists(Path.Combine(junction, "Pokemon TCG Live.exe")));
    }

    [Fact]
    public async Task GetJunctionTarget_ReturnsTargetItPointsAt()
    {
        // Mirrors the PTCGL case (spaces + accented char in the target). GetJunctionTarget reads
        // the link's own reparse data, so it resolves the target without walking into it — this is
        // what lets the install flow validate a fresh junction without racing a settling install.
        var target = Path.Combine(_tempRoot, "Pokémon Trading Card Game Live");
        Directory.CreateDirectory(target);
        var junction = Path.Combine(_tempRoot, "PokemonTCGLive");
        _junctionsToRemove.Add(junction);

        await _service.CreateJunctionAsync(junction, target);

        var resolved = _service.GetJunctionTarget(junction);
        Assert.NotNull(resolved);
        Assert.Equal(Normalize(target), Normalize(resolved!));
    }

    [Fact]
    public void GetJunctionTarget_ReturnsNull_ForPlainDirectory()
    {
        var plain = Path.Combine(_tempRoot, "not-a-link");
        Directory.CreateDirectory(plain);

        Assert.Null(_service.GetJunctionTarget(plain));
    }

    [Fact]
    public void GetJunctionTarget_ReturnsNull_ForMissingPath()
    {
        Assert.Null(_service.GetJunctionTarget(Path.Combine(_tempRoot, "does-not-exist")));
    }

    private static string Normalize(string p) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
}
