using System.Text.Json;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// What a signed registry says about who may sign a plugin's index.
///
/// <para><see cref="Unresolved"/> is the zero value on purpose. Any field or property that holds one
/// of these and was never assigned reads as "nobody has asked the registry yet", which every
/// consumer must refuse — rather than as <see cref="None"/>, which is a permission. The states that
/// grant something are the ones you have to write down.</para>
/// </summary>
public enum IndexTrustStatus
{
    /// <summary>Never computed. Not an answer; consumers fail closed on it.</summary>
    Unresolved = 0,

    /// <summary>The registry names no signing key for this plugin — the unsigned path, unchanged.</summary>
    None,

    /// <summary>The registry names a key this manager can verify against.</summary>
    Anchored,

    /// <summary>
    /// The registry names something where a key belongs, and it cannot be used. Never collapses into
    /// <see cref="None"/>: "there is no anchor" is a permission to read an unsigned catalog, and a
    /// broken anchor must never be able to grant it.
    /// </summary>
    Unusable
}

/// <summary>
/// The resolved trust state for one plugin, plus the reason when there isn't one.
///
/// <para>The constructor is private and the properties are get-only, so the invalid combinations
/// cannot be written down at all: no <see cref="IndexTrustStatus.None"/> carrying an anchor, no
/// <see cref="IndexTrustStatus.Anchored"/> without one. A consumer that asks "is there an anchor?"
/// and one that asks "is the status Anchored?" therefore cannot disagree — which matters, because
/// several consumers ask it each way and the wrong answer grants the unsigned path.</para>
/// </summary>
public sealed record IndexTrustResolution
{
    private IndexTrustResolution(IndexTrustStatus status, ClaimTrustAnchor? anchor, string? reason)
    {
        Status = status;
        Anchor = anchor;
        Reason = reason;
    }

    public IndexTrustStatus Status { get; }

    /// <summary>Non-null exactly when <see cref="Status"/> is <see cref="IndexTrustStatus.Anchored"/>.</summary>
    public ClaimTrustAnchor? Anchor { get; }

    /// <summary>
    /// Non-null exactly when <see cref="Status"/> is <see cref="IndexTrustStatus.Unusable"/>. Written
    /// to be read aloud: it reaches the user as the reason a plugin is missing from their catalog,
    /// and it is deliberately application-neutral — the same sentence is surfaced by the AuthorTool
    /// while publishing and (later) by the manager while reading, so it must not tell a publisher to
    /// update the manager. Callers add their own remedy.
    /// </summary>
    public string? Reason { get; }

    public static readonly IndexTrustResolution NoAnchor = new(IndexTrustStatus.None, null, null);

    /// <summary>
    /// The arguments are checked at RUNTIME, not merely annotated. A private constructor stops an
    /// object initializer writing down an invalid combination, and stops nothing at all coming
    /// through here — `Anchored(null!)` would have produced an Anchored resolution with no anchor,
    /// making the invariant above false while consumers dereference <c>Anchor!</c> on the strength
    /// of it. Nullable annotations are a compiler courtesy, not enforcement.
    /// </summary>
    public static IndexTrustResolution Anchored(ClaimTrustAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return new(IndexTrustStatus.Anchored, anchor, null);
    }

    /// <summary>
    /// A blank reason is refused as well as a null one: this text is the whole of what a user hears
    /// about why a plugin vanished from their catalog, and an empty refusal is indistinguishable
    /// from the failure having no explanation at all.
    /// </summary>
    public static IndexTrustResolution Unusable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(IndexTrustStatus.Unusable, null, reason);
    }
}

/// <summary>
/// Reads a plugin's <c>indexTrust</c> block out of a signature-verified registry document.
///
/// <para><b>One reader, two applications.</b> The AuthorTool decides what to sign with this and the
/// manager decides what to trust with it, and the value both derive — the trust context — is hashed
/// into every claim. Two implementations that disagree about a single byte produce catalogs the
/// other cannot read, so there is deliberately only one.</para>
///
/// <para><b>Why the raw JSON and not the deserialized model.</b> The trust context binds the index
/// address as the exact string the registry carries (<see cref="ClaimTrustContext.Compute"/>). A
/// <see cref="Uri"/> round-trip is a normalising one: percent-encoding case, default ports, IDN
/// hosts and a trailing dot in the host can all come back spelled differently than they went in.
/// Today's live registry is unaffected — which is precisely the danger, because the first author it
/// does affect would see every manager refuse a perfectly good proof, with nothing in the failure
/// naming the cause.</para>
///
/// <para>The caller must have verified the registry's signature first. Nothing here checks that,
/// and everything here trusts what it reads.</para>
/// </summary>
public static class IndexTrustReader
{
    /// <summary>Bounds on the two free-text members, applied before any cryptography is attempted.</summary>
    private const int MaxKeyIdLength = 128;
    private const int MaxPublicKeyPemLength = 8 * 1024;

    /// <summary>
    /// The members <c>indexTrust</c> may carry. Anything else refuses the plugin rather than being
    /// ignored: this is a versioned security block, and the version is the scheme name, so a member
    /// nobody here recognises means the document was written to a contract this build does not
    /// implement. Extending it is what a scheme bump is for.
    /// </summary>
    private static readonly HashSet<string> KnownMembers =
        new(StringComparer.Ordinal) { "scheme", "keyId", "algorithm", "publicKeyPem" };

    /// <summary>
    /// Resolves the trust state for <paramref name="pluginId"/>.
    ///
    /// <para>The id is matched <b>ordinally</b> and only ordinally. A trust anchor decides which key
    /// may sign for a plugin, and matching that loosely would make 'amethyst' and 'Amethyst' one
    /// identity. An entry that differs only in capitalisation therefore resolves to
    /// <see cref="IndexTrustStatus.None"/> here, and is surfaced as its own refusal by
    /// <c>TryReadIndexUrl</c>, which exists to see exactly that disagreement.</para>
    /// </summary>
    public static IndexTrustResolution Resolve(string verifiedRegistryJson, string pluginId)
    {
        JsonDocument document;
        try
        {
            // Duplicates refuse rather than resolving to first- or last-wins. The setting applies
            // recursively, so it also covers two `indexTrust` members in one entry — an ambiguity
            // where two readers can each behave "correctly" and reach different keys.
            document = JsonDocument.Parse(
                verifiedRegistryJson, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException ex)
        {
            return IndexTrustResolution.Unusable($"the registry could not be read ({ex.Message})");
        }

        using (document)
        {
            // A root that is not an object is not a registry. Checked rather than assumed, because
            // JsonElement.TryGetProperty THROWS InvalidOperationException on a non-object element
            // instead of returning false — so without this the reader would fault rather than
            // refuse, and a fault is not a decision anyone can act on.
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return IndexTrustResolution.Unusable("the registry is not a set of values");

            // Missing or non-array `plugins` is structural breakage, not "this plugin isn't listed".
            // Only a well-formed collection is allowed to establish genuine absence, because absence
            // is the state that grants the unsigned path.
            if (!document.RootElement.TryGetProperty("plugins", out var plugins) ||
                plugins.ValueKind != JsonValueKind.Array)
            {
                return IndexTrustResolution.Unusable("the registry carries no list of plugins");
            }

            // The WHOLE array is scanned, and two entries sharing this id refuse.
            //
            // Returning on the first match looks equivalent and is not: JSON permits repeated array
            // elements, and AllowDuplicateProperties only governs repeated MEMBERS of one object —
            // verified by probe, two elements both with "id": "amethyst" parse without complaint. So
            // an unanchored entry placed before an anchored one would have resolved to None, and None
            // is permission to publish and read unsigned. Whoever writes the registry does not get to
            // choose which of two answers a reader sees.
            JsonElement? match = null;
            foreach (var plugin in plugins.EnumerateArray())
            {
                if (plugin.ValueKind != JsonValueKind.Object) continue;
                if (!plugin.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                if (!string.Equals(id.GetString(), pluginId, StringComparison.Ordinal)) continue;

                if (match is not null)
                {
                    return IndexTrustResolution.Unusable(
                        $"the registry lists '{pluginId}' more than once, so which key signs its " +
                        "catalog is ambiguous");
                }

                match = plugin;
            }

            return match is null ? IndexTrustResolution.NoAnchor : ResolveEntry(match.Value, pluginId);
        }
    }

    private static IndexTrustResolution ResolveEntry(JsonElement plugin, string pluginId)
    {
        if (!plugin.TryGetProperty("indexTrust", out var trust))
            return IndexTrustResolution.NoAnchor;

        // Present and not an object. This is the state that must never be reported as absent: a
        // caller reading "no anchor" treats an anchored catalog as an unsigned one, and on the
        // publishing side that means signing nothing over a catalog every manager expects a proof
        // for.
        if (trust.ValueKind != JsonValueKind.Object)
        {
            return IndexTrustResolution.Unusable(
                $"the registry entry for '{pluginId}' has a signing-key block that isn't a set of " +
                "values, so there is no way to tell which key signs this catalog");
        }

        foreach (var member in trust.EnumerateObject())
        {
            if (!KnownMembers.Contains(member.Name))
            {
                // Application-neutral on purpose: the AuthorTool surfaces this while PUBLISHING, so
                // "update the manager" would send a publisher to the wrong program, and a typo has
                // no newer release to update to at all. Each caller adds its own remedy.
                return IndexTrustResolution.Unusable(
                    $"the signing-key block for '{pluginId}' carries '{member.Name}', which this " +
                    "version doesn't understand");
            }
        }

        // The address is part of what a claim is signed over, so an entry that cannot say where
        // managers read cannot have its claims verified either.
        if (!plugin.TryGetProperty("repoIndexUrl", out var url) || url.ValueKind != JsonValueKind.String)
        {
            return IndexTrustResolution.Unusable(
                $"the registry entry for '{pluginId}' names a signing key but doesn't say where its " +
                "catalog is read from");
        }

        if (!TryMember(trust, "scheme", out var scheme, out var missing) ||
            !TryMember(trust, "keyId", out var keyId, out missing) ||
            !TryMember(trust, "algorithm", out var algorithm, out missing) ||
            !TryMember(trust, "publicKeyPem", out var publicKeyPem, out missing))
        {
            return IndexTrustResolution.Unusable(
                $"the signing-key block for '{pluginId}' is missing {missing}");
        }

        if (keyId.Length > MaxKeyIdLength)
            return IndexTrustResolution.Unusable($"the signing key named for '{pluginId}' has an over-long name");
        if (publicKeyPem.Length > MaxPublicKeyPemLength)
            return IndexTrustResolution.Unusable($"the signing key named for '{pluginId}' is implausibly large");

        var anchor = new ClaimTrustAnchor
        {
            PluginId = pluginId,
            RepoIndexUrl = url.GetString()!,
            Scheme = scheme,
            KeyId = keyId,
            Algorithm = algorithm,
            PublicKeyPem = publicKeyPem
        };

        // Usable is defined as "a verifier can be built from it", rather than by restating the
        // scheme, algorithm and key-size rules here. Restating them is how the two would drift, and
        // an anchor this reader blessed that the verifier then rejects is a catalog that fails at
        // the point of use with a worse message.
        try
        {
            using var probe = new ClaimVerifier(anchor);
        }
        catch (ClaimFormatException ex)
        {
            return IndexTrustResolution.Unusable(
                $"the signing key the registry names for '{pluginId}' can't be used ({ex.Message})");
        }
        catch (Exception ex)
        {
            // A malformed PEM surfaces as a CryptographicException from ImportFromPem.
            return IndexTrustResolution.Unusable(
                $"the signing key the registry names for '{pluginId}' can't be read ({ex.Message})");
        }

        return IndexTrustResolution.Anchored(anchor);
    }

    private static bool TryMember(JsonElement trust, string name, out string value, out string missing)
    {
        missing = name;
        value = "";

        if (!trust.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text)) return false;

        value = text;
        return true;
    }
}
