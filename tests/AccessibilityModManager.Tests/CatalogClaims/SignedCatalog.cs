using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// Builds real signed index documents for tests — an anchor, a signer, and the <c>proof</c> block
/// attached exactly the way the AuthorTool attaches it.
///
/// <para>Signatures here are genuine. A fixture that faked them would let a test pass while the
/// verification it claims to exercise did nothing, which is the failure mode this project keeps
/// finding in its own tests.</para>
/// </summary>
internal sealed class SignedCatalog : IDisposable
{
    private const string Passphrase = "pp";
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public SignedCatalog(
        RSA? key = null,
        string pluginId = "plug-a",
        string indexUrl = "https://example.invalid/index.json",
        string keyId = "k1")
    {
        key ??= ClaimTestKeys.Primary;

        Anchor = new ClaimTrustAnchor
        {
            PluginId = pluginId,
            RepoIndexUrl = indexUrl,
            Scheme = ClaimTrustAnchor.SchemeV1,
            KeyId = keyId,
            Algorithm = ClaimTrustAnchor.AlgorithmRsaPssSha256,
            PublicKeyPem = key.ExportSubjectPublicKeyInfoPem()
        };

        Signer = new ClaimSigner(
            key.ExportEncryptedPkcs8PrivateKeyPem(Passphrase,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)),
            Passphrase, Anchor);
    }

    public ClaimTrustAnchor Anchor { get; }
    public ClaimSigner Signer { get; }

    public void Dispose() => Signer.Dispose();

    public SignedClaim Header(long seq = 1, string repoVersion = "1") => Signer.Sign(
        ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header }, seq, ClaimAudience.Everyone,
        $$"""{"pluginId":"{{Anchor.PluginId}}","repoVersion":"{{repoVersion}}"}""");

    public SignedClaim Game(string gameId = "game-1", long seq = 1) => Signer.Sign(
        ClaimKind.Game, new ClaimIdentity { Kind = ClaimKind.Game, GameId = gameId }, seq,
        ClaimAudience.Everyone,
        $$"""{"gameId":"{{gameId}}","displayName":"Game One","modName":"Mod"}""");

    public SignedClaim Release(
        string gameId = "game-1", string version = "1.0.0", string channel = "stable",
        long seq = 1, string url = "https://example.invalid/pkg.zip") =>
        Signer.Sign(
            ClaimKind.Release,
            new ClaimIdentity { Kind = ClaimKind.Release, GameId = gameId, Channel = channel, Version = version },
            seq, ClaimAudience.Everyone,
            $$"""
            {"gameId":"{{gameId}}","pluginId":"{{Anchor.PluginId}}","version":"{{version}}","channel":"{{channel}}","packageUrl":"{{url}}","sha256":"{{Sha}}"}
            """);

    public SignedClaim Revocation(ClaimIdentity identity, long seq, ClaimAudience? audience = null) =>
        Signer.Sign(ClaimKind.Revocation, identity, seq, audience ?? ClaimAudience.Everyone, "{}");

    /// <summary>A plain header + one game + one release, the smallest catalog that projects.</summary>
    public List<SignedClaim> Minimal(long releaseSeq = 1) =>
        [Header(), Game(), Release(seq: releaseSeq)];

    /// <summary>
    /// The index document a server would serve: a plaintext catalog with the <c>proof</c> attached.
    ///
    /// <para><paramref name="plaintext"/> exists so a test can make the served catalog DISAGREE with
    /// the claims. A verifying manager must take the projection and nothing else, and the only way
    /// to demonstrate that is to hand it a plaintext that would be visibly wrong if it were used.</para>
    /// </summary>
    public byte[] Build(
        IReadOnlyList<SignedClaim> claims, string? plaintext = null, bool withManifest = true)
    {
        var root = JsonNode.Parse(plaintext ?? Plaintext())!.AsObject();

        var manifest = withManifest
            ? Signer.SignManifest(generation: 1, parent: null, claimsDigest: ClaimDigest.Compute(claims))
            : null;

        root["proof"] = JsonSerializer.SerializeToNode(
            manifest is null
                ? WriteWithoutManifest(claims)
                : ClaimProof.Write(Anchor, manifest, claims),
            Options);

        return Encoding.UTF8.GetBytes(root.ToJsonString(Options));
    }

    /// <summary>
    /// A proof with no manifest — what the catalog API will serve once it filters by tier, and the
    /// reason a consumer must pass <c>requireManifest: false</c>.
    /// </summary>
    private ClaimProofDocument WriteWithoutManifest(IReadOnlyList<SignedClaim> claims) => new()
    {
        Scheme = ClaimTrustAnchor.SchemeV1,
        KeyId = Anchor.KeyId,
        Algorithm = Anchor.Algorithm,
        Manifest = null,
        Claims = [.. claims.Select(c => new ClaimProofEntry(
            Convert.ToBase64String(c.PayloadBytes), Convert.ToBase64String(c.Signature)))]
    };

    /// <summary>An ordinary unsigned index for this plugin — also what the unsigned path is fed.</summary>
    public string Plaintext(string gameId = "game-1", string version = "1.0.0") => $$"""
        {
          "pluginId": "{{Anchor.PluginId}}",
          "repoVersion": "1",
          "generatedAt": "2026-07-24T00:00:00Z",
          "games": [ { "gameId": "{{gameId}}", "displayName": "Game One", "modName": "Mod" } ],
          "releasesByGameId": {
            "{{gameId}}": [
              {
                "pluginId": "{{Anchor.PluginId}}",
                "gameId": "{{gameId}}",
                "version": "{{version}}",
                "channel": "stable",
                "packageUrl": "https://example.invalid/pkg.zip",
                "sha256": "{{Sha}}"
              }
            ]
          }
        }
        """;
}
