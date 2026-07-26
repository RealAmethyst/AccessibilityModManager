using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// Attaches the signed <c>proof</c> block to an index about to be published.
///
/// Deliberately does no I/O. The publish flow already fetches the signed registry (to check it
/// still points at this address) and the live index (to detect a third-party change), so both inputs
/// are handed in. Keeping this pure makes the sequencing and revocation logic — the part where a
/// mistake would be expensive and invisible — directly testable.
/// </summary>
public sealed class IndexProofService(ClaimSigningKeyStore keyStore, ILogger logger)
{
    private static readonly JsonSerializerOptions IndexOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Reads the trust anchor for a plugin out of the registry.
    ///
    /// The caller must have verified the registry's signature first: everything here — the key, the
    /// index address, the scheme — is only meaningful because the registry vouches for it. Returns
    /// null when the entry carries no <c>indexTrust</c>, which is the normal state before an author
    /// has published their signing key.
    /// </summary>
    public static ClaimTrustAnchor? TryReadAnchor(string verifiedRegistryJson, string pluginId)
    {
        using var document = JsonDocument.Parse(verifiedRegistryJson);
        if (!document.RootElement.TryGetProperty("plugins", out var plugins) ||
            plugins.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var plugin in plugins.EnumerateArray())
        {
            if (!plugin.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            if (!string.Equals(id.GetString(), pluginId, StringComparison.Ordinal)) continue;

            if (!plugin.TryGetProperty("indexTrust", out var trust) || trust.ValueKind != JsonValueKind.Object)
                return null;
            if (!plugin.TryGetProperty("repoIndexUrl", out var url) || url.ValueKind != JsonValueKind.String)
                return null;

            return new ClaimTrustAnchor
            {
                PluginId = pluginId,
                RepoIndexUrl = url.GetString()!,
                Scheme = RequiredString(trust, "scheme"),
                KeyId = RequiredString(trust, "keyId"),
                Algorithm = RequiredString(trust, "algorithm"),
                PublicKeyPem = RequiredString(trust, "publicKeyPem")
            };
        }

        return null;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ClaimFormatException($"the registry's indexTrust block has no '{name}'");
        return value.GetString()!;
    }

    public sealed record ProofResult(byte[] IndexJson, ClaimSetBuilder.BuildResult Build);

    /// <summary>
    /// Returns the index bytes with a freshly built proof block attached.
    /// </summary>
    /// <param name="localIndexJson">The index about to be published.</param>
    /// <param name="anchor">From the VERIFIED registry — never from the index being published.</param>
    /// <param name="liveIndexJson">
    /// What is currently published, or null if nothing is. This is the authority for sequence
    /// numbers: taking them from local state instead would let a restored backup or a second
    /// machine reissue a number already in use, which reads as the author asserting two different
    /// truths about one version and is refused by every verifier.
    /// </param>
    public ProofResult AttachProof(byte[] localIndexJson, ClaimTrustAnchor anchor, byte[]? liveIndexJson)
    {
        var indexText = Encoding.UTF8.GetString(localIndexJson);
        var index = JsonSerializer.Deserialize<PluginRepoIndex>(indexText, IndexOptions)
            ?? throw new InvalidOperationException("The index could not be read.");

        if (!string.Equals(index.PluginId, anchor.PluginId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This index is for plugin '{index.PluginId}' but the registry entry being signed against " +
                $"is '{anchor.PluginId}'. Publishing it would produce claims no manager would accept.");
        }

        var previous = ReadPreviousClaims(liveIndexJson, anchor);

        using var signer = keyStore.OpenSigner(anchor);
        var built = ClaimSetBuilder.Build(index, previous, signer);

        // Sanity-check our own output before it goes anywhere. If the set we just produced would be
        // refused by a verifier, that must surface here rather than after publication.
        ClaimVerifier.ValidateSet(built.Claims);

        var root = JsonNode.Parse(indexText)?.AsObject()
            ?? throw new InvalidOperationException("The index is not a JSON object.");
        root["proof"] = JsonSerializer.SerializeToNode(
            ClaimProof.Write(anchor.KeyId, built.Claims), IndexOptions);

        var withProof = root.ToJsonString(IndexOptions);

        logger.Information(
            "Attached proof for {PluginId}: {Unchanged} unchanged, {Added} added, {Updated} updated, {Revoked} revoked",
            index.PluginId, built.Unchanged, built.Added, built.Updated, built.Revoked);

        return new ProofResult(Encoding.UTF8.GetBytes(withProof), built);
    }

    /// <summary>
    /// Verified claims from the live index, or an empty set when it carries no proof yet.
    ///
    /// A proof that exists but does NOT verify stops the publish. It means one of two things: the
    /// registry now names a different key, or the live file was tampered with. Neither is safe to
    /// paper over — and treating it as "no previous claims" would restart sequences from one, which
    /// is exactly the equivocation the sequence rules exist to prevent.
    /// </summary>
    private IReadOnlyList<SignedClaim> ReadPreviousClaims(byte[]? liveIndexJson, ClaimTrustAnchor anchor)
    {
        if (liveIndexJson is null or []) return [];

        ClaimProofDocument? document;
        try
        {
            document = ClaimProof.TryExtract(Encoding.UTF8.GetString(liveIndexJson));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "The index currently on the server could not be read, so its claim history can't be " +
                "continued. Publishing now could reuse sequence numbers already in use.", ex);
        }

        if (document is null)
        {
            logger.Information("The live index carries no proof yet — starting a fresh claim history");
            return [];
        }

        try
        {
            return ClaimProof.ReadVerified(document, anchor);
        }
        catch (ClaimFormatException ex)
        {
            throw new InvalidOperationException(
                "The proof on the server's index doesn't verify against the key the registry currently " +
                "names. Either the registry was re-pointed at a different key, or that file has been " +
                "altered. Publishing would reissue sequence numbers and break every manager that has " +
                $"already seen the current ones.\n\nDetail: {ex.Message}", ex);
        }
    }
}
