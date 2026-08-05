namespace AccessibilityModManager.Core.Models;

/// <summary>
/// The trust anchor a claim is signed under, taken from the signed registry entry for a plugin.
/// Everything here comes from the registry — never from the index being verified, which is the
/// thing under suspicion.
///
/// <para>It lives in Core rather than beside the verifier because the resolved trust state is a
/// property of the accepted registry model (<see cref="PluginEntry.IndexTrust"/>), and Core cannot
/// reference Infrastructure. The reader that produces one, and every use of it, stay in
/// Infrastructure with the cryptography.</para>
/// </summary>
public sealed record ClaimTrustAnchor
{
    public required string PluginId { get; init; }

    /// <summary>The exact repoIndexUrl from the signed registry, compared ordinally.</summary>
    public required string RepoIndexUrl { get; init; }

    public required string Scheme { get; init; }
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public required string PublicKeyPem { get; init; }

    public const string SchemeV1 = "signed-claims-v1";
    public const string AlgorithmRsaPssSha256 = "rsa-pss-sha256";
}

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

    /// <summary>
    /// A source the USER added themselves, which no registry vouches for. Unsigned, like
    /// <see cref="None"/>, and given the same capabilities once accepted — but deliberately its own
    /// state rather than a reuse of it.
    ///
    /// <para>The two are different facts. <see cref="None"/> means "the signed registry lists this
    /// plugin and names no key for it"; this means "no registry mentions it at all". Collapsing them
    /// would leave nothing in the type saying where an entry came from, so every place that needs
    /// to tell them apart would need a second, parallel flag — and two mechanisms that must agree
    /// are how a gap opens. A registry entry can never be stamped with this state
    /// (<see cref="PluginEntry.ResolveIndexTrust"/> refuses it) and a user source can never be
    /// stamped with any other, so the pairing of origin and trust is decided once, at
    /// construction.</para>
    /// </summary>
    UserApprovedUnsigned,

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
    /// while publishing and by the manager while reading, so it must not tell a publisher to
    /// update the manager. Callers add their own remedy.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Nobody has asked the registry. Not a state any reader produces — it is what an unstamped
    /// holder reads as, so that forgetting to resolve fails closed instead of granting the unsigned
    /// path.
    /// </summary>
    public static readonly IndexTrustResolution Unresolved = new(IndexTrustStatus.Unresolved, null, null);

    public static readonly IndexTrustResolution NoAnchor = new(IndexTrustStatus.None, null, null);

    /// <summary>
    /// A user-added source: unsigned, and no registry vouches for it. Carries no anchor, because
    /// there is nothing to anchor to — the user's decision to add it is the whole of its trust.
    /// </summary>
    public static readonly IndexTrustResolution UserApprovedUnsigned =
        new(IndexTrustStatus.UserApprovedUnsigned, null, null);

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
