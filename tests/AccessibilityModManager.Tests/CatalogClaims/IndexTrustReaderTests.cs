using System.Security.Cryptography;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// The registry says who may sign a plugin's catalog, and this reads that answer. Three states, and
/// the whole point of the type is that the third one exists: an entry that names something unusable
/// where a key belongs must never be reported as "no key", because "no key" is a PERMISSION — it
/// sends the AuthorTool down the unsigned publish path and would send a manager down the unsigned
/// read path.
///
/// <para>Before this reader existed, the AuthorTool returned null for a present-but-malformed
/// <c>indexTrust</c>, and its publish coordinator read null as "unsigned". The only thing standing
/// between that and publishing plaintext over a signed catalog was the machine happening to hold
/// publishing records — so on a replacement machine, which has none, it would have gone through.</para>
/// </summary>
public sealed class IndexTrustReaderTests
{
    private const string PluginId = "amethyst";
    private const string IndexUrl = "https://accessibilitymods.com/registry/plugins/amethyst/index.json";

    private static string Pem => ClaimTestKeys.Primary.ExportSubjectPublicKeyInfoPem();

    /// <summary>A registry whose one entry carries whatever <paramref name="trustBlock"/> says.</summary>
    private static string Registry(string trustBlock, string? repoIndexUrl = IndexUrl, string id = PluginId)
    {
        var url = repoIndexUrl is null ? "" : $""""  "repoIndexUrl": {JsonSerializer.Serialize(repoIndexUrl)}, """";
        return $$"""
        {
          "registryVersion": "3",
          "updatedAt": "2026-07-30T00:00:00Z",
          "plugins": [
            { "id": {{JsonSerializer.Serialize(id)}}, {{url}} {{trustBlock}} }
          ]
        }
        """;
    }

    private static string ValidTrust(string? scheme = null, string? algorithm = null, string? pem = null) =>
        $$""""
        "indexTrust": {
          "scheme": {{JsonSerializer.Serialize(scheme ?? ClaimTrustAnchor.SchemeV1)}},
          "keyId": "amethyst-2026-07",
          "algorithm": {{JsonSerializer.Serialize(algorithm ?? ClaimTrustAnchor.AlgorithmRsaPssSha256)}},
          "publicKeyPem": {{JsonSerializer.Serialize(pem ?? Pem)}}
        }
        """";

    // ---- the state that grants something -------------------------------------------------

    [Fact]
    public void A_well_formed_entry_resolves_to_an_anchor()
    {
        var resolution = IndexTrustReader.Resolve(Registry(ValidTrust()), PluginId);

        Assert.Equal(IndexTrustStatus.Anchored, resolution.Status);
        Assert.Equal("amethyst-2026-07", resolution.Anchor!.KeyId);
        Assert.Null(resolution.Reason);
    }

    [Fact]
    public void The_index_address_is_carried_exactly_as_the_registry_spells_it()
    {
        // The load-bearing one. ClaimTrustContext hashes this string, and the author signs using the
        // registry's literal text. A Uri round-trip normalises — percent-encoding case, default
        // ports, a trailing dot in the host — so deriving it from a parsed Uri would make the
        // manager compute a different trust context than the author signed under, and every claim
        // would fail to verify for reasons nothing in the failure would name.
        const string awkward = "https://accessibilitymods.com:443/registry/plugins/%7Eamethyst/index.json";

        var resolution = IndexTrustReader.Resolve(Registry(ValidTrust(), repoIndexUrl: awkward), PluginId);

        Assert.Equal(IndexTrustStatus.Anchored, resolution.Status);
        Assert.Equal(awkward, resolution.Anchor!.RepoIndexUrl);
        Assert.NotEqual(new Uri(awkward).AbsoluteUri, resolution.Anchor.RepoIndexUrl);
    }

    // ---- genuinely absent, which is the only route to the unsigned path ------------------

    [Fact]
    public void An_entry_with_no_trust_block_has_no_anchor()
    {
        var resolution = IndexTrustReader.Resolve(Registry(""" "name": "Amethyst's mods" """), PluginId);

        Assert.Equal(IndexTrustStatus.None, resolution.Status);
    }

    [Fact]
    public void A_plugin_the_registry_does_not_list_has_no_anchor()
    {
        var registry = """{ "registryVersion": "3", "plugins": [] }""";

        Assert.Equal(IndexTrustStatus.None, IndexTrustReader.Resolve(registry, PluginId).Status);
    }

    [Fact]
    public void An_entry_whose_id_differs_only_in_case_is_not_matched()
    {
        // Deliberate, and it must stay this way: a trust anchor decides which key may sign for a
        // plugin, so matching loosely would make 'amethyst' and 'Amethyst' one identity. The
        // disagreement is surfaced by TryReadIndexUrl, which exists to see exactly this.
        var resolution = IndexTrustReader.Resolve(Registry(ValidTrust(), id: "Amethyst"), PluginId);

        Assert.Equal(IndexTrustStatus.None, resolution.Status);
    }

    // ---- present and unusable: everything below must NOT be reported as absent -----------

    [Theory]
    [InlineData(""" "indexTrust": null """)]
    [InlineData(""" "indexTrust": "signed-claims-v1" """)]
    [InlineData(""" "indexTrust": ["signed-claims-v1"] """)]
    [InlineData(""" "indexTrust": 7 """)]
    [InlineData(""" "indexTrust": true """)]
    public void A_trust_block_that_is_not_a_set_of_values_is_unusable(string trustBlock)
    {
        var resolution = IndexTrustReader.Resolve(Registry(trustBlock), PluginId);

        Assert.Equal(IndexTrustStatus.Unusable, resolution.Status);
        Assert.NotNull(resolution.Reason);
    }

    [Theory]
    [InlineData("scheme")]
    [InlineData("keyId")]
    [InlineData("algorithm")]
    [InlineData("publicKeyPem")]
    public void A_trust_block_missing_any_member_is_unusable(string drop)
    {
        var members = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scheme"] = JsonSerializer.Serialize(ClaimTrustAnchor.SchemeV1),
            ["keyId"] = "\"amethyst-2026-07\"",
            ["algorithm"] = JsonSerializer.Serialize(ClaimTrustAnchor.AlgorithmRsaPssSha256),
            ["publicKeyPem"] = JsonSerializer.Serialize(Pem)
        };
        members.Remove(drop);

        var body = string.Join(", ", members.Select(m => $"\"{m.Key}\": {m.Value}"));
        var block = $$""" "indexTrust": { {{body}} } """;
        var resolution = IndexTrustReader.Resolve(Registry(block), PluginId);

        Assert.Equal(IndexTrustStatus.Unusable, resolution.Status);
        Assert.Contains(drop, resolution.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_member_is_unusable_rather_than_treated_as_present()
    {
        var block = $$""""
        "indexTrust": {
          "scheme": "signed-claims-v1", "keyId": "   ",
          "algorithm": "rsa-pss-sha256", "publicKeyPem": {{JsonSerializer.Serialize(Pem)}}
        }
        """";

        Assert.Equal(IndexTrustStatus.Unusable, IndexTrustReader.Resolve(Registry(block), PluginId).Status);
    }

    [Fact]
    public void An_unrecognised_member_inside_the_trust_block_is_unusable()
    {
        // indexTrust is a versioned security block and the scheme name IS the version, so a member
        // this build does not know means the document follows a contract it does not implement.
        // Ignoring it would be guessing at what the author meant to constrain.
        var block = $$""""
        "indexTrust": {
          "scheme": "signed-claims-v1", "keyId": "amethyst-2026-07",
          "algorithm": "rsa-pss-sha256", "publicKeyPem": {{JsonSerializer.Serialize(Pem)}},
          "requireCountersignature": true
        }
        """";

        var resolution = IndexTrustReader.Resolve(Registry(block), PluginId);

        Assert.Equal(IndexTrustStatus.Unusable, resolution.Status);
        Assert.Contains("requireCountersignature", resolution.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_scheme_is_unusable_not_absent()
    {
        var resolution = IndexTrustReader.Resolve(Registry(ValidTrust(scheme: "signed-claims-v2")), PluginId);

        Assert.Equal(IndexTrustStatus.Unusable, resolution.Status);
    }

    [Fact]
    public void An_unsupported_algorithm_is_unusable_not_absent()
    {
        var resolution = IndexTrustReader.Resolve(Registry(ValidTrust(algorithm: "ed25519")), PluginId);

        Assert.Equal(IndexTrustStatus.Unusable, resolution.Status);
    }

    [Fact]
    public void A_key_that_is_not_a_readable_public_key_is_unusable()
    {
        var resolution = IndexTrustReader.Resolve(
            Registry(ValidTrust(pem: "-----BEGIN PUBLIC KEY-----\nnot base64\n-----END PUBLIC KEY-----")),
            PluginId);

        Assert.Equal(IndexTrustStatus.Unusable, resolution.Status);
    }

    [Fact]
    public void A_key_of_the_wrong_size_is_unusable()
    {
        using var weak = RSA.Create(2048);

        var resolution = IndexTrustReader.Resolve(
            Registry(ValidTrust(pem: weak.ExportSubjectPublicKeyInfoPem())), PluginId);

        Assert.Equal(IndexTrustStatus.Unusable, resolution.Status);
    }

    [Fact]
    public void An_entry_that_names_a_key_but_no_address_is_unusable()
    {
        // The address is part of what every claim is signed over, so an entry that cannot say where
        // its catalog is read from cannot have that catalog verified either.
        var resolution = IndexTrustReader.Resolve(Registry(ValidTrust(), repoIndexUrl: null), PluginId);

        Assert.Equal(IndexTrustStatus.Unusable, resolution.Status);
    }

    [Fact]
    public void Two_trust_blocks_in_one_entry_are_unusable()
    {
        // One reader takes the first, another takes the last, and each is internally consistent —
        // so the two disagree about which key signs this catalog while both believe they are right.
        var block = $"{ValidTrust()}, {ValidTrust(scheme: "signed-claims-v2")}";

        var resolution = IndexTrustReader.Resolve(Registry(block), PluginId);

        Assert.Equal(IndexTrustStatus.Unusable, resolution.Status);
    }

    // ---- the collection itself, not just the entry ---------------------------------------

    [Fact]
    public void An_id_listed_twice_is_unusable_rather_than_first_one_wins()
    {
        // JSON permits repeated ARRAY ELEMENTS, and AllowDuplicateProperties only governs repeated
        // members of one object — so this parses cleanly and a first-match reader would answer
        // "None" here. None is permission to publish and read unsigned, which would let whoever
        // writes the registry choose which of two answers a reader sees by ordering them.
        var registry = $$"""
        {
          "registryVersion": "3",
          "plugins": [
            { "id": "amethyst", "repoIndexUrl": "{{IndexUrl}}" },
            { "id": "amethyst", "repoIndexUrl": "{{IndexUrl}}", {{ValidTrust()}} }
          ]
        }
        """;

        var resolution = IndexTrustReader.Resolve(registry, PluginId);

        Assert.Equal(IndexTrustStatus.Unusable, resolution.Status);
        Assert.NotEqual(IndexTrustStatus.None, resolution.Status);
    }

    [Theory]
    [InlineData("""{ "registryVersion": "3" }""")]
    [InlineData("""{ "registryVersion": "3", "plugins": null }""")]
    [InlineData("""{ "registryVersion": "3", "plugins": {} }""")]
    [InlineData("""{ "registryVersion": "3", "plugins": "amethyst" }""")]
    public void A_registry_with_no_usable_plugin_list_is_unusable_not_absent(string registry)
    {
        // Structural breakage is not "this plugin isn't listed". Only a well-formed collection may
        // establish genuine absence, because absence is the state that grants the unsigned path.
        Assert.Equal(IndexTrustStatus.Unusable, IndexTrustReader.Resolve(registry, PluginId).Status);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"amethyst\"")]
    [InlineData("7")]
    [InlineData("null")]
    public void A_registry_that_is_not_a_set_of_values_is_refused_rather_than_faulting(string registry)
    {
        // JsonElement.TryGetProperty THROWS on a non-object element rather than returning false, so
        // without an explicit root check this reader faulted instead of refusing — and a fault is
        // not a decision any caller can act on.
        var resolution = IndexTrustReader.Resolve(registry, PluginId);

        Assert.Equal(IndexTrustStatus.Unusable, resolution.Status);
    }

    [Fact]
    public void Text_that_is_not_json_at_all_is_refused_rather_than_faulting()
    {
        Assert.Equal(IndexTrustStatus.Unusable, IndexTrustReader.Resolve("not json", PluginId).Status);
    }

    // ---- the states themselves ------------------------------------------------------------

    [Fact]
    public void The_invalid_combinations_cannot_be_constructed_at_all()
    {
        // Rather than every consumer having to check Status AND Anchor and agree about it, the
        // combinations that would let them disagree are unrepresentable: the constructor is private
        // and the properties are get-only, so there is no None-carrying-an-anchor to mishandle.
        var type = typeof(IndexTrustResolution);

        Assert.Empty(type.GetConstructors());
        foreach (var name in new[] { nameof(IndexTrustResolution.Status), nameof(IndexTrustResolution.Anchor),
                                     nameof(IndexTrustResolution.Reason) })
        {
            Assert.Null(type.GetProperty(name)!.SetMethod);
        }

        Assert.Null(IndexTrustResolution.NoAnchor.Anchor);
        Assert.Null(IndexTrustResolution.Unusable("why").Anchor);
        Assert.Null(IndexTrustResolution.NoAnchor.Reason);
    }

    [Fact]
    public void The_factories_enforce_the_invariant_rather_than_only_annotating_it()
    {
        // Sealing the constructor stops an object initializer writing down an invalid combination
        // and stops nothing coming through the factories: `Anchored(null!)` used to return an
        // Anchored resolution carrying no anchor, which makes the documented invariant false while
        // consumers dereference `Anchor!` on the strength of it. A nullable annotation is a compiler
        // courtesy, not a runtime check — and the reflection test above passes either way, which is
        // exactly why this one exists beside it.
        Assert.Throws<ArgumentNullException>(() => IndexTrustResolution.Anchored(null!));
        Assert.Throws<ArgumentNullException>(() => IndexTrustResolution.Unusable(null!));
        Assert.Throws<ArgumentException>(() => IndexTrustResolution.Unusable("   "));
    }

    [Fact]
    public void Unresolved_is_the_default_so_a_field_nobody_assigned_is_never_a_permission()
    {
        // The zero value has to be the state that grants nothing. If default(IndexTrustStatus) were
        // None, any field or property that was never populated would read as "the registry names no
        // key" — which is precisely the permission to read and publish unsigned catalogs.
        Assert.Equal(IndexTrustStatus.Unresolved, default(IndexTrustStatus));
    }
}
