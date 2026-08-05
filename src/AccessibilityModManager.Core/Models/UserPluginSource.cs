namespace AccessibilityModManager.Core.Models;

/// <summary>
/// A catalog the user added themselves, persisted in <see cref="AppConfig.UserPluginSources"/>.
///
/// <para>Nothing vouches for one of these. The user was shown what a source can do and chose to add
/// it, and that decision is recorded here rather than only in whichever screen happened to make it —
/// see <see cref="NoticeAcceptedUtc"/>.</para>
///
/// <para>Unlike <see cref="AppConfig.PluginRegistryUrl"/>, this IS user data and is deserialized.
/// That is safe because a source can only ever be an INDEX, never a registry: it contributes one
/// author's catalog under one plugin id, so it can never introduce a second author or redirect the
/// trust anchor. Adding a source and changing the registry stay different operations.</para>
/// </summary>
public sealed class UserPluginSource
{
    /// <summary>
    /// The plugin id this source publishes under, pinned when it was added. This is the source's
    /// identity: a source whose index later claims a different id is a different source, and is
    /// refused rather than silently followed.
    /// </summary>
    public string PluginId { get; set; } = "";

    /// <summary>Absolute HTTPS address of the author's index.json.</summary>
    public string IndexUrl { get; set; } = "";

    /// <summary>The author's display name at the time it was added, for the list. Cosmetic only.</summary>
    public string? DisplayName { get; set; }

    public DateTimeOffset AddedUtc { get; set; }

    /// <summary>
    /// When the user accepted the risk notice, and for WHICH source — see
    /// <see cref="AcceptedFor"/>. Absent means it has never been accepted.
    /// </summary>
    public DateTimeOffset? NoticeAcceptedUtc { get; set; }

    /// <summary>
    /// The identity the acceptance was given for: the canonical plugin id and the exact index
    /// address, as <see cref="AcceptanceKey"/> builds them. Recomputed on load and compared, so
    /// editing either field in the settings file returns the source to "not accepted" rather than
    /// letting an old approval carry over to a different developer or a different address.
    ///
    /// <para><b>What this is and is not.</b> It is not a signature and cannot be. Everything here
    /// lives in one file the user's own account can write, so anything able to forge a source can
    /// forge this too. What it genuinely stops is the realistic case: a record edited in place, or
    /// appended without knowing the format, silently inheriting an approval the user gave for
    /// something else. Treat it as "this approval belongs to this exact source", not as proof of
    /// who wrote the line.</para>
    /// </summary>
    public string? AcceptedFor { get; set; }

    /// <summary>
    /// The value <see cref="AcceptedFor"/> must hold for this source to count as accepted. Ordinal
    /// on the URL because a different address is a different source even when it differs only by
    /// case or a trailing slash.
    /// </summary>
    public static string AcceptanceKey(string? pluginId, string? indexUrl) =>
        $"{SafeId.Canonical(pluginId).ToLowerInvariant()}|{indexUrl?.Trim() ?? ""}";
}
