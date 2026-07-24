using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// Wave-3 audit coverage: registry replay guard (finding 19), required signature verifier (40),
/// registry link validation (7), index identity binding (13), and safe-id enforcement (14).
/// </summary>
public class TrustHardeningTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly RSA _rsa = RSA.Create(2048);

    public TrustHardeningTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_trust_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _rsa.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------- registry client

    [Fact]
    public void RegistryClient_RefusesToExistWithoutVerifier()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PluginRegistryClient(new HttpClient(), TestLogger.Create(), null!));
    }

    [Fact]
    public async Task RegistryClient_SignedRegistry_FetchesAndRecordsHighwater()
    {
        var client = MakeRegistryClient(RegistryJson("1.0.0"));
        var registry = await client.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"));
        Assert.Equal("1.0.0", registry.RegistryVersion);
        var marker = File.ReadAllLines(Path.Combine(_tempRoot, "registry-highwater.txt"));
        Assert.Equal("1.0.0", marker[0].Trim());
        Assert.Equal(64, marker[1].Trim().Length); // content hash recorded alongside the version
    }

    [Fact]
    public async Task RegistryClient_SameVersionDifferentContent_RefusedAsReplay()
    {
        var first = MakeRegistryClient(RegistryJson("1.0.0"));
        await first.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"));

        // Same version, different (validly signed) bytes — the replay guard must catch it.
        var changed = MakeRegistryClient(RegistryJson("1.0.0", extraLinkUrl: "https://example.invalid/discord"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            changed.FetchRegistryAsync(new Uri("https://example.invalid/registry.json")));
        Assert.Contains("without a version bump", ex.Message);
    }

    [Fact]
    public async Task RegistryClient_RejectedRegistry_DoesNotAdvanceTheMarker()
    {
        var v1 = MakeRegistryClient(RegistryJson("1.0.0"));
        await v1.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"));

        // v3 is signed but malformed (http link) — it must be refused WITHOUT pinning v3.
        var badV3 = MakeRegistryClient(RegistryJson("3.0.0", extraLinkUrl: "http://example.invalid/x"));
        await Assert.ThrowsAsync<System.Security.SecurityException>(() =>
            badV3.FetchRegistryAsync(new Uri("https://example.invalid/registry.json")));

        // A valid v2 must still be acceptable afterwards.
        var v2 = MakeRegistryClient(RegistryJson("2.0.0"));
        var registry = await v2.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"));
        Assert.Equal("2.0.0", registry.RegistryVersion);
    }

    [Fact]
    public void JunctionName_UnsafeValues_Refused()
    {
        var shimService = new AccessibilityModManager.Infrastructure.Installer.AsciiPathShimService(TestLogger.Create());
        var real = Path.Combine(_tempRoot, "real-game");

        Assert.Throws<InvalidOperationException>(() => shimService.GetJunctionPath(
            new AsciiPathShim { JunctionName = "..\\evil", Reason = "r" }, real));
        Assert.Throws<InvalidOperationException>(() => shimService.GetJunctionPath(
            new AsciiPathShim { JunctionName = "Pokémon", Reason = "r" }, real)); // non-ASCII
        Assert.Throws<InvalidOperationException>(() => shimService.GetJunctionPath(
            new AsciiPathShim { JunctionName = "C:\\evil", Reason = "r" }, real));

        // The real live value keeps working.
        var path = shimService.GetJunctionPath(
            new AsciiPathShim { JunctionName = "PokemonTCGLive", Reason = "r" }, real);
        Assert.EndsWith("PokemonTCGLive", path);
    }

    [Fact]
    public async Task RegistryClient_OlderThanSeenVersion_RefusedAsReplay()
    {
        var newer = MakeRegistryClient(RegistryJson("2.0.0"));
        await newer.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"));

        var older = MakeRegistryClient(RegistryJson("1.5.0"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            older.FetchRegistryAsync(new Uri("https://example.invalid/registry.json")));
        Assert.Contains("older than", ex.Message);

        // Same version as the high-water mark is fine (normal refetch).
        var same = MakeRegistryClient(RegistryJson("2.0.0"));
        await same.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"));
    }

    [Fact]
    public async Task RegistryClient_TamperedRegistry_Refused()
    {
        var json = RegistryJson("1.0.0");
        var client = MakeRegistryClient(json, tamperAfterSigning: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchRegistryAsync(new Uri("https://example.invalid/registry.json")));
    }

    [Fact]
    public async Task RegistryClient_NonHttpsExtraLink_Refused()
    {
        var json = RegistryJson("1.0.0", extraLinkUrl: "http://example.invalid/discord");
        var client = MakeRegistryClient(json);
        var ex = await Assert.ThrowsAsync<System.Security.SecurityException>(() =>
            client.FetchRegistryAsync(new Uri("https://example.invalid/registry.json")));
        Assert.Contains("link", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- repo index client

    [Fact]
    public async Task RepoClient_IndexClaimingDifferentPluginId_Refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FetchIndexAsync(IndexJson(pluginId: "impostor")));
        Assert.Contains("identity mismatch", ex.Message);
    }

    [Fact]
    public async Task RepoClient_ReleaseClaimingDifferentPluginId_Refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FetchIndexAsync(IndexJson(releasePluginId: "impostor")));
        Assert.Contains("claims plugin id", ex.Message);
    }

    [Fact]
    public async Task RepoClient_ReleaseFiledUnderWrongGame_Refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FetchIndexAsync(IndexJson(releaseGameId: "other-game")));
        Assert.Contains("claims game id", ex.Message);
    }

    [Fact]
    public async Task RepoClient_UnsafeGameId_Refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FetchIndexAsync(IndexJson(gameId: "games/escape")));
        Assert.Contains("letters, digits", ex.Message);
    }

    [Fact]
    public async Task RepoClient_WellFormedIndex_Loads()
    {
        var index = await FetchIndexAsync(IndexJson());
        Assert.Equal("plug-a", index.PluginId);
        Assert.Single(index.Games);
    }

    // ---------------------------------------------------------------- safe ids

    [Theory]
    [InlineData("melonloader")]
    [InlineData("ptcgl")]
    [InlineData("dotnet-10.desktop_x64")]
    public void EnsureSafeId_AcceptsNormalIds(string id)
    {
        Assert.Equal(id, PathSafety.EnsureSafeId(id, "id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("C:evil")]
    [InlineData("space id")]
    public void EnsureSafeId_RejectsUnsafeIds(string? id)
    {
        Assert.Throws<InvalidOperationException>(() => PathSafety.EnsureSafeId(id, "id"));
    }

    // ---------------------------------------------------------------- harness

    private PluginRegistryClient MakeRegistryClient(string registryJson, bool tamperAfterSigning = false)
    {
        var signature = Convert.ToBase64String(
            _rsa.SignData(Encoding.UTF8.GetBytes(registryJson), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        if (tamperAfterSigning)
            registryJson = registryJson.Replace("plug-a", "plug-x");

        var handler = new RouteHandler(url =>
            url.Contains(".sig") ? signature : registryJson);
        var verifier = new RegistrySignatureVerifier(_rsa.ExportSubjectPublicKeyInfoPem(), TestLogger.Create());
        return new PluginRegistryClient(new HttpClient(handler), TestLogger.Create(), verifier, _tempRoot);
    }

    private static string RegistryJson(string version, string? extraLinkUrl = null)
    {
        var links = extraLinkUrl == null ? "{}" : $"{{ \"discord\": \"{extraLinkUrl}\" }}";
        return $$"""
        {
          "registryVersion": "{{version}}",
          "updatedAt": "2026-07-24T00:00:00Z",
          "plugins": [
            {
              "id": "plug-a",
              "name": "Plug A",
              "author": "Author",
              "description": "desc",
              "repoIndexUrl": "https://example.invalid/index.json",
              "links": {{links}}
            }
          ]
        }
        """;
    }

    private async Task<PluginRepoIndex> FetchIndexAsync(string indexJson)
    {
        var client = new PluginRepoClient(new HttpClient(new RouteHandler(_ => indexJson)), TestLogger.Create());
        var entry = new PluginEntry
        {
            Id = "plug-a",
            Name = "Plug A",
            Author = "Author",
            Description = "desc",
            RepoIndexUrl = new Uri("https://example.invalid/index.json")
        };
        return await client.FetchPluginIndexAsync(entry);
    }

    private static string IndexJson(
        string pluginId = "plug-a", string gameId = "game-1",
        string? releasePluginId = null, string? releaseGameId = null)
    {
        releasePluginId ??= pluginId;
        releaseGameId ??= gameId;
        return $$"""
        {
          "pluginId": "{{pluginId}}",
          "repoVersion": "1",
          "generatedAt": "2026-07-24T00:00:00Z",
          "games": [ { "gameId": "{{gameId}}", "displayName": "Game 1" } ],
          "releasesByGameId": {
            "{{gameId}}": [
              {
                "pluginId": "{{releasePluginId}}",
                "gameId": "{{releaseGameId}}",
                "version": "1.0.0",
                "channel": "stable",
                "packageUrl": "https://example.invalid/pkg.zip",
                "sha256": "00"
              }
            ]
          }
        }
        """;
    }

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _respond;
        public RouteHandler(Func<string, string> respond) { _respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_respond(request.RequestUri!.AbsoluteUri))
            });
    }
}
