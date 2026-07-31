using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.Security;

/// <summary>
/// The registry is the one document with no per-item tolerance: a manager accepts it whole or
/// refuses it whole. So anything ADDED to it has to be provably invisible to managers that predate
/// the addition — and that has to be a test, because the moment a registry naming a signing key is
/// signed and published, every deployed manager reads it. If they refused it, every user's catalog
/// would go dark at once and no later publish could undo it: the broken document is already live and
/// the fix is another publish those same managers cannot read.
///
/// <para>Live since 30 July 2026, so this stopped being a precondition and became a description of
/// production.</para>
///
/// <para><b>Two generations, two tests, and they must not be merged.</b> One pins what the DEPLOYED
/// 1.14.x managers do — those binaries are fixed and can never be changed, so that test is a frozen
/// snapshot and deliberately does not track current code. The other pins what THIS build's real
/// acceptance path does. An earlier version of this file tested neither: it called
/// <c>PluginRegistryValidation</c>, which is the AuthorTool's pre-publish mirror and is not called
/// by <see cref="PluginRegistryClient"/> at all, while its comment claimed it was "written against
/// the manager's OWN acceptance path". The property held; the test did not establish it.</para>
/// </summary>
public sealed class RegistryForwardCompatibilityTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly RSA _rsa = RSA.Create(2048);

    public RegistryForwardCompatibilityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_fwd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _rsa.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    /// <summary>The shape that went live: an entry carrying the index-signing anchor.</summary>
    private const string RegistryWithIndexTrust = """
    {
      "registryVersion": "3",
      "updatedAt": "2026-07-30T00:00:00Z",
      "plugins": [
        {
          "id": "amethyst",
          "name": "Amethyst's mods",
          "author": "Amethyst",
          "description": "Accessibility mods.",
          "repoIndexUrl": "https://accessibilitymods.com/registry/plugins/amethyst/index.json",
          "indexTrust": {
            "scheme": "signed-claims-v1",
            "keyId": "amethyst-2026-07",
            "algorithm": "rsa-pss-sha256",
            "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMIIB\n-----END PUBLIC KEY-----"
          }
        }
      ]
    }
    """;

    // ---- generation 1: the binaries already in users' hands -------------------------------

    /// <summary>
    /// The options 1.14.x deserialized the registry with, written out as a literal ON PURPOSE.
    ///
    /// <para>This is a frozen record of a build that shipped. It must NOT be replaced with a
    /// reference to whatever <see cref="PluginRegistryClient"/> uses today — the whole question is
    /// what the binaries that can no longer be changed do, and pointing this at current code would
    /// make the test silently follow the thing it exists to be independent of.</para>
    /// </summary>
    private static readonly JsonSerializerOptions DeployedManagerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// The registry model as 1.14.x shipped it, reproduced here rather than referenced.
    ///
    /// <para>Deserializing into the live <c>PluginRegistry</c> would have defeated the point, and
    /// imminently: the very next increment adds an <c>IndexTrust</c> member to the production entry
    /// model. From that moment a test pointed at the live type would be demonstrating that the
    /// CURRENT manager reads the field — the opposite of what it claims — while still passing.
    /// These types are a snapshot of code that can never be patched, so they never change.</para>
    /// </summary>
    private sealed class DeployedV114Registry
    {
        public string RegistryVersion { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
        public List<DeployedV114Entry> Plugins { get; set; } = [];
    }

    private sealed class DeployedV114Entry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Author { get; set; } = "";
        public string Description { get; set; } = "";
        public Uri? RepoIndexUrl { get; set; }
        public Uri? Website { get; set; }
        public bool IsBuiltIn { get; set; }
        public Dictionary<string, Uri> Links { get; set; } = [];
        public Dictionary<string, string> Metadata { get; set; } = [];
    }

    [Fact]
    public void Managers_shipped_before_signing_existed_ignore_the_signing_key_entirely()
    {
        // No UnmappedMemberHandling.Disallow and camelCase naming: `indexTrust` is simply dropped.
        // That is the entire reason anchoring a key on 30 July did not take every user's catalog
        // down, and it is a property of code that can never be patched.
        var registry = JsonSerializer.Deserialize<DeployedV114Registry>(
            RegistryWithIndexTrust, DeployedManagerOptions);

        Assert.NotNull(registry);
        var entry = Assert.Single(registry!.Plugins);
        Assert.Equal("amethyst", entry.Id);
        Assert.Equal("https://accessibilitymods.com/registry/plugins/amethyst/index.json",
            entry.RepoIndexUrl!.AbsoluteUri);

        // And the member really was invisible to them, rather than merely surviving the parse:
        // nothing on the shipped entry model could hold it.
        Assert.DoesNotContain(typeof(DeployedV114Entry).GetProperties(),
            p => p.Name.Contains("Trust", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Managers_shipped_before_a_member_existed_ignore_it_at_the_top_level_and_per_entry()
    {
        // Not only `indexTrust`: managers are on whatever version they are on, and the registry has
        // to stay readable by all of them for the ecosystem to be extendable at all.
        //
        // Scoped deliberately to members these binaries never knew. It says nothing about members
        // added INSIDE `indexTrust`, which current builds refuse by design — that block is versioned
        // by its scheme name and extending it is what a scheme bump is for.
        var withFutureMembers = """
        {
          "registryVersion": "3",
          "updatedAt": "2026-07-30T00:00:00Z",
          "somethingAddedLater": { "nested": [1, 2, 3] },
          "plugins": [
            {
              "id": "amethyst",
              "name": "Amethyst's mods",
              "author": "Amethyst",
              "description": "Accessibility mods.",
              "repoIndexUrl": "https://accessibilitymods.com/registry/plugins/amethyst/index.json",
              "aFieldFromTheFuture": "ignored"
            }
          ]
        }
        """;

        var registry = JsonSerializer.Deserialize<DeployedV114Registry>(
            withFutureMembers, DeployedManagerOptions);

        Assert.NotNull(registry);
        Assert.Single(registry!.Plugins);
    }

    // ---- generation 2: this build's real acceptance path ---------------------------------

    [Fact]
    public async Task This_builds_own_acceptance_path_takes_the_live_registry_whole()
    {
        // Through PluginRegistryClient itself — signature verification, per-entry validation and the
        // replay high-water — rather than through a validator production never calls.
        var registry = await FetchAsync(RegistryWithIndexTrust);

        var entry = Assert.Single(registry.Plugins);
        Assert.Equal("amethyst", entry.Id);
    }

    [Fact]
    public async Task This_builds_own_acceptance_path_ignores_members_it_does_not_know()
    {
        var withFutureMembers = RegistryWithIndexTrust
            .Replace("\"registryVersion\": \"3\",",
                "\"registryVersion\": \"3\", \"somethingAddedLater\": { \"nested\": [1, 2, 3] },")
            .Replace("\"id\": \"amethyst\",", "\"id\": \"amethyst\", \"aFieldFromTheFuture\": \"ignored\",");

        var registry = await FetchAsync(withFutureMembers);

        Assert.Single(registry.Plugins);
    }

    private async Task<PluginRegistry> FetchAsync(string registryJson)
    {
        var signature = Convert.ToBase64String(
            _rsa.SignData(Encoding.UTF8.GetBytes(registryJson), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        var handler = new RouteHandler(url => url.Contains(".sig") ? signature : registryJson);
        var verifier = new RegistrySignatureVerifier(_rsa.ExportSubjectPublicKeyInfoPem(), TestLogger.Create());
        var client = new PluginRegistryClient(new HttpClient(handler), TestLogger.Create(), verifier, _tempRoot);

        var fetched = await client.FetchRegistryAsync(new Uri("https://example.invalid/plugin-registry.json"));
        return fetched.Value;
    }

    // ---- the index side ------------------------------------------------------------------

    [Fact]
    public void A_published_index_carrying_a_signature_block_is_still_readable()
    {
        // This one always did go through the real path: PluginRepoClient.ValidateIndex calls
        // PluginIndexValidation.Validate, so a `proof` member being ignored here is the behaviour
        // deployed managers actually have.
        var index = """
        {
          "pluginId": "amethyst",
          "repoVersion": "1",
          "generatedAt": "2026-07-30T00:00:00Z",
          "games": [
            { "gameId": "game1", "displayName": "Game one", "modName": "Mod" }
          ],
          "releasesByGameId": {
            "game1": [
              {
                "gameId": "game1",
                "pluginId": "amethyst",
                "version": "1.0.0",
                "channel": "stable",
                "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "packageUrl": "https://accessibilitymods.com/releases/game1-1.0.0.zip"
              }
            ]
          },
          "proof": {
            "scheme": "signed-claims-v1",
            "keyId": "amethyst-2026-07",
            "algorithm": "rsa-pss-sha256",
            "claims": [],
            "manifest": "not-inspected-here"
          }
        }
        """;

        var report = PluginIndexValidation.Validate("amethyst", index);

        Assert.Empty(report.TrustErrors);
        Assert.Empty(report.UnobtainableReleases);
    }
}
