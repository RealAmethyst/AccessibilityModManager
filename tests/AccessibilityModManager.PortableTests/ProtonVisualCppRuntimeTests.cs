using System.IO.Compression;
using System.Security.Cryptography;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.PortableTests;

public sealed class ProtonVisualCppRuntimeTests
{
    [Fact]
    public async Task PublishedCyberSleuthPackageRequiresX64VisualCppRuntime()
    {
        var path = Environment.GetEnvironmentVariable("AMM_DSCS_PUBLIC_ZIP");
        if (path is null) return;

        const string expectedHash =
            "fc96dce6eaaf01c63dce6d7b41d8e4d174d471bd404e3747f7a83430f9cf92e6";
        await using (var stream = File.OpenRead(path))
            Assert.Equal(expectedHash, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)));

        using var archive = ZipFile.OpenRead(path);
        Assert.Equal(VisualCppArchitecture.X64, ProtonVisualCppRuntimeDetector.Detect(archive));
    }

    [Fact]
    public void NonPeFilesDoNotRequireVisualCppRuntime()
    {
        Assert.Equal(VisualCppArchitecture.None,
            ProtonVisualCppRuntimeDetector.Detect("not a PE file"u8.ToArray()));

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var file = archive.CreateEntry("files/plugin.dll").Open();
            file.Write("not a PE file"u8);
        }
        stream.Position = 0;
        using var read = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(VisualCppArchitecture.None, ProtonVisualCppRuntimeDetector.Detect(read));
    }

    [Fact]
    public async Task InstalledProtonPrefixReportsPinnedRuntime()
    {
        var proton = Environment.GetEnvironmentVariable("AMM_DSCS_PROTON_EXE");
        var steamRoot = Environment.GetEnvironmentVariable("AMM_DSCS_STEAM_ROOT");
        var compatData = Environment.GetEnvironmentVariable("AMM_DSCS_COMPAT_DATA");
        if (proton is null || steamRoot is null || compatData is null) return;

        var runtime = new ProtonVisualCppRuntime(new HttpClient(), Path.GetTempPath());
        Assert.True(await runtime.IsInstalledAsync(
            new ProtonContext(proton, steamRoot, compatData, "1042550"),
            VisualCppArchitecture.X64));
    }
}
