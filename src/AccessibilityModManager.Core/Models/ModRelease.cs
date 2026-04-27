namespace AccessibilityModManager.Core.Models;

/// <summary>
/// A specific mod version available for download from a plugin's repo.
/// </summary>
public sealed class ModRelease
{
    public required string GameId { get; init; }
    public required string PluginId { get; init; }
    public required string Version { get; init; }
    public required string Channel { get; init; } // "stable" or "beta"

    /// <summary>
    /// Public HTTPS URL for the wrapped ZIP. Set on every public release. <c>null</c> when
    /// <see cref="Patreon"/> is set instead — the manager fetches the asset from Patreon's
    /// CDN at install time using the patron's OAuth token. Exactly one of
    /// <c>PackageUrl</c> / <c>Patreon</c> must be present.
    /// </summary>
    public Uri? PackageUrl { get; init; }

    public required string Sha256 { get; init; }
    public string? ChangelogUrl { get; init; }
    /// <summary>
    /// Release notes / changelog content for this version, optional. Markdown. Stored
    /// alongside the release in index.json so the manager can render the changelog
    /// in-app without a network round-trip. The same text gets pushed to the GitHub
    /// release body when the AuthorTool uploads, so the two stay in sync.
    /// </summary>
    public string? Notes { get; init; }
    public CompatibilityInfo? Compatibility { get; init; }

    /// <summary>
    /// Optional Patreon gate for this release. When present, the wrapped ZIP is hosted as
    /// an attachment on a tier-locked Patreon post (Q1=C). The manager only downloads it
    /// when the user is OAuth-authenticated and currently entitled to one of the listed
    /// tiers. Hides the release entirely from non-entitled users (Q3=A).
    /// </summary>
    public PatreonGate? Patreon { get; init; }

    // Used by the Version ComboBox's SelectedItem announcement and anywhere else that falls back
    // to ToString. Without this override the screen reader would say the type's full name.
    public override string ToString() => Version;
}

public sealed class CompatibilityInfo
{
    public string? MinGameVersion { get; init; }
    public string? MaxGameVersion { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// Patreon-attachment hosting for a release. The author manually creates a tier-locked
/// post on Patreon and attaches the wrapped ZIP; this block tells the manager which
/// post to fetch and which campaign / tier IDs grant access. Patron-side reading is the
/// only API path Patreon supports — programmatic post creation is not (see
/// PATREON_API_FINDINGS.md).
/// </summary>
public sealed class PatreonGate
{
    /// <summary>The author's Patreon campaign id. Same value across all of an author's gated releases.</summary>
    public required string CampaignId { get; init; }

    /// <summary>
    /// Tier ids that grant access. Any-of matching — a user entitled to any tier in the
    /// list passes the gate. Patreon tier ids don't have a guaranteed numeric ordering, so
    /// "this tier or higher" isn't a thing; authors enumerate every tier explicitly.
    /// </summary>
    public required List<string> TierIds { get; init; }

    /// <summary>
    /// Optional Patreon post id (numeric) the wrapped ZIP is attached to. Used by the
    /// file-picker fallback flow — the manager opens this post in the patron's browser so
    /// they can grab the attachment manually. Null when the author has set up an
    /// auto-download server (<see cref="ServerUrl"/>) and skipped the Patreon-post
    /// infrastructure entirely; in that case the manager downloads from the server with
    /// the patron's bearer token and never needs a post URL.
    /// </summary>
    public string? PostId { get; init; }

    /// <summary>
    /// Optional filename to pick out of a post that has multiple attachments. Supports the
    /// "one post per game, many files" pattern (Q1=A) — an author keeps a single Patreon
    /// post per game and attaches each new wrapped-ZIP version to it; this field tells the
    /// manager which file on the post belongs to <em>this</em> release. Null means "first
    /// attachment", which is the original one-post-per-release behaviour.
    /// </summary>
    public string? AttachmentFileName { get; init; }

    /// <summary>
    /// Optional HTTPS URL of an author-hosted download server that streams the wrapped ZIP
    /// after validating the patron's Patreon bearer token. Lets the manager auto-download
    /// gated releases without the manual "open Patreon, download, point manager at file"
    /// dance — Patreon's public API doesn't expose post attachment URLs to anyone (creator
    /// or patron) anymore, so the only way to keep auto-download alive is for the author
    /// to host the files themselves with their own token-validating endpoint.
    /// When null/empty, the manager falls back to the file-picker flow. Server-side
    /// implementation reference lives in <c>PATREON_VPS_SETUP.md</c>; the AuthorTool
    /// uploads to it via SFTP per <c>AUTOMATED_RELEASE_UPLOAD.md</c>.
    /// </summary>
    public string? ServerUrl { get; init; }
}
