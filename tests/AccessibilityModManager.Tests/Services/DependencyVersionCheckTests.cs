using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// Version-aware dependency checks against a real path on disk.
///
/// <para>Written for .NET runtimes, which are the case that forced it: they install side by side,
/// each version in its own folder, and modern .NET no longer writes per-runtime registry entries —
/// a machine carrying 9 and 10 can have nothing under <c>InstalledVersions</c> but a
/// <c>sharedhost</c> key. Registry checks therefore report .NET missing on a machine that has it,
/// and an existence-only file check would force the author to pin one exact patch number, which
/// goes stale the moment a user updates.</para>
/// </summary>
public sealed class DependencyVersionCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amm-depver-" + Guid.NewGuid().ToString("N"));

    public DependencyVersionCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string RuntimeDirWith(params string[] versionFolders)
    {
        var shared = Path.Combine(_root, "Microsoft.WindowsDesktop.App");
        Directory.CreateDirectory(shared);
        foreach (var v in versionFolders) Directory.CreateDirectory(Path.Combine(shared, v));
        return shared;
    }

    private static Dependency SystemDep(string path, string? minVersion) => new()
    {
        Id = "dotnet9",
        Type = "system",
        MinVersion = minVersion,
        Check = new DependencyCheck { FilePath = path }
    };

    private static async Task<DependencyStatus> CheckAsync(Dependency dep)
    {
        var checker = new DependencyChecker(TestLogger.Create());
        var install = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "g",
                DisplayName = "Game",
                Dependencies = [dep]
            },
            PluginId = "p",
            InstallPath = Path.GetTempPath(),
            IsValid = true
        };
        var results = await checker.CheckAsync(install);
        return Assert.Single(results);
    }

    [Fact]
    public async Task ANewerRuntimeThanRequiredSatisfiesIt()
    {
        // The whole point: the author asks for 9.0.0 and the user has 9.0.18. Pinning the exact
        // folder name would have called this missing.
        var dir = RuntimeDirWith("8.0.23", "9.0.18");

        var status = await CheckAsync(SystemDep(dir, "9.0.0"));

        Assert.Equal(DependencyStatusKind.Installed, status.Status);
    }

    [Fact]
    public async Task OnlyOlderRuntimesReadsAsIncompatibleRatherThanMissing()
    {
        // Missing would offer to install it; Incompatible says what is actually wrong.
        var dir = RuntimeDirWith("5.0.17", "8.0.0");

        var status = await CheckAsync(SystemDep(dir, "9.0.0"));

        Assert.Equal(DependencyStatusKind.Incompatible, status.Status);
        Assert.Contains("8.0.0", status.Details);
        Assert.Contains("9.0.0", status.Details);
    }

    /// <summary>
    /// 10 is newer than 9, not older. A plain string comparison gets this backwards, which is
    /// exactly the version this project is on.
    /// </summary>
    [Fact]
    public async Task DoubleDigitVersionsCompareAsNumbers()
    {
        var dir = RuntimeDirWith("9.0.18", "10.0.10");

        var status = await CheckAsync(SystemDep(dir, "10.0.0"));

        Assert.Equal(DependencyStatusKind.Installed, status.Status);
    }

    [Fact]
    public async Task AFolderWithNoVersionedChildrenCannotClaimToPass()
    {
        // The parent folder exists, so existence alone would say Installed — while nothing there
        // establishes any version at all.
        var shared = Path.Combine(_root, "Microsoft.WindowsDesktop.App");
        Directory.CreateDirectory(shared);
        Directory.CreateDirectory(Path.Combine(shared, "notaversion"));

        var status = await CheckAsync(SystemDep(shared, "9.0.0"));

        Assert.Equal(DependencyStatusKind.Incompatible, status.Status);
    }

    [Fact]
    public async Task WithoutAMinVersionItIsStillJustPresence()
    {
        // Every dependency in the live index is this shape, and it must not change meaning.
        var shared = Path.Combine(_root, "Microsoft.WindowsDesktop.App");
        Directory.CreateDirectory(shared);

        var status = await CheckAsync(SystemDep(shared, minVersion: null));

        Assert.Equal(DependencyStatusKind.Installed, status.Status);
    }

    [Fact]
    public async Task AMissingPathIsStillMissing()
    {
        var status = await CheckAsync(SystemDep(Path.Combine(_root, "nope"), "9.0.0"));

        Assert.Equal(DependencyStatusKind.Missing, status.Status);
    }

    [Fact]
    public async Task AFileCarriesItsOwnVersion()
    {
        // A real assembly, so the metadata read is the real one rather than a stub's idea of it.
        var runtimeDll = typeof(object).Assembly.Location;
        Assert.True(File.Exists(runtimeDll));

        var status = await CheckAsync(SystemDep(runtimeDll, "1.0.0"));

        Assert.Equal(DependencyStatusKind.Installed, status.Status);
    }

    [Fact]
    public async Task AFileWithNoReadableVersionCannotClaimToPass()
    {
        var plain = Path.Combine(_root, "loader.txt");
        await File.WriteAllTextAsync(plain, "not a binary");

        var status = await CheckAsync(SystemDep(plain, "9.0.0"));

        Assert.Equal(DependencyStatusKind.Incompatible, status.Status);
    }
}
