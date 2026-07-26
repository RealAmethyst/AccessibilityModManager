using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// Reads and writes claim payloads.
///
/// The signature covers the exact bytes that travel, so the producer's serialization is the only
/// one that ever exists. That removes canonicalisation from the security path entirely: two
/// implementations never have to independently produce identical bytes, they only have to READ the
/// same bytes the same way. What matters instead is that the reader is strict — a payload that two
/// parsers could interpret differently is refused rather than guessed at.
///
/// The writer is still deterministic (fixed member order, no whitespace), so an unchanged object
/// re-serializes to identical bytes. That is a stability property, not a security one: it keeps a
/// republished object from looking changed when it isn't.
/// </summary>
public static class ClaimCodec
{
    /// <summary>Prefixed to the payload before signing. A signature made in one context can then
    /// never be meaningful in another as further signed artifacts are added.</summary>
    private static readonly byte[] DomainPrefix = "amm-claim-v1\n"u8.ToArray();

    public const int SupportedVersion = 1;

    /// <summary>Bounds a hostile or corrupt payload before any parsing work happens.</summary>
    public const int MaxPayloadBytes = 256 * 1024;

    public static byte[] BytesToSign(ReadOnlySpan<byte> payloadBytes)
    {
        var buffer = new byte[DomainPrefix.Length + payloadBytes.Length];
        DomainPrefix.CopyTo(buffer, 0);
        payloadBytes.CopyTo(buffer.AsSpan(DomainPrefix.Length));
        return buffer;
    }

    /// <summary>
    /// Serializes a payload deterministically. Member order is fixed by this method rather than by
    /// reflection order, and no floating-point value can appear anywhere — <c>Seq</c> is the only
    /// number and it is an integer.
    /// </summary>
    public static byte[] Serialize(ClaimPayload payload)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", payload.V);
            w.WriteString("trustContext", payload.TrustContext);
            w.WriteString("kind", payload.Kind.ToString());

            w.WriteStartObject("identity");
            w.WriteString("kind", payload.Identity.Kind.ToString());
            if (payload.Identity.GameId is not null) w.WriteString("gameId", payload.Identity.GameId);
            if (payload.Identity.Channel is not null) w.WriteString("channel", payload.Identity.Channel);
            if (payload.Identity.Version is not null) w.WriteString("version", payload.Identity.Version);
            w.WriteEndObject();

            w.WriteNumber("seq", payload.Seq);

            w.WriteStartObject("audience");
            w.WriteBoolean("public", payload.Audience.Public);
            if (payload.Audience.CampaignId is not null) w.WriteString("campaignId", payload.Audience.CampaignId);
            w.WriteStartArray("tierIds");
            foreach (var tier in payload.Audience.TierIds) w.WriteStringValue(tier);
            w.WriteEndArray();
            w.WriteEndObject();

            w.WritePropertyName("body");
            using (var body = JsonDocument.Parse(payload.BodyJson))
                body.RootElement.WriteTo(w);

            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Strictly parses payload bytes. Every rejection here is a case where two readers could
    /// otherwise disagree about what was signed.
    /// </summary>
    public static ClaimPayload Parse(ReadOnlySpan<byte> payloadBytes)
    {
        if (payloadBytes.Length == 0)
            throw new ClaimFormatException("claim payload is empty");
        if (payloadBytes.Length > MaxPayloadBytes)
            throw new ClaimFormatException($"claim payload exceeds {MaxPayloadBytes} bytes");
        if (payloadBytes.Length >= 3 && payloadBytes[0] == 0xEF && payloadBytes[1] == 0xBB && payloadBytes[2] == 0xBF)
            throw new ClaimFormatException("claim payload has a UTF-8 BOM");

        JsonDocument doc;
        try
        {
            // AllowDuplicateProperties=false applies RECURSIVELY, which matters: a hand-rolled check
            // at the top level would still have let a release body carry two packageUrl or sha256
            // members, readable as first-wins by one parser and last-wins by another while a single
            // valid signature covered both readings.
            doc = JsonDocument.Parse(payloadBytes.ToArray(),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException ex)
        {
            throw new ClaimFormatException("claim payload is not valid JSON", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ClaimFormatException("claim payload is not a JSON object");

            var known = new HashSet<string>(StringComparer.Ordinal)
                { "v", "trustContext", "kind", "identity", "seq", "audience", "body" };
            foreach (var member in root.EnumerateObject())
            {
                if (!known.Contains(member.Name))
                {
                    // Unknown fields are tolerated inside `body` for forward compatibility, but never
                    // in the envelope: an envelope field a strict verifier ignores could carry
                    // meaning to a laxer one.
                    throw new ClaimFormatException($"unknown envelope member '{member.Name}'");
                }
            }

            var v = RequireInt(root, "v");
            if (v != SupportedVersion)
                throw new ClaimFormatException($"unsupported claim version {v}");

            var kind = RequireEnum(root, "kind");
            var identityElement = RequireObject(root, "identity");
            RejectUnknownMembers(identityElement, "identity", ["kind", "gameId", "channel", "version"]);

            var identity = new ClaimIdentity
            {
                Kind = RequireEnum(identityElement, "kind"),
                GameId = OptionalString(identityElement, "gameId"),
                Channel = OptionalString(identityElement, "channel"),
                Version = OptionalString(identityElement, "version")
            };

            // `kind` says whether this claim ASSERTS an object or WITHDRAWS one; `identity.kind`
            // says what kind of object it is about. For an assertion the two are necessarily the
            // same. A revocation is the one case where they differ on purpose — it withdraws a game
            // or a release, and shares that object's identity so the two can be matched up.
            if (kind == ClaimKind.Revocation)
            {
                if (identity.Kind is not (ClaimKind.Game or ClaimKind.Release))
                    throw new ClaimFormatException("a revocation must name a game or a release");
            }
            else if (identity.Kind != kind)
            {
                throw new ClaimFormatException("claim kind and identity kind disagree");
            }

            var audienceElement = RequireObject(root, "audience");
            RejectUnknownMembers(audienceElement, "audience", ["public", "campaignId", "tierIds"]);

            var isPublic = RequireBool(audienceElement, "public");
            var tierIds = new List<string>();
            if (audienceElement.TryGetProperty("tierIds", out var tiers))
            {
                if (tiers.ValueKind != JsonValueKind.Array)
                    throw new ClaimFormatException("audience.tierIds is not an array");
                foreach (var tier in tiers.EnumerateArray())
                {
                    if (tier.ValueKind != JsonValueKind.String)
                        throw new ClaimFormatException("audience.tierIds contains a non-string");

                    var tierId = tier.GetString()!;
                    // A blank tier id can never match a real entitlement, and a duplicate makes
                    // audience comparison ambiguous — which is how a narrowing gets mistaken for
                    // "unchanged" and nobody is told they lost access.
                    if (string.IsNullOrWhiteSpace(tierId))
                        throw new ClaimFormatException("audience.tierIds contains a blank tier id");
                    if (tierIds.Contains(tierId, StringComparer.Ordinal))
                        throw new ClaimFormatException($"audience.tierIds repeats '{tierId}'");
                    tierIds.Add(tierId);
                }
            }

            var audience = new ClaimAudience
            {
                Public = isPublic,
                CampaignId = OptionalString(audienceElement, "campaignId"),
                TierIds = tierIds
            };

            if (!audience.Public && (string.IsNullOrWhiteSpace(audience.CampaignId) || audience.TierIds.Count == 0))
                throw new ClaimFormatException("a non-public audience must name a campaign and at least one tier");
            if (audience.Public && (audience.CampaignId is not null || audience.TierIds.Count > 0))
                throw new ClaimFormatException("a public audience must not carry campaign or tier restrictions");

            // An object specifically: a body that is a bare string or array has no room for the
            // identity fields the verifier cross-checks against, and allowing it would mean two
            // shapes of "valid" claim to reason about.
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
                throw new ClaimFormatException("claim payload has no object body");

            var seq = RequireLong(root, "seq");
            if (seq < 0) throw new ClaimFormatException("seq is negative");

            return new ClaimPayload
            {
                V = v,
                TrustContext = RequireString(root, "trustContext"),
                Kind = kind,
                Identity = identity,
                Seq = seq,
                Audience = audience,
                BodyJson = body.GetRawText()
            };
        }
    }

    /// <summary>
    /// Envelope objects get an exact allowlist. Unknown members are tolerated inside <c>body</c> for
    /// forward compatibility, but identity and audience are envelope data: a member a strict reader
    /// ignores could carry meaning to a laxer one — an "excludeTierIds" that one implementation
    /// honours and another does not is a disclosure bug with a valid signature on it.
    /// </summary>
    private static void RejectUnknownMembers(JsonElement element, string where, string[] allowed)
    {
        foreach (var member in element.EnumerateObject())
        {
            if (!allowed.Contains(member.Name, StringComparer.Ordinal))
                throw new ClaimFormatException($"unknown member '{member.Name}' in {where}");
        }
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new ClaimFormatException($"'{name}' is missing or not an object");
        return value;
    }

    private static string RequireString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ClaimFormatException($"'{name}' is missing or not a string");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new ClaimFormatException($"'{name}' is not a string");
        return value.GetString();
    }

    private static bool RequireBool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False))
            throw new ClaimFormatException($"'{name}' is missing or not a boolean");
        return value.GetBoolean();
    }

    private static int RequireInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number))
            throw new ClaimFormatException($"'{name}' is missing or not a whole number");
        return number;
    }

    private static long RequireLong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var number))
            throw new ClaimFormatException($"'{name}' is missing or not a whole number");
        return number;
    }

    private static ClaimKind RequireEnum(JsonElement parent, string name)
    {
        var text = RequireString(parent, name);
        // Case-sensitive on purpose: "Release" and "release" must not both be accepted, or the same
        // bytes could be classified differently by two readers.
        return text switch
        {
            "Header" => ClaimKind.Header,
            "Game" => ClaimKind.Game,
            "Release" => ClaimKind.Release,
            "Revocation" => ClaimKind.Revocation,
            _ => throw new ClaimFormatException($"'{name}' is not a known claim kind: '{text}'")
        };
    }

    /// <summary>
    /// Identifies the content of a claim for equivocation checks — two claims with the same
    /// identity and sequence but different content are a trust violation.
    ///
    /// Hashes the PAYLOAD, never the signature: RSA-PSS is randomised, so re-signing an unchanged
    /// payload after a cache loss produces different signature bytes and would otherwise look like
    /// an author equivocating.
    /// </summary>
    public static string ContentHash(ReadOnlySpan<byte> payloadBytes) =>
        Convert.ToHexStringLower(SHA256.HashData(payloadBytes));
}

public sealed class ClaimFormatException : Exception
{
    public ClaimFormatException(string message) : base(message) { }
    public ClaimFormatException(string message, Exception inner) : base(message, inner) { }
}
