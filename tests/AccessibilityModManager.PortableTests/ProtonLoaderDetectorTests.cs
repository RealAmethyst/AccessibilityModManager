using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.PortableTests;

public sealed class ProtonLoaderDetectorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-loader-detect-" + Guid.NewGuid().ToString("N"));
    private static readonly string PeFile = typeof(ProtonLoaderDetector).Assembly.Location;

    [Fact]
    public void RecognizesInstalledBepInExProxyWithMatchingGameArchitecture()
    {
        var game = PrepareGame();
        File.Copy(PeFile, Path.Combine(game, "winhttp.dll"));
        var marker = Path.Combine(game, "BepInEx", "core", "BepInEx.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.Copy(PeFile, marker);

        var finding = ProtonLoaderDetector.Detect(game, "Game.exe");

        Assert.NotNull(finding);
        Assert.Equal("BepInEx", finding.Loader);
        Assert.Equal("winhttp.dll=n,b", finding.DllOverride);
    }

    [Fact]
    public void ProxyNameAloneDoesNotClaimAWorkingLoader()
    {
        var game = PrepareGame();
        File.Copy(PeFile, Path.Combine(game, "winhttp.dll"));

        Assert.Null(ProtonLoaderDetector.Detect(game, "Game.exe"));
    }

    [Fact]
    public void RecognizesInstalledMelonLoaderWithItsProxy()
    {
        var game = PrepareGame();
        File.Copy(PeFile, Path.Combine(game, "version.dll"));
        var marker = Path.Combine(game, "MelonLoader", "net6", "MelonLoader.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.Copy(PeFile, marker);

        var finding = ProtonLoaderDetector.Detect(game, "Game.exe");

        Assert.NotNull(finding);
        Assert.Equal("MelonLoader", finding.Loader);
        Assert.Equal("version=n,b", finding.DllOverride);
    }

    [Fact]
    public void RecognizesSubfolderFreetypeProxyWithLoaderMarker()
    {
        var game = PrepareGame();
        var nested = Path.Combine(game, "app_digister");
        Directory.CreateDirectory(nested);
        File.Copy(PeFile, Path.Combine(nested, "Game.exe"));
        File.Copy(PeFile, Path.Combine(nested, "freetype.dll"));
        File.Copy(PeFile, Path.Combine(nested, "DSCSModLoader.dll"));

        var finding = ProtonLoaderDetector.Detect(game, "app_digister/Game.exe");

        Assert.NotNull(finding);
        Assert.Equal("DSCSModLoader", finding.Loader);
        Assert.Equal("freetype=n,b", finding.DllOverride);
        Assert.Equal("app_digister/freetype.dll", finding.ProxyPath);
    }

    [Fact]
    public void BrokenProxyIsRefused()
    {
        var game = PrepareGame();
        File.WriteAllText(Path.Combine(game, "winhttp.dll"), "not a PE file");
        var marker = Path.Combine(game, "BepInEx", "core", "BepInEx.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.Copy(PeFile, marker);

        Assert.Throws<InvalidOperationException>(() => ProtonLoaderDetector.Detect(game, "Game.exe"));
    }

    [Fact]
    public void LinkedProxyOutsideGameFolderIsRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        var game = PrepareGame();
        var outside = Path.Combine(Path.GetTempPath(), "amm-proxy-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            File.Copy(PeFile, outside);
            File.CreateSymbolicLink(Path.Combine(game, "winhttp.dll"), outside);
            var marker = Path.Combine(game, "BepInEx", "core", "BepInEx.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.Copy(PeFile, marker);

            Assert.Throws<InvalidOperationException>(() => ProtonLoaderDetector.Detect(game, "Game.exe"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    private string PrepareGame()
    {
        Directory.CreateDirectory(root);
        File.Copy(PeFile, Path.Combine(root, "Game.exe"));
        return root;
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
