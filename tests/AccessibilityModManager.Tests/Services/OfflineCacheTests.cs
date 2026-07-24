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
/// Audit finding 33: offline, the manager serves the LAST ACCEPTED registry/indexes from a local
/// cache — marked as cached — instead of an empty catalog. The cache is a convenience, never a
/// trust bypass: cached bytes re-run the full acceptance gate (signature, validation, replay
/// highwater), so anything a live fetch would refuse, the cache is refused too.
/// </summary>
public class OfflineCacheTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly RSA _rsa = RSA.Create(2048);

    public OfflineCacheTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _rsa.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------- registry

    [Fact]
    public async Task Registry_NetworkDown_ServesLastAcceptedCopy_MarkedCached()
    {
        var online = MakeRegistryClient(RegistryJson("1.0.0"));
        var live = await online.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"));
        Assert.False(live.FromCache);

        var offline = MakeOfflineRegistryClient();
        var cached = await offline.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"));

        Assert.True(cached.FromCache);
        Assert.NotNull(cached.CachedAtUtc);
        Assert.Equal("1.0.0", cached.Value.RegistryVersion);
        Assert.Equal("plug-a", Assert.Single(cached.Value.Plugins).Id);
    }

    [Fact]
    public async Task Registry_NetworkDown_NoCache_SurfacesOfflineError()
    {
        var offline = MakeOfflineRegistryClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            offline.FetchRegistryAsync(new Uri("https://example.invalid/registry.json")));
        Assert.Contains("no offline copy", ex.Message);
    }

    [Fact]
    public async Task Registry_TamperedCache_RefusedOffline()
    {
        var online = MakeRegistryClient(RegistryJson("1.0.0"));
        await online.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"));

        // Tamper with the cached registry bytes on disk — the signature check must kill it.
        var cachePath = Path.Combine(_tempRoot, "cache", "registry.json");
        File.WriteAllText(cachePath, File.ReadAllText(cachePath).Replace("plug-a", "plug-x"));

        var offline = MakeOfflineRegistryClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            offline.FetchRegistryAsync(new Uri("https://example.invalid/registry.json")));
        Assert.Contains("rejected", ex.Message);
    }

    [Fact]
    public async Task Registry_StaleCacheBelowHighwater_RefusedOffline()
    {
        // Accept v2 live: highwater = 2.0.0, cache = v2.
        var online = MakeRegistryClient(RegistryJson("2.0.0"));
        await online.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"));

        // Swap the cache for a VALIDLY SIGNED older v1 — a cache-level replay attempt. The
        // envelope carries the correct source URL so the refusal proves the HIGHWATER check,
        // not the source binding.
        var v1Json = RegistryJson("1.0.0");
        var envelope = new
        {
            FetchedAtUtc = DateTimeOffset.UtcNow,
            SourceUrl = "https://example.invalid/registry.json",
            RegistryJson = v1Json,
            SignatureBase64 = Sign(v1Json)
        };
        File.WriteAllBytes(Path.Combine(_tempRoot, "cache", "registry.json"),
            JsonSerializer.SerializeToUtf8Bytes(envelope,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var offline = MakeOfflineRegistryClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            offline.FetchRegistryAsync(new Uri("https://example.invalid/registry.json")));
        Assert.Contains("rejected", ex.Message);
    }

    [Fact]
    public async Task Registry_CacheWithoutHighwaterMarker_RefusedOffline()
    {
        var online = MakeRegistryClient(RegistryJson("1.0.0"));
        await online.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"));

        // No marker = no proof this machine ever accepted a registry — deleting the marker must
        // not let a planted cache in through the "first fetch" door.
        File.Delete(Path.Combine(_tempRoot, "registry-highwater.txt"));

        var offline = MakeOfflineRegistryClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            offline.FetchRegistryAsync(new Uri("https://example.invalid/registry.json")));
        Assert.Contains("rejected", ex.Message);
    }

    [Fact]
    public async Task Registry_CacheFromDifferentSourceUrl_RefusedOffline()
    {
        var online = MakeRegistryClient(RegistryJson("1.0.0"));
        await online.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"));

        var offline = MakeOfflineRegistryClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            offline.FetchRegistryAsync(new Uri("https://other.invalid/registry.json")));
        Assert.Contains("rejected", ex.Message);
    }

    // ---------------------------------------------------------------- plugin index

    [Fact]
    public async Task Index_NetworkDown_ServesLastAcceptedCopy_MarkedCached()
    {
        var cacheDir = Path.Combine(_tempRoot, "index-cache");
        var online = new PluginRepoClient(
            new HttpClient(new RouteHandler(_ => IndexJson())), TestLogger.Create(), cacheDir);
        var live = await online.FetchPluginIndexAsync(Entry());
        Assert.False(live.FromCache);

        var offline = new PluginRepoClient(new HttpClient(new FailingHandler()), TestLogger.Create(), cacheDir);
        var cached = await offline.FetchPluginIndexAsync(Entry());

        Assert.True(cached.FromCache);
        Assert.NotNull(cached.CachedAtUtc);
        Assert.Equal("plug-a", cached.Value.PluginId);
    }

    [Fact]
    public async Task Index_TamperedCache_FailsIdentityBinding_Offline()
    {
        var cacheDir = Path.Combine(_tempRoot, "index-cache");
        var online = new PluginRepoClient(
            new HttpClient(new RouteHandler(_ => IndexJson())), TestLogger.Create(), cacheDir);
        await online.FetchPluginIndexAsync(Entry());

        // Rewrite the cached index to claim a different plugin id — identity binding must refuse.
        // (The cache file name is a hash of the id, so find the single envelope by enumeration.)
        var cachePath = Assert.Single(Directory.EnumerateFiles(cacheDir, "*.json"));
        File.WriteAllText(cachePath, File.ReadAllText(cachePath).Replace("plug-a", "plug-x"));

        var offline = new PluginRepoClient(new HttpClient(new FailingHandler()), TestLogger.Create(), cacheDir);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            offline.FetchPluginIndexAsync(Entry()));
        Assert.Contains("rejected", ex.Message);
    }

    [Fact]
    public async Task Index_NetworkDown_NoCache_SurfacesOfflineError()
    {
        var offline = new PluginRepoClient(new HttpClient(new FailingHandler()), TestLogger.Create(),
            Path.Combine(_tempRoot, "index-cache-empty"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            offline.FetchPluginIndexAsync(Entry()));
        Assert.Contains("no offline", ex.Message);
    }

    // ---------------------------------------------------------------- harness

    private string Sign(string json) => Convert.ToBase64String(
        _rsa.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

    private PluginRegistryClient MakeRegistryClient(string registryJson)
    {
        var signature = Sign(registryJson);
        var handler = new RouteHandler(url => url.Contains(".sig") ? signature : registryJson);
        return new PluginRegistryClient(new HttpClient(handler), TestLogger.Create(),
            new RegistrySignatureVerifier(_rsa.ExportSubjectPublicKeyInfoPem(), TestLogger.Create()), _tempRoot);
    }

    private PluginRegistryClient MakeOfflineRegistryClient() =>
        new(new HttpClient(new FailingHandler()), TestLogger.Create(),
            new RegistrySignatureVerifier(_rsa.ExportSubjectPublicKeyInfoPem(), TestLogger.Create()), _tempRoot);

    private static PluginEntry Entry() => new()
    {
        Id = "plug-a",
        Name = "Plug A",
        Author = "Author",
        Description = "desc",
        RepoIndexUrl = new Uri("https://example.invalid/index.json")
    };

    private static string RegistryJson(string version) => $$"""
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
              "links": {}
            }
          ]
        }
        """;

    private static string IndexJson() => """
        {
          "pluginId": "plug-a",
          "repoVersion": "1",
          "generatedAt": "2026-07-24T00:00:00Z",
          "games": [ { "gameId": "game-1", "displayName": "Game 1" } ],
          "releasesByGameId": {
            "game-1": [
              {
                "pluginId": "plug-a",
                "gameId": "game-1",
                "version": "1.0.0",
                "channel": "stable",
                "packageUrl": "https://example.invalid/pkg.zip",
                "sha256": "00"
              }
            ]
          }
        }
        """;

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

    /// <summary>Simulates the network being down: every request throws like a DNS/conn failure.</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("simulated network failure");
    }
}
