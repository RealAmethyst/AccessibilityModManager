using System.Text.Json.Nodes;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.AuthorTool.ViewModels;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// Naming a signing key in the registry entry — the edit that, once the registry is signed and
/// published, switches a catalog to signed for good.
///
/// <para>The command itself changes only local JSON, so nothing here is irreversible. What matters
/// is that it refuses everything it cannot safely do, because the mistakes it could make are only
/// discovered after the registry is live: an entry anchored to a key nobody holds, or to an address
/// nobody reads, or an existing anchor quietly replaced — which stops every claim already published
/// from verifying.</para>
/// </summary>
public sealed class RegistryAnchorTests : IDisposable
{
    private const string PluginId = "amethyst";
    private const string IndexUrl = "https://accessibilitymods.com/registry/plugins/amethyst/index.json";

    private readonly string _root;
    private readonly ClaimSigningKeyStore _keys;
    private readonly List<string> _dialogs = [];
    private bool _confirmAnswer = true;

    public RegistryAnchorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "anchor-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);

        var config = new AuthorConfigService(TestLogger.Create(), _root);
        var heads = new PublisherHeadStore(config, TestLogger.Create());
        _keys = new ClaimSigningKeyStore(config, heads, TestLogger.Create());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private static string Registry(string version = "2", string? indexUrl = null, string? indexTrust = null) => $$"""
        {
          "registryVersion": "{{version}}",
          "updatedAt": "2026-07-30T00:00:00Z",
          "plugins": [
            {
              "id": "amethyst",
              "name": "Amethyst's mods",
              "author": "Amethyst",
              "description": "Accessibility mods.",
              "repoIndexUrl": "{{indexUrl ?? IndexUrl}}"{{(indexTrust is null ? "" : ",\n      \"indexTrust\": " + indexTrust)}}
            }
          ]
        }
        """;

    /// <summary>
    /// The view model reaches the network in its constructor (it clones or pulls the registry repo),
    /// so these drive the anchor command against JSON set directly rather than loaded from a repo.
    /// </summary>
    private RegistryAdminViewModel Create()
    {
        var vm = new RegistryAdminViewModel(
            new AuthorConfigService(TestLogger.Create(), _root),
            new GitHubService(TestLogger.Create()),
            new GitService(TestLogger.Create()),
            new ServerUploadService(TestLogger.Create()),
            _keys,
            TestLogger.Create(),
            showInfoDialog: (title, _) => _dialogs.Add(title),
            confirmDialog: (_, _) => _confirmAnswer,
            browseForFolder: _ => null,
            browseForFile: (_, _, _) => null,
            navigateBack: () => { });

        return vm;
    }

    private static JsonNode? TrustOf(string json) =>
        JsonNode.Parse(json)?["plugins"]?[0]?["indexTrust"];

    [Fact]
    public void The_key_is_written_into_the_entry_and_the_version_raised()
    {
        _keys.Create(PluginId, "pp");
        var signing = _keys.TryGet(PluginId)!;

        var vm = Create();
        vm.RegistryJsonContent = Registry(version: "2");
        vm.SelectedPluginId = PluginId;

        vm.UseLocalSigningKeyCommand.Execute(null);

        var trust = TrustOf(vm.RegistryJsonContent!);
        Assert.NotNull(trust);
        Assert.Equal(ClaimTrustAnchor.SchemeV1, trust!["scheme"]!.GetValue<string>());
        Assert.Equal(signing.KeyId, trust["keyId"]!.GetValue<string>());
        Assert.Equal(ClaimTrustAnchor.AlgorithmRsaPssSha256, trust["algorithm"]!.GetValue<string>());
        Assert.Equal(signing.PublicKeyPem, trust["publicKeyPem"]!.GetValue<string>());

        // Raised, because managers refuse a registry no newer than one they have already seen.
        Assert.Equal("3", JsonNode.Parse(vm.RegistryJsonContent!)!["registryVersion"]!.GetValue<string>());
    }

    [Fact]
    public void What_it_writes_is_readable_as_an_anchor_by_the_code_that_will_read_it()
    {
        // The strongest check available: the entry it produces has to satisfy the same reader the
        // publish path uses, or signing would switch on and immediately refuse.
        _keys.Create(PluginId, "pp");

        var vm = Create();
        vm.RegistryJsonContent = Registry();
        vm.SelectedPluginId = PluginId;

        vm.UseLocalSigningKeyCommand.Execute(null);

        var anchor = IndexProofService.TryReadAnchor(vm.RegistryJsonContent!, PluginId);

        Assert.NotNull(anchor);
        Assert.Equal(_keys.TryGet(PluginId)!.PublicKeyFingerprint,
            ClaimTrustContext.PublicKeyFingerprint(anchor!.PublicKeyPem));

        // And the key this machine holds opens against it — so the first publish can actually sign.
        _keys.OpenSigner(anchor).Dispose();
    }

    [Fact]
    public void Declining_the_confirmation_changes_nothing()
    {
        _keys.Create(PluginId, "pp");
        _confirmAnswer = false;

        var vm = Create();
        vm.RegistryJsonContent = Registry();
        vm.SelectedPluginId = PluginId;
        var before = vm.RegistryJsonContent;

        vm.UseLocalSigningKeyCommand.Execute(null);

        Assert.Equal(before, vm.RegistryJsonContent);
        Assert.Null(TrustOf(vm.RegistryJsonContent!));
    }

    [Fact]
    public void An_entry_already_naming_a_different_key_is_refused_rather_than_rotated()
    {
        // Replacing it stops every claim already published from verifying the moment the registry
        // goes live. That is a rotation, and it is not what this button is.
        _keys.Create(PluginId, "pp");

        var vm = Create();
        vm.RegistryJsonContent = Registry(indexTrust: """
            {
              "scheme": "signed-claims-v1",
              "keyId": "someone-elses-key",
              "algorithm": "rsa-pss-sha256",
              "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----"
            }
            """);
        vm.SelectedPluginId = PluginId;

        vm.UseLocalSigningKeyCommand.Execute(null);

        Assert.Equal("someone-elses-key", TrustOf(vm.RegistryJsonContent!)!["keyId"]!.GetValue<string>());
        Assert.Contains("That entry already names a different key", _dialogs);
    }

    [Fact]
    public void Naming_the_same_key_twice_is_a_no_op_rather_than_a_version_bump()
    {
        // Otherwise every visit raises the version and invites a pointless re-sign and re-publish.
        _keys.Create(PluginId, "pp");

        var vm = Create();
        vm.RegistryJsonContent = Registry(version: "2");
        vm.SelectedPluginId = PluginId;
        vm.UseLocalSigningKeyCommand.Execute(null);
        var after = vm.RegistryJsonContent;

        vm.UseLocalSigningKeyCommand.Execute(null);

        Assert.Equal(after, vm.RegistryJsonContent);
        Assert.Equal("3", JsonNode.Parse(vm.RegistryJsonContent!)!["registryVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Anchoring_is_refused_when_this_machine_has_no_key_for_the_plugin()
    {
        var vm = Create();
        vm.RegistryJsonContent = Registry();
        vm.SelectedPluginId = PluginId;

        vm.UseLocalSigningKeyCommand.Execute(null);

        Assert.Null(TrustOf(vm.RegistryJsonContent!));
        Assert.Contains("There is no key for that plugin on this machine", _dialogs);
    }

    [Fact]
    public void An_address_managers_are_not_sent_to_is_refused_before_the_key_is_anchored()
    {
        // Anchoring against the wrong address would publish signed catalogs nobody reads, and the
        // publish path would then refuse — a confusing place to find out.
        _keys.Create(PluginId, "pp");

        var vm = Create();
        vm.RegistryJsonContent = Registry(indexUrl: "https://elsewhere.example/index.json");
        vm.SelectedPluginId = PluginId;

        vm.UseLocalSigningKeyCommand.Execute(null);

        Assert.Null(TrustOf(vm.RegistryJsonContent!));
        Assert.Contains("Fix the index address first", _dialogs);
    }

    [Theory]
    [InlineData("\"a-string-not-an-object\"")]
    [InlineData("[ \"an\", \"array\" ]")]
    [InlineData("7")]
    public void An_indexTrust_shape_this_version_does_not_understand_is_refused_not_overwritten(string value)
    {
        // Matching only on "is it a JSON object" would let anything else fall through and be
        // silently replaced. Not recognising something the registry is vouching for is not a reason
        // to overwrite it.
        _keys.Create(PluginId, "pp");

        var vm = Create();
        vm.RegistryJsonContent = Registry(indexTrust: value);
        vm.SelectedPluginId = PluginId;
        var before = vm.RegistryJsonContent;

        vm.UseLocalSigningKeyCommand.Execute(null);

        Assert.Equal(before, vm.RegistryJsonContent);
        Assert.Contains("That entry already carries something else under indexTrust", _dialogs);
    }

    [Fact]
    public void An_entry_with_no_index_address_is_refused()
    {
        // "Nothing to compare" is not "they agree". Anchoring a key to an entry that cannot say
        // where managers read produces a signed catalog nobody reads.
        _keys.Create(PluginId, "pp");

        var vm = Create();
        vm.RegistryJsonContent = """
            {
              "registryVersion": "2",
              "updatedAt": "2026-07-30T00:00:00Z",
              "plugins": [ { "id": "amethyst", "name": "n", "author": "a", "description": "d" } ]
            }
            """;
        vm.SelectedPluginId = PluginId;

        vm.UseLocalSigningKeyCommand.Execute(null);

        Assert.Null(TrustOf(vm.RegistryJsonContent!));
        Assert.Contains("That entry has no index address", _dialogs);
    }

    [Fact]
    public void A_non_numeric_registry_version_is_refused()
    {
        // The high-water check that refuses replayed older registries compares numbers.
        _keys.Create(PluginId, "pp");

        var vm = Create();
        vm.RegistryJsonContent = Registry(version: "two");
        vm.SelectedPluginId = PluginId;

        vm.UseLocalSigningKeyCommand.Execute(null);

        Assert.Null(TrustOf(vm.RegistryJsonContent!));
        Assert.Contains("The registry version has to be a whole number", _dialogs);
    }
}
