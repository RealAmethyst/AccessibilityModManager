using System.IO.Compression;
using System.Security;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Security;

public class SafeZipExtractorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SafeZipExtractor _extractor;

    public SafeZipExtractorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ammtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _extractor = new SafeZipExtractor(TestLogger.Create());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task ExtractAsync_ExtractsValidZip()
    {
        var zipPath = CreateTestZip(("hello.txt", "world"), ("sub/nested.txt", "content"));
        var extractDir = Path.Combine(_tempDir, "output");

        await _extractor.ExtractAsync(zipPath, extractDir);

        Assert.True(File.Exists(Path.Combine(extractDir, "hello.txt")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(extractDir, "hello.txt")));
        Assert.True(File.Exists(Path.Combine(extractDir, "sub", "nested.txt")));
    }

    [Fact]
    public async Task ExtractAsync_ThrowsOnZipSlipAttempt()
    {
        var zipPath = Path.Combine(_tempDir, "evil.zip");
        using (var stream = File.Create(zipPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../../evil.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("malicious");
        }

        var extractDir = Path.Combine(_tempDir, "output");
        await Assert.ThrowsAsync<SecurityException>(() =>
            _extractor.ExtractAsync(zipPath, extractDir));
    }

    [Fact]
    public async Task ExtractAsync_ThrowsOnRootedEntryName()
    {
        // A rooted entry name makes Path.Combine discard the target entirely; containment must
        // reject the resolved outside path.
        var zipPath = CreateTestZip(("C:\\evil\\payload.txt", "malicious"));
        var extractDir = Path.Combine(_tempDir, "output");

        await Assert.ThrowsAsync<SecurityException>(() =>
            _extractor.ExtractAsync(zipPath, extractDir));
    }

    [Fact]
    public async Task ExtractAsync_TargetWithTrailingSeparator_Succeeds()
    {
        // Regression for the doubled-separator false positive: extracting into a destination
        // written with a trailing separator (the shape a bare drive root like "D:\" arrives in)
        // used to fail every entry as "Zip slip detected".
        var zipPath = CreateTestZip(("data/file.txt", "hello"));
        var extractDir = Path.Combine(_tempDir, "output") + Path.DirectorySeparatorChar;

        await _extractor.ExtractAsync(zipPath, extractDir);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(_tempDir, "output", "data", "file.txt")));
    }

    [Fact]
    public async Task ExtractAsync_RespectssCancellation()
    {
        var zipPath = CreateTestZip(("a.txt", "data"));
        var extractDir = Path.Combine(_tempDir, "output");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _extractor.ExtractAsync(zipPath, extractDir, cts.Token));
    }

    private string CreateTestZip(params (string name, string content)[] entries)
    {
        var zipPath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.zip");
        using var stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return zipPath;
    }
}
