using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.PortableTests;

public sealed class UpdateCheckerTests
{
    private const string Tag = "v1.19.0";
    private static readonly Version Version = new(1, 19, 0);
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("verified update test payload");
    private static string Url(string name) => $"https://github.com/RealAmethyst/AccessibilityModManager/releases/download/{Tag}/{name}";
    private static object Asset(string name) => new { name, browser_download_url = Url(name), size = Payload.Length };
    private static string[] BothPlatforms => [
        "AccessibilityModManager-1.19.0-Setup.exe", "AccessibilityModManager-1.19.0-Setup.exe.sha256",
        "AccessibilityModManager-1.19.0-linux-x64.tar.gz", "AccessibilityModManager-1.19.0-linux-x64.tar.gz.sha256",
        "PluginIndexAuthor-0.29.0.exe", "Unrelated-Setup.exe", "Unrelated-Setup.exe.sha256"];

    [Theory]
    [InlineData("win-x64", "AccessibilityModManager-1.19.0-Setup.exe")]
    [InlineData("linux-x64", "AccessibilityModManager-1.19.0-linux-x64.tar.gz")]
    public async Task MixedReleaseSelectsOnlyExactProductPlatformAndMatchingChecksum(string runtime, string name)
    {
        using var handler = new Handler(BothPlatforms.Reverse().ToArray());
        using var http = new HttpClient(handler);
        var checker = new UpdateChecker(http, new LoggerConfiguration().CreateLogger(), runtime);
        var info = await checker.CheckForUpdateAsync(new Version(1, 18, 4, 0));
        Assert.NotNull(info);
        Assert.Equal(Url(name), info.InstallerUrl.AbsoluteUri);
        Assert.Equal(runtime, info.RuntimeIdentifier);
        Assert.EndsWith(name + ".sha256", handler.Requests.Last());
        var path = await checker.DownloadAsync(info, null);
        try { Assert.Equal(Payload, await File.ReadAllBytesAsync(path)); }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public async Task MissingPlatformNeverFallsBackToOtherPlatform(string runtime)
    {
        var other = runtime == "win-x64" ? "linux-x64" : "win-x64";
        var name = UpdateChecker.AssetName(Version, other);
        using var http = new HttpClient(new Handler([name, name + ".sha256"]));
        var checker = new UpdateChecker(http, new LoggerConfiguration().CreateLogger(), runtime);
        Assert.Null(await checker.CheckForUpdateAsync(new Version(1, 18, 4)));
        Assert.Null(checker.LastError);
    }

    [Theory]
    [InlineData("missing-hash")]
    [InlineData("duplicate")]
    [InlineData("wrong-checksum-name")]
    [InlineData("external-url")]
    [InlineData("bad-hash")]
    public async Task AmbiguousOrMismatchedAssetsAreRejected(string fault)
    {
        var name = UpdateChecker.AssetName(Version, "win-x64");
        var names = fault == "missing-hash" ? new[] { name, "Unrelated-Setup.exe.sha256" }
            : fault == "duplicate" ? new[] { name, name, name + ".sha256" }
            : new[] { name, name + ".sha256" };
        using var handler = new Handler(names) { Fault = fault };
        using var http = new HttpClient(handler);
        var checker = new UpdateChecker(http, new LoggerConfiguration().CreateLogger(), "win-x64");
        Assert.Null(await checker.CheckForUpdateAsync(new Version(1, 18, 4)));
        Assert.NotNull(checker.LastError);
    }

    [Theory]
    [InlineData("prerelease")]
    [InlineData("draft")]
    [InlineData("current")]
    public async Task StableUpdaterDoesNotOfferPreviewOrCurrentBuild(string state)
    {
        using var http = new HttpClient(new Handler(BothPlatforms) { Fault = state });
        var checker = new UpdateChecker(http, new LoggerConfiguration().CreateLogger(), "linux-x64");
        Assert.Null(await checker.CheckForUpdateAsync(state == "current" ? new Version(1, 19, 0, 0) : new Version(1, 18, 4)));
    }

    [Fact]
    public async Task DownloadRejectsWrongPlatformAndTampering()
    {
        using var handler = new Handler(BothPlatforms);
        using var http = new HttpClient(handler);
        var checker = new UpdateChecker(http, new LoggerConfiguration().CreateLogger(), "linux-x64");
        var info = (await checker.CheckForUpdateAsync(new Version(1, 18, 4)))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => checker.DownloadAsync(info with { RuntimeIdentifier = "win-x64" }, null));
        await Assert.ThrowsAsync<InvalidDataException>(() => checker.DownloadAsync(info with { Sha256 = new string('0', 64) }, null));
    }

    private sealed class Handler(string[] names) : HttpMessageHandler
    {
        public string? Fault { get; init; }
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            HttpContent content;
            if (url.Contains("api.github.com"))
            {
                var assets = names.Select(name => Fault == "external-url"
                    ? (object)new { name, browser_download_url = "https://example.org/" + name, size = Payload.Length }
                    : Asset(name));
                content = new StringContent(JsonSerializer.Serialize(new { tag_name = Tag, name = "Manager", body = "Test notes",
                    draft = Fault == "draft", prerelease = Fault == "prerelease", assets }));
            }
            else if (url.EndsWith(".sha256"))
            {
                var hash = Convert.ToHexString(SHA256.HashData(Payload));
                var filename = Fault == "wrong-checksum-name" ? "Other-Setup.exe" : Path.GetFileName(url)[..^7];
                content = new StringContent(Fault == "bad-hash" ? "garbage" : "\ufeff" + hash + "  " + filename);
            }
            else content = new ByteArrayContent(Payload);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
