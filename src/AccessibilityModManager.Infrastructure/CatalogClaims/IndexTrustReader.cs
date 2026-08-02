using System.Text.Json;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Infrastructure.CatalogClaims;

/// <summary>
/// The trust state of every plugin in one registry document — or the single reason the document
/// itself could not be read.
///
/// <para>The two are separate states rather than one map that happens to be empty. An empty map
/// reads as "no plugin has an anchor", and that is the permission to treat every catalog as
/// unsigned. A caller has to deal with <see cref="DocumentError"/> before it can reach the
/// resolutions at all: <see cref="ByPluginId"/> throws while one is set.</para>
/// </summary>
public sealed record RegistryTrustResolutions
{
    private readonly IReadOnlyDictionary<string, IndexTrustResolution>? _byPluginId;

    private RegistryTrustResolutions(
        string? documentError, IReadOnlyDictionary<string, IndexTrustResolution>? byPluginId)
    {
        DocumentError = documentError;
        _byPluginId = byPluginId;
    }

    /// <summary>Non-null exactly when the document could not be read at all.</summary>
    public string? DocumentError { get; }

    /// <summary>One resolution per requested plugin id. Throws while <see cref="DocumentError"/> is set.</summary>
    public IReadOnlyDictionary<string, IndexTrustResolution> ByPluginId =>
        _byPluginId ?? throw new InvalidOperationException(
            $"The registry document could not be read ({DocumentError}); there are no per-plugin " +
            "resolutions to read.");

    public static RegistryTrustResolutions Broken(string documentError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentError);
        return new(documentError, null);
    }

    public static RegistryTrustResolutions Resolved(IReadOnlyDictionary<string, IndexTrustResolution> byPluginId)
    {
        ArgumentNullException.ThrowIfNull(byPluginId);
        return new(null, byPluginId);
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
    ///
    /// <para>A document-level failure comes back as <see cref="IndexTrustStatus.Unusable"/> for this
    /// plugin, which is the right answer for a caller asking about exactly one. A caller resolving a
    /// whole registry wants <see cref="ResolveAll"/>, which reports that once instead of once per
    /// plugin.</para>
    /// </summary>
    public static IndexTrustResolution Resolve(string verifiedRegistryJson, string pluginId)
    {
        var all = ResolveAll(verifiedRegistryJson, [pluginId]);
        return all.DocumentError is { } error
            ? IndexTrustResolution.Unusable(error)
            : all.ByPluginId[pluginId];
    }

    /// <summary>
    /// Resolves every requested plugin from ONE parse of the registry.
    ///
    /// <para>Two levels of failure, deliberately distinct. A broken <b>anchor</b> refuses its own
    /// plugin and leaves the rest of the catalog working — the settled rule, because on a
    /// multi-author registry one author's typo must not dark every user's whole catalog. A broken
    /// <b>document</b> is not an anchor: it is the signed trust root failing to parse, which darkens
    /// everything whichever way it is reported, so it is reported once and clearly rather than as N
    /// identical per-plugin refusals.</para>
    /// </summary>
    public static RegistryTrustResolutions ResolveAll(string verifiedRegistryJson, IEnumerable<string> pluginIds)
    {
        var wanted = new HashSet<string>(pluginIds, StringComparer.Ordinal);

        JsonDocument document;
        try
        {
            // Duplicates are ALLOWED at the parser and refused per plugin below.
            //
            // Rejecting them here instead reads as stricter and breaks the settled rule: the setting
            // is recursive, so one author repeating `indexTrust` — or any member, anywhere in their
            // entry — throws before anything knows which entry it was, and the whole registry goes
            // dark. That is exactly the multi-author failure the per-plugin rule exists to prevent,
            // arriving through the parser. Duplicates still refuse; they refuse locally.
            document = JsonDocument.Parse(verifiedRegistryJson);
        }
        catch (JsonException ex)
        {
            return RegistryTrustResolutions.Broken($"the registry could not be read ({ex.Message})");
        }

        using (document)
        {
            // A root that is not an object is not a registry. Checked rather than assumed, because
            // JsonElement.TryGetProperty THROWS InvalidOperationException on a non-object element
            // instead of returning false — so without this the reader would fault rather than
            // refuse, and a fault is not a decision anyone can act on.
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return RegistryTrustResolutions.Broken("the registry is not a set of values");

            // A repeated member at the ROOT is not localisable to any plugin — two `plugins` arrays
            // is two registries — so this one does refuse the document.
            if (HasRepeatedMember(document.RootElement))
                return RegistryTrustResolutions.Broken("the registry repeats a top-level entry");

            // Missing or non-array `plugins` is structural breakage, not "this plugin isn't listed".
            // Only a well-formed collection is allowed to establish genuine absence, because absence
            // is the state that grants the unsigned path.
            if (!document.RootElement.TryGetProperty("plugins", out var plugins) ||
                plugins.ValueKind != JsonValueKind.Array)
            {
                return RegistryTrustResolutions.Broken("the registry carries no list of plugins");
            }

            // The WHOLE array is scanned, and two entries sharing an id refuse that id.
            //
            // Returning on the first match looks equivalent and is not: JSON permits repeated array
            // elements, and AllowDuplicateProperties only governs repeated MEMBERS of one object —
            // verified by probe, two elements both with "id": "amethyst" parse without complaint. So
            // an unanchored entry placed before an anchored one would have resolved to None, and None
            // is permission to publish and read unsigned. Whoever writes the registry does not get to
            // choose which of two answers a reader sees.
            var matches = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var duplicated = new HashSet<string>(StringComparer.Ordinal);

            foreach (var plugin in plugins.EnumerateArray())
            {
                if (plugin.ValueKind != JsonValueKind.Object) continue;
                if (!plugin.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;

                var pluginId = id.GetString()!;
                if (!wanted.Contains(pluginId)) continue;

                if (!matches.TryAdd(pluginId, plugin)) duplicated.Add(pluginId);
            }

            var resolved = new Dictionary<string, IndexTrustResolution>(StringComparer.Ordinal);
            foreach (var pluginId in wanted)
            {
                resolved[pluginId] =
                    duplicated.Contains(pluginId)
                        ? IndexTrustResolution.Unusable(
                            $"the registry lists '{pluginId}' more than once, so which key signs its " +
                            "catalog is ambiguous")
                        : matches.TryGetValue(pluginId, out var entry)
                            ? ResolveEntry(entry, pluginId)
                            : IndexTrustResolution.NoAnchor;
            }

            return RegistryTrustResolutions.Resolved(resolved);
        }
    }

    /// <summary>
    /// True when an object names the same member twice.
    ///
    /// <para>Checked by hand because the parser's recursive setting cannot be scoped to one entry,
    /// and this question has to be answerable per plugin. A repeated member means two readers can
    /// each behave correctly and reach different keys — including two <c>indexTrust</c> blocks, where
    /// one of the two answers may be "no anchor", which is permission.</para>
    /// </summary>
    private static bool HasRepeatedMember(JsonElement element)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in element.EnumerateObject())
            if (!seen.Add(member.Name)) return true;

        return false;
    }

    private static IndexTrustResolution ResolveEntry(JsonElement plugin, string pluginId)
    {
        // Localised duplicate detection: this entry, and the trust block inside it. Anything else in
        // the document repeating a member is somebody else's problem and must not be this plugin's.
        if (HasRepeatedMember(plugin))
        {
            return IndexTrustResolution.Unusable(
                $"the registry entry for '{pluginId}' names the same thing twice, so which key signs " +
                "its catalog is ambiguous");
        }

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

        if (HasRepeatedMember(trust))
        {
            return IndexTrustResolution.Unusable(
                $"the signing-key block for '{pluginId}' names the same thing twice, so which key " +
                "signs its catalog is ambiguous");
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
