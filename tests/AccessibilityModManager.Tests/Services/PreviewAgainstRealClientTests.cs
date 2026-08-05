using System.Net.Http;
using System.Text;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// Previewing a source through the REAL <see cref="PluginRepoClient"/>, not a stub.
///
/// <para><b>Why this file exists.</b> The first version of the preview shipped broken: it handed the
/// placeholder id "candidate" to the fetch, and the client's identity binding requires the catalog
/// to declare exactly the id it was asked for — so every real catalog was refused, and adding any
/// source failed with "the registry entry 'candidate' served an index claiming to be 'buu420'".
/// The existing tests passed throughout, because their fake repo client returned an index without
/// running the validation the real one runs. They agreed with their fixtures instead of with the
/// engine.</para>
///
/// <para>So these go through the actual client over a canned HTTP response, and one of them serves
/// buu420's real published catalog shape — the exact case that failed for Amethyst.</para>
/// </summary>
public sealed class PreviewAgainstRealClientTests : IDisposable
{
    private readonly string _root;

    public PreviewAgainstRealClientTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ammtest_preview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private UserSourceAdder Adder(string servedJson) => new(
        new PluginRepoClient(
            new HttpClient(new ByteRouteHandler(_ => Encoding.UTF8.GetBytes(servedJson))),
            TestLogger.Create(),
            _root),
        TestLogger.Create());

    [Fact]
    public async Task A_real_catalog_previews_successfully()
    {
        // The regression. Before the fix this failed for every genuine index, because no real
        // catalog declares itself to be called "candidate".
        var preview = await Adder(BuuStyleIndex()).PreviewAsync(
            "https://raw.githubusercontent.com/buu420/buu-s-mods/main/index.json", [], [], [],
            new Dictionary<string, string>());

        Assert.True(preview.CanAdd, preview.Refusal);
        Assert.Equal("buu420", preview.PluginId);
        Assert.Equal("Buu420", preview.DisplayName);
        Assert.Equal(2, preview.GameCount);
    }

    [Fact]
    public async Task Nothing_is_cached_by_a_preview()
    {
        // Declining must leave no trace, and a cached copy must never let a later preview succeed
        // against an address that is actually unreachable.
        await Adder(BuuStyleIndex()).PreviewAsync(
            "https://example.invalid/index.json", [], [], [], new Dictionary<string, string>());

        var cacheDir = Path.Combine(_root, "cache", "indexes");
        var files = Directory.Exists(cacheDir)
            ? Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories)
            : [];

        Assert.Empty(files);
    }

    [Fact]
    public async Task A_catalog_whose_releases_disagree_with_its_own_id_is_still_refused()
    {
        // Adopting the declared id must not mean skipping the consistency checks. The releases
        // inside still have to agree with whatever the document says it is.
        var inconsistent = BuuStyleIndex().Replace(
            "\"pluginId\": \"buu420\",\r\n        \"gameId\": \"ffviiold\"",
            "\"pluginId\": \"someone-else\",\r\n        \"gameId\": \"ffviiold\"");

        var preview = await Adder(inconsistent).PreviewAsync(
            "https://example.invalid/index.json", [], [], [], new Dictionary<string, string>());

        Assert.False(preview.CanAdd);
    }

    [Fact]
    public async Task A_catalog_with_an_unusable_developer_id_is_refused()
    {
        var bad = BuuStyleIndex().Replace("\"pluginId\": \"buu420\",\r\n  \"repoVersion\"",
                                          "\"pluginId\": \"buu420.\",\r\n  \"repoVersion\"");

        var preview = await Adder(bad).PreviewAsync("https://example.invalid/index.json", [], [], [], new Dictionary<string, string>());

        Assert.False(preview.CanAdd);
    }

    [Fact]
    public async Task A_real_catalog_claiming_a_registry_id_is_still_refused()
    {
        // The identity gate has to keep working now that the id comes from the document.
        var preview = await Adder(BuuStyleIndex().Replace("buu420", "amethyst")).PreviewAsync(
            "https://example.invalid/index.json",
            [TestPluginEntry.Unanchored("amethyst")], [], [], new Dictionary<string, string>());

        Assert.False(preview.CanAdd);
        Assert.Contains("amethyst", preview.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The shape of buu420's real published catalog — two games, one release each, an author block.
    /// CRLF, because that is what the live file uses and the replacements above rely on it.
    /// </summary>
    private static string BuuStyleIndex() =>
        string.Join("\r\n",
            "{",
            "  \"pluginId\": \"buu420\",",
            "  \"repoVersion\": \"1\",",
            "  \"generatedAt\": \"2026-08-04T04:21:58.2709007Z\",",
            "  \"games\": [",
            "    { \"gameId\": \"ffviiold\", \"displayName\": \"Final Fantasy VII (2013)\", \"modName\": \"Blind Soldier (2013)\" },",
            "    { \"gameId\": \"ffviinew\", \"displayName\": \"Final Fantasy VII (2026)\", \"modName\": \"Blind Soldier (2026)\" }",
            "  ],",
            "  \"releasesByGameId\": {",
            "    \"ffviiold\": [",
            "      {",
            "        \"pluginId\": \"buu420\",",
            "        \"gameId\": \"ffviiold\",",
            "        \"version\": \"0.1\",",
            "        \"channel\": \"beta\",",
            "        \"packageUrl\": \"https://github.com/buu420/blind-soldier/releases/download/v0.1/ffviiold-v0.1-amm.zip\",",
            "        \"sha256\": \"6a4ff2184eb263cde8b945fe1592f8eb085e74a6a23fd2ca16e82d7a92d337ce\"",
            "      }",
            "    ],",
            "    \"ffviinew\": [",
            "      {",
            "        \"pluginId\": \"buu420\",",
            "        \"gameId\": \"ffviinew\",",
            "        \"version\": \"0.1\",",
            "        \"channel\": \"beta\",",
            "        \"packageUrl\": \"https://github.com/buu420/blind-soldier/releases/download/v0.1/ffviinew-v0.1-amm.zip\",",
            "        \"sha256\": \"63540c959ef5c87a0144fc4102073d9e9b0318630d3d559c45d47d862414cddc\"",
            "      }",
            "    ]",
            "  },",
            "  \"author\": { \"displayName\": \"Buu420\" }",
            "}");
}
