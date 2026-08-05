using System.Text.Json.Serialization;

namespace AccessibilityModManager.Core.Models;

/// <summary>
/// An entry in the central plugin registry. Represents one plugin author's listing.
/// </summary>
public sealed class PluginEntry
{
    private IndexTrustResolution? _indexTrust;

    /// <summary>
    /// Who may sign this plugin's catalog, according to the signed registry.
    ///
    /// <para><b>Never deserialized.</b> The registry document carries a member literally named
    /// <c>indexTrust</c>, so a settable property of this name would let the served plaintext choose
    /// its own trust anchor — the document under suspicion deciding what it is checked against. It is
    /// get-only AND <see cref="JsonIgnoreAttribute"/>: get-only already stops it under today's
    /// serializer options, and the attribute keeps it stopped if those options ever change. The only
    /// way in is <see cref="ResolveIndexTrust"/>, called by the registry acceptance gate after the
    /// signature has been verified, through the one strict reader.</para>
    ///
    /// <para>An entry nobody resolved reads as <see cref="IndexTrustStatus.Unresolved"/>, which every
    /// consumer refuses. Forgetting the gate therefore fails closed, rather than reading as
    /// <see cref="IndexTrustStatus.None"/> — which is the permission to read an unsigned catalog.</para>
    /// </summary>
    [JsonIgnore]
    public IndexTrustResolution IndexTrust => _indexTrust ?? IndexTrustResolution.Unresolved;

    /// <summary>
    /// Stamps the resolved trust state onto this entry. Called once, by the registry acceptance gate.
    ///
    /// <para>A second call throws rather than overwriting. Two gates would mean two answers to "who
    /// signs this", and the later one wins by arriving later — which is a downgrade path, not a
    /// correction. <see cref="IndexTrustStatus.Unresolved"/> is refused as an argument for the same
    /// reason it is the zero value: it is the absence of an answer, not one.</para>
    /// </summary>
    public void ResolveIndexTrust(IndexTrustResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        if (resolution.Status == IndexTrustStatus.Unresolved)
        {
            throw new ArgumentException(
                "Unresolved is the absence of an answer, not one — resolve to None, Anchored or Unusable.",
                nameof(resolution));
        }

        // A registry entry is, by definition, one the signed registry vouches for. Stamping it with
        // the user-source state would say the opposite while the entry still carried a registry id
        // and a registry index URL — an entry claiming both provenances at once. Refused here so
        // that the pairing cannot be written down, rather than being a rule each consumer has to
        // remember to check.
        if (resolution.Status == IndexTrustStatus.UserApprovedUnsigned)
        {
            throw new ArgumentException(
                $"'{IndexTrustStatus.UserApprovedUnsigned}' belongs to sources the user added themselves. " +
                $"A registry entry ('{Id}') cannot hold it — build a user source through its own factory instead.",
                nameof(resolution));
        }

        if (_indexTrust is not null)
        {
            throw new InvalidOperationException(
                $"The trust state for plugin '{Id}' has already been resolved; it is set once, by the " +
                "registry acceptance gate.");
        }

        _indexTrust = resolution;
    }

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string Description { get; init; }
    public required Uri RepoIndexUrl { get; init; }
    public Uri? Website { get; init; }
    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// Flexible links the plugin author wants to share — Discord, GitHub, Patreon,
    /// donation pages, documentation, or anything else.
    /// Key = label (e.g., "Discord", "GitHub", "Donate"), Value = URL.
    /// </summary>
    public Dictionary<string, Uri> Links { get; init; } = [];

    /// <summary>
    /// Open-ended metadata for future use. Plugin authors can include any extra
    /// key-value info here without needing a schema change.
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = [];
}
