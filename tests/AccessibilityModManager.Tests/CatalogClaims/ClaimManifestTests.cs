using System.Text;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// The manifest on its own: the commitment, the chain rules, and the reader.
/// </summary>
public sealed class ClaimManifestTests
{
    private const string Digest = "abcdef1111111111111111111111111111111111111111111111111111111111";
    private const string Parent = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string Context = "3333333333333333333333333333333333333333333333333333333333333333";

    private static byte[] Payload(long generation, string? parent, string digest = Digest) =>
        ManifestCodec.Serialize(new ProofManifest
        {
            V = 1, TrustContext = Context, Generation = generation, Parent = parent, ClaimsDigest = digest
        });

    [Fact]
    public void The_first_manifest_names_no_parent()
    {
        var manifest = ManifestCodec.Parse(Payload(1, null));

        Assert.Equal(1, manifest.Generation);
        Assert.Null(manifest.Parent);
    }

    [Fact]
    public void A_first_manifest_that_names_a_parent_is_refused()
    {
        // There is nothing for it to descend from, so the link is either a mistake or an attempt to
        // graft a history onto something that does not exist.
        Assert.Throws<ClaimFormatException>(() => ManifestCodec.Parse(Payload(1, Parent)));
    }

    [Fact]
    public void A_later_manifest_with_no_parent_is_refused()
    {
        // Without the link there is no chain — two publishes claiming one generation stop being
        // visibly a fork to anyone holding both.
        Assert.Throws<ClaimFormatException>(() => ManifestCodec.Parse(Payload(2, null)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ClaimCodec.MaxCounter + 1)]
    [InlineData(long.MaxValue)]
    public void A_generation_outside_the_range_is_refused(long generation)
    {
        var text = Encoding.UTF8.GetString(Payload(2, Parent))
            .Replace("\"generation\":2", $"\"generation\":{generation}");

        Assert.Throws<ClaimFormatException>(() => ManifestCodec.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void The_top_of_the_range_is_accepted()
    {
        // Both ends pinned, not just the bottom. The ceiling is not 2^63-1 because the builder takes
        // one past the highest value it is shown, and a hostile long.MaxValue would overflow into a
        // negative sequence — so where exactly it sits is part of the contract, and a second
        // implementation has to agree on it.
        var text = Encoding.UTF8.GetString(Payload(2, Parent))
            .Replace("\"generation\":2", $"\"generation\":{ClaimCodec.MaxCounter}");

        Assert.Equal(ClaimCodec.MaxCounter, ManifestCodec.Parse(Encoding.UTF8.GetBytes(text)).Generation);
    }

    [Fact]
    public void An_uppercase_hash_is_refused()
    {
        // Two spellings of one value are two values when everything downstream compares ordinally.
        Assert.Throws<ClaimFormatException>(() => ManifestCodec.Parse(Payload(1, null, Digest.ToUpperInvariant())));
    }

    [Fact]
    public void An_unknown_member_is_refused()
    {
        var text = Encoding.UTF8.GetString(Payload(1, null)).Replace("{\"v\":1", "{\"surprise\":true,\"v\":1");

        Assert.Throws<ClaimFormatException>(() => ManifestCodec.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void The_digest_is_over_a_sorted_set_so_order_does_not_change_it()
    {
        // Claim order carries no meaning, and every projection sorts explicitly — so a server that
        // reorders the array changes nothing, which is exactly what the digest should say.
        var key = ClaimTestKeys.Primary;
        var anchor = new ClaimTrustAnchor
        {
            PluginId = "amethyst",
            RepoIndexUrl = "https://accessibilitymods.com/registry/plugins/amethyst/index.json",
            Scheme = ClaimTrustAnchor.SchemeV1,
            KeyId = "k1",
            Algorithm = ClaimTrustAnchor.AlgorithmRsaPssSha256,
            PublicKeyPem = key.ExportSubjectPublicKeyInfoPem()
        };

        using var signer = new ClaimSigner(
            key.ExportEncryptedPkcs8PrivateKeyPem("pp",
                new System.Security.Cryptography.PbeParameters(
                    System.Security.Cryptography.PbeEncryptionAlgorithm.Aes256Cbc,
                    System.Security.Cryptography.HashAlgorithmName.SHA256, 100_000)),
            "pp", anchor);

        var a = signer.Sign(ClaimKind.Game, new ClaimIdentity { Kind = ClaimKind.Game, GameId = "a" }, 1,
            ClaimAudience.Everyone, """{"gameId":"a"}""");
        var b = signer.Sign(ClaimKind.Game, new ClaimIdentity { Kind = ClaimKind.Game, GameId = "b" }, 1,
            ClaimAudience.Everyone, """{"gameId":"b"}""");

        Assert.Equal(ClaimDigest.Compute([a, b]), ClaimDigest.Compute([b, a]));

        // But a duplicate is not a reorder.
        Assert.NotEqual(ClaimDigest.Compute([a, b]), ClaimDigest.Compute([a, b, b]));
    }
}
