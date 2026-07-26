using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// The publisher's commitment to a whole claim set.
///
/// Individually signed claims prove that each object is genuine; nothing about them proves the SET
/// is. A hostile or corrupt server could delete one entry and what remained still verified
/// perfectly — so the next honest publish saw no history for that object, started its sequence
/// again at one, and the author's own offline key signed two different truths about one release.
/// Deleting the whole proof was worse: that read as "an index from before claims existed" and
/// restarted every history at once.
///
/// The manifest closes that. It is signed by the same key, under a DIFFERENT domain prefix so a
/// claim can never be read as a manifest or the reverse, and it commits to a digest over every
/// claim present. Deletion, addition, substitution and duplication all change the digest.
///
/// What it deliberately does NOT do is prove freshness: a complete older publish is internally
/// consistent and verifies. That is what the author's locally committed head is for.
/// </summary>
public sealed record ProofManifest
{
    public required int V { get; init; }

    /// <summary>The same value every claim in the set carries — one plugin, one index address,
    /// one key.</summary>
    public required string TrustContext { get; init; }

    /// <summary>Per-plugin publish counter, starting at 1. Unrelated to any claim's sequence.</summary>
    public required long Generation { get; init; }

    /// <summary>
    /// The previous manifest's payload hash; null only for the first publish. Makes the publish
    /// history a hash chain, so two manifests claiming one generation are visibly a fork to anyone
    /// holding both. Kept for audit and diagnosis — it is NOT a freshness mechanism, because the
    /// newest manifest only ever names its immediate parent and cannot show what a server is
    /// hiding beyond it.
    /// </summary>
    public string? Parent { get; init; }

    /// <summary>SHA-256 over the claim hashes — see <see cref="ClaimDigest"/>.</summary>
    public required string ClaimsDigest { get; init; }
}

/// <summary>A manifest as it travels: exact signed bytes, the signature, and the parsed value.</summary>
public sealed record SignedManifest
{
    public required byte[] PayloadBytes { get; init; }
    public required byte[] Signature { get; init; }
    public required ProofManifest Manifest { get; init; }

    /// <summary>
    /// Hex SHA-256 of <see cref="PayloadBytes"/>. This is what a later manifest names as its parent
    /// and what the author's committed head records, so "the same publish" is decided by content
    /// rather than by a counter a server could relabel.
    /// </summary>
    public string PayloadHash => Convert.ToHexStringLower(SHA256.HashData(PayloadBytes));
}

/// <summary>
/// The commitment itself: SHA-256 over every claim's payload hash, each as 64 lowercase hex
/// characters followed by a newline, sorted ascending as text.
///
/// A digest rather than a list, on purpose. A list of hashes would let a reader check membership of
/// what it received, but it would also disclose exactly how many objects exist — the existence
/// oracle this whole design works to remove. The completeness property is only meaningful to
/// someone entitled to see everything, which is the author alone, and the author recomputes the
/// digest from what they were handed.
///
/// Sorting makes the commitment a multiset: reordering the proof array is invisible to it. That is
/// safe only because claim order carries no meaning — every projection sorts explicitly rather than
/// inheriting the order claims arrived in.
/// </summary>
public static class ClaimDigest
{
    public static string Compute(IEnumerable<SignedClaim> claims)
    {
        var hashes = claims
            .Select(c => ClaimCodec.ContentHash(c.PayloadBytes))
            .Order(StringComparer.Ordinal)
            .ToList();

        var buffer = new StringBuilder();
        foreach (var hash in hashes) buffer.Append(hash).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(buffer.ToString())));
    }
}

/// <summary>
/// Reads and writes manifest payloads. Same discipline as <see cref="ClaimCodec"/>: bytes travel
/// verbatim, the reader is strict, and anything two parsers could read differently is refused.
/// </summary>
public static class ManifestCodec
{
    /// <summary>
    /// Distinct from the claim prefix, which is the point. Domain separation means a manifest
    /// signature can never be presented as a claim signature, or the reverse, however wrong an
    /// implementation gets its parsing.
    /// </summary>
    private static readonly byte[] DomainPrefix = "amm-proof-manifest-v1\n"u8.ToArray();

    public const int SupportedVersion = 1;

    /// <summary>A manifest is five scalars; anything approaching this is malformed.</summary>
    public const int MaxPayloadBytes = 4 * 1024;

    public static byte[] BytesToSign(ReadOnlySpan<byte> payloadBytes)
    {
        var buffer = new byte[DomainPrefix.Length + payloadBytes.Length];
        DomainPrefix.CopyTo(buffer, 0);
        payloadBytes.CopyTo(buffer.AsSpan(DomainPrefix.Length));
        return buffer;
    }

    public static byte[] Serialize(ProofManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", manifest.V);
            w.WriteString("trustContext", manifest.TrustContext);
            w.WriteNumber("generation", manifest.Generation);
            if (manifest.Parent is not null) w.WriteString("parent", manifest.Parent);
            w.WriteString("claimsDigest", manifest.ClaimsDigest);
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static ProofManifest Parse(ReadOnlySpan<byte> payloadBytes)
    {
        if (payloadBytes.Length == 0)
            throw new ClaimFormatException("manifest payload is empty");
        if (payloadBytes.Length > MaxPayloadBytes)
            throw new ClaimFormatException($"manifest payload exceeds {MaxPayloadBytes} bytes");
        if (payloadBytes.Length >= 3 && payloadBytes[0] == 0xEF && payloadBytes[1] == 0xBB && payloadBytes[2] == 0xBF)
            throw new ClaimFormatException("manifest payload has a UTF-8 BOM");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payloadBytes.ToArray(),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException ex)
        {
            throw new ClaimFormatException("manifest payload is not valid JSON", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ClaimFormatException("manifest payload is not a JSON object");

            ClaimCodec.RejectUnknownMembers(root, "manifest",
                ["v", "trustContext", "generation", "parent", "claimsDigest"]);

            var v = ClaimCodec.RequireInt(root, "v");
            if (v != SupportedVersion)
                throw new ClaimFormatException($"unsupported manifest version {v}");

            var generation = ClaimCodec.RequireLong(root, "generation");
            if (generation is < 1 or > ClaimCodec.MaxCounter)
                throw new ClaimFormatException($"generation must be between 1 and {ClaimCodec.MaxCounter}");

            // A parent belongs to every manifest except the very first, and to no other. Leaving
            // that unstated let a first publish name an ancestor it cannot have, and a later one
            // omit the link that makes the history a chain at all — a second implementation reading
            // only the document would have had to guess which was meant.
            var parent = ClaimCodec.OptionalString(root, "parent");
            if (generation == 1 && parent is not null)
                throw new ClaimFormatException("the first manifest must not name a parent");
            if (generation > 1 && parent is null)
                throw new ClaimFormatException($"manifest generation {generation} names no parent");
            if (parent is not null) RequireHash(parent, "parent");

            var digest = ClaimCodec.RequireString(root, "claimsDigest");
            RequireHash(digest, "claimsDigest");

            return new ProofManifest
            {
                V = v,
                TrustContext = ClaimCodec.RequireString(root, "trustContext"),
                Generation = generation,
                Parent = parent,
                ClaimsDigest = digest
            };
        }
    }

    /// <summary>
    /// Hashes are 64 lowercase hex characters, exactly. Accepting uppercase would make two
    /// spellings of one value, and two spellings compared ordinally are two different values.
    /// </summary>
    private static void RequireHash(string value, string name)
    {
        if (value.Length != 64 || !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ClaimFormatException($"'{name}' is not a lowercase hex sha256");
    }
}
