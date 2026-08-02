using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.CatalogClaims;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// Who may sign each plugin's catalog is decided once, when a registry is accepted, and every
/// component reads that one answer.
///
/// <para>These go through <see cref="PluginRegistryClient.FetchRegistryAsync"/> rather than calling
/// the reader, because the thing under test is that the acceptance gate CONSULTS it and stamps the
/// result. A registry accepted without being stamped hands every consumer an entry that looks
/// unsigned.</para>
/// </summary>
public sealed class RegistryTrustResolutionTests : IDisposable
{
    private readonly string _root;
    private readonly RSA _registryKey = RSA.Create(2048);
    private readonly RSA _pluginKey = ClaimTestKeys.Primary;

    public RegistryTrustResolutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ammtest_regtrust_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _registryKey.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<PluginRegistry> AcceptAsync(string registryJson)
    {
        var signature = Convert.ToBase64String(_registryKey.SignData(
            Encoding.UTF8.GetBytes(registryJson), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

        var client = new PluginRegistryClient(
            new HttpClient(new RouteHandler(url => url.Contains(".sig") ? signature : registryJson)),
            TestLogger.Create(),
            new RegistrySignatureVerifier(_registryKey.ExportSubjectPublicKeyInfoPem(), TestLogger.Create()),
            _root);

        return (await client.FetchRegistryAsync(new Uri("https://example.invalid/registry.json"))).Value;
    }

    [Fact]
    public async Task PluginWithNoSigningKey_ResolvesToNone()
    {
        var registry = await AcceptAsync(Registry(Entry("plug-a")));

        Assert.Equal(IndexTrustStatus.None, Assert.Single(registry.Plugins).IndexTrust.Status);
    }

    [Fact]
    public async Task PluginWithASigningKey_ResolvesToAnchored_KeepingTheUrlExactly()
    {
        // The trust context is hashed over the index address as the REGISTRY spells it. A Uri
        // round-trip normalises percent-encoding case and default ports, and the resulting anchor
        // would fail every signature check with nothing naming the cause.
        const string url = "https://Example.Invalid:443/a%2Fb/index.json";
        var registry = await AcceptAsync(Registry(Entry("plug-a", url: url, anchored: true)));

        var trust = Assert.Single(registry.Plugins).IndexTrust;

        Assert.Equal(IndexTrustStatus.Anchored, trust.Status);
        Assert.Equal(url, trust.Anchor!.RepoIndexUrl);
        Assert.NotEqual(url, new Uri(url).AbsoluteUri);   // the normalisation this avoids is real
    }

    [Fact]
    public async Task OneBrokenAnchor_RefusesItsOwnPlugin_AndLeavesTheRestWorking()
    {
        // The settled multi-author rule. Under the alternative — refuse the whole registry — one
        // author's typo darkens every user's entire catalog, including everyone else's mods.
        var registry = await AcceptAsync(Registry(
            Entry("plug-a", anchored: true),
            Entry("plug-b", trustValue: """{"scheme":"signed-claims-v1","keyId":"k","algorithm":"rsa-pss-sha256","publicKeyPem":"not a key"}"""),
            Entry("plug-c")));

        var byId = registry.Plugins.ToDictionary(p => p.Id, p => p.IndexTrust);

        Assert.Equal(IndexTrustStatus.Anchored, byId["plug-a"].Status);
        Assert.Equal(IndexTrustStatus.Unusable, byId["plug-b"].Status);
        Assert.Equal(IndexTrustStatus.None, byId["plug-c"].Status);
        Assert.False(string.IsNullOrWhiteSpace(byId["plug-b"].Reason));
    }

    [Fact]
    public async Task AnchorBlockThatIsNotAnObject_IsUnusable_NeverNone()
    {
        // "There is no anchor" is the permission to read an unsigned catalog. A present-but-broken
        // anchor must never be able to grant it.
        var registry = await AcceptAsync(Registry(Entry("plug-a", trustValue: "\"yes\"")));

        Assert.Equal(IndexTrustStatus.Unusable, Assert.Single(registry.Plugins).IndexTrust.Status);
    }

    [Fact]
    public async Task OneEntryRepeatingAMember_RefusesOnlyThatPlugin()
    {
        // A repeated member is ambiguous and must refuse — but LOCALLY. Refusing it by parsing the
        // whole document strictly reads as more careful and is the multi-author failure arriving
        // through the parser: the parser throws before anything knows whose entry it was, and every
        // other author disappears with them.
        var json = Registry(Entry("plug-a", anchored: true), Entry("plug-b"))
            .Replace("\"id\": \"plug-b\",", "\"id\": \"plug-b\", \"name\": \"Twice\",");

        var registry = await AcceptAsync(json);
        var byId = registry.Plugins.ToDictionary(p => p.Id, p => p.IndexTrust);

        Assert.Equal(IndexTrustStatus.Anchored, byId["plug-a"].Status);
        Assert.Equal(IndexTrustStatus.Unusable, byId["plug-b"].Status);
        Assert.Contains("twice", byId["plug-b"].Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegistryRepeatingATopLevelMember_IsRefusedAsADocument()
    {
        // Two `plugins` arrays is two registries, and that is not localisable to any one author.
        var json = Registry(Entry("plug-a")).Replace(
            "\"registryVersion\": \"9.0.0\",", "\"registryVersion\": \"9.0.0\", \"registryVersion\": \"1.0.0\",");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptAsync(json));

        Assert.Contains("repeats a top-level entry", ex.Message);
    }

    // The reader's other document-level refusals — a non-object root, a missing or non-array
    // `plugins` — are unreachable from here: deserializing into PluginRegistry fails on those first.
    // They are still checked, because the AuthorTool calls the same reader on registries it has not
    // deserialized, and IndexTrustReaderTests exercises them directly.

    [Fact]
    public async Task ServedIndexTrust_CannotPopulateTheResolution_ByDeserialization()
    {
        // The registry document carries a member named indexTrust, and PluginEntry now has a
        // property of that name. If deserialization could reach it, the served document would be
        // choosing its own trust anchor — the thing under suspicion deciding what it is checked
        // against. It reaches the entry only through the gate, and only after the signature.
        var hostile = """
            {"status": 2, "anchor": {"pluginId":"plug-a","repoIndexUrl":"https://x.invalid/i.json",
            "scheme":"signed-claims-v1","keyId":"k","algorithm":"rsa-pss-sha256","publicKeyPem":"x"}}
            """;

        var entry = JsonSerializer.Deserialize<PluginEntry>($$"""
            {"id":"plug-a","name":"Plug A","author":"Author","description":"desc",
             "repoIndexUrl":"https://example.invalid/index.json","indexTrust":{{hostile}}}
            """, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        Assert.Equal(IndexTrustStatus.Unresolved, entry.IndexTrust.Status);
        Assert.Null(entry.IndexTrust.Anchor);
    }

    [Fact]
    public void ResolvingTwice_Throws()
    {
        // Two gates would mean two answers, with the later one winning by arriving later. That is a
        // downgrade path, not a correction.
        var entry = TestPluginEntry.Unanchored();

        Assert.Throws<InvalidOperationException>(() =>
            entry.ResolveIndexTrust(IndexTrustResolution.NoAnchor));
    }

    [Fact]
    public void ResolvingToUnresolved_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            TestPluginEntry.Unresolved().ResolveIndexTrust(IndexTrustResolution.Unresolved));
    }

    // ------------------------------------------------------------------ fixtures

    /// <param name="trustValue">The raw JSON VALUE of the indexTrust member, when one is wanted.</param>
    private string Entry(
        string id, string? url = null, bool anchored = false, string? trustValue = null)
    {
        var value = trustValue
                    ?? (anchored
                        ? $$"""{"scheme":"signed-claims-v1","keyId":"k1","algorithm":"rsa-pss-sha256","publicKeyPem":{{JsonSerializer.Serialize(_pluginKey.ExportSubjectPublicKeyInfoPem())}}}"""
                        : null);

        var trust = value is null ? "" : ", \"indexTrust\": " + value;

        return $$"""
            {
              "id": "{{id}}",
              "name": "Plug A",
              "author": "Author",
              "description": "desc",
              "repoIndexUrl": "{{url ?? $"https://example.invalid/{id}/index.json"}}"{{trust}}
            }
            """;
    }

    private static string Registry(params string[] entries) => $$"""
        {
          "registryVersion": "9.0.0",
          "updatedAt": "2026-08-02T00:00:00Z",
          "plugins": [{{string.Join(",", entries)}}]
        }
        """;
}
