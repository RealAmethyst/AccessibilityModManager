namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// Persisted to %LocalAppData%\AccessibilityModManager-Author\config.json. Holds the list of
/// projects the user has worked on and per-project metadata (per-game source repos used by
/// the release upload flow). The on-disk index.json never carries author-only data — it stays
/// in this config so end users of the manager don't see it.
/// </summary>
public sealed class AuthorConfig
{
    public List<RecentProject> RecentProjects { get; set; } = [];
    public string? LastOpenedProjectPath { get; set; }

    /// <summary>
    /// Last folder the registry-admin view used as the local checkout of the registry repo.
    /// Only meaningful in admin builds; harmless to persist either way.
    /// </summary>
    public string? LastRegistryRepoPath { get; set; }

    /// <summary>
    /// SFTP / SSH config for the author's Patreon-gate download server. When populated, the
    /// AuthorTool uploads each Patreon-gated wrapped ZIP and its tier-gate metadata to the
    /// server so the manager can fetch it with the patron's bearer token. Null when the
    /// author hasn't set up a server yet — the AuthorTool falls back to no auto-upload.
    /// </summary>
    public ServerUploadConfig? ServerUpload { get; set; }

    /// <summary>
    /// The keys this author signs catalog claims with, one per plugin, keyed by plugin id.
    ///
    /// Deliberately a separate section from <see cref="ServerUpload"/>: that one holds an SSH
    /// credential for moving files, this one holds signing identities that vouch for content.
    /// Conflating a transport credential with a signing key would throw away the separation that
    /// makes having two keys worthwhile.
    ///
    /// Per plugin rather than per author, which the first version got wrong. One key for everything
    /// an author publishes means a single compromise reaches every plugin they have — the opposite
    /// of why the catalog key is split from the registry key in the first place — and it made a
    /// second plugin impossible to publish at all, since creating its key was refused on the
    /// grounds that "a key already exists".
    /// </summary>
    public Dictionary<string, ClaimSigningConfig> ClaimSigningKeys { get; set; } =
        new(StringComparer.Ordinal);
}

/// <summary>
/// Where this author's catalog-claim signing key lives, and how to unlock it without asking.
///
/// The registry key it sits beside is picked from a file dialog and unlocked by typing a passphrase
/// every time — fine for something signed a few times a year. Claims are signed on every publish,
/// so that would put a passphrase prompt in the middle of a routine release, which the design
/// explicitly rules out. Hence stored path plus stored passphrase.
/// </summary>
public sealed class ClaimSigningConfig
{
    /// <summary>The plugin these claims are published under. Bound into every signature.</summary>
    public string PluginId { get; set; } = "";

    /// <summary>Identifies the key in the signed registry, so rotation is expressible.</summary>
    public string KeyId { get; set; } = "";

    /// <summary>Encrypted PKCS#8 private key on this machine. Never leaves it except as an export.</summary>
    public string PrivateKeyPath { get; set; } = "";

    /// <summary>
    /// Passphrase for the key file. DPAPI-protected at rest; always cleartext in memory, exactly
    /// like the SSH passphrase beside it.
    /// </summary>
    public string Passphrase { get; set; } = "";

    /// <summary>
    /// True when <see cref="Passphrase"/> holds a DPAPI blob. An explicit flag, never inferred from
    /// the value's shape — a passphrase that happens to look like ciphertext must survive.
    /// </summary>
    public bool PassphraseProtected { get; set; }

    /// <summary>
    /// Public half, as published in the registry. Kept here so the tool can prove the private key
    /// still matches what the registry vouches for before signing anything with it.
    /// </summary>
    public string PublicKeyPem { get; set; } = "";

    /// <summary>SHA-256 of the key's DER SubjectPublicKeyInfo, for display and comparison.</summary>
    public string PublicKeyFingerprint { get; set; } = "";
}

/// <summary>
/// Where the AuthorTool publishes Patreon-gated wrapped ZIPs to. SFTP under the same SSH
/// key the user already uses for PuTTY / FileZilla — no separate password or admin token.
/// All fields are required when auto-upload is enabled.
/// </summary>
public sealed class ServerUploadConfig
{
    /// <summary>SFTP host the AuthorTool connects to.</summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// OpenSSH-style SHA256 fingerprint of the server's host key. Null for configs saved
    /// before host-key pinning was added; connections then fail closed and report the
    /// presented fingerprint so the author can verify it out-of-band.
    /// </summary>
    public string? HostKeyFingerprint { get; set; }

    /// <summary>
    /// True when <c>KeyPassphrase</c> is stored as a DPAPI blob. An explicit flag, never inferred
    /// from the value's shape — a passphrase that happens to start with "dpapi:" must survive
    /// (audit finding 38). In memory the passphrase is always cleartext and this is false.
    /// </summary>
    public bool KeyPassphraseProtected { get; set; }

    /// <summary>SSH username on the VPS that owns the releases folder.</summary>
    public string User { get; set; } = "";

    /// <summary>
    /// Filesystem path of the catalog web root on the VPS — where plugin-registry.json, its
    /// signature, and plugins/&lt;id&gt;/index.json are served from. Publishing writes here via
    /// staged uploads and atomic renames. Absent in old configs → the known default.
    /// </summary>
    public string RemoteCatalogRoot { get; set; } = "/var/www/accessibilitymods.com/registry";

    /// <summary>
    /// Path to the OpenSSH-format private key on the local machine. SSH.NET reads this
    /// file at upload time; it's never copied or sent over the wire.
    /// </summary>
    public string PrivateKeyPath { get; set; } = "";

    /// <summary>
    /// Optional passphrase for the private key. Empty if the key has no passphrase (the
    /// usual case for a single-user dev workstation).
    /// </summary>
    public string KeyPassphrase { get; set; } = "";

    /// <summary>
    /// Absolute POSIX base path on the server where each <c>{gameId}/{version}/</c>
    /// subfolder lives.
    /// </summary>
    public string RemoteBasePath { get; set; } = "";

    /// <summary>
    /// Public HTTPS base URL the manager hits to download a release. The AuthorTool writes
    /// <c>{PublicBaseUrl}/{gameId}/{version}/{filename}</c> into the index entry's Patreon
    /// block as the <c>serverUrl</c>. Must start with <c>https://</c>.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>SFTP port. Defaults to 22; only set if the server runs on a non-standard port.</summary>
    public int Port { get; set; } = 22;
}

public sealed class RecentProject
{
    public required string Path { get; set; }
    public string? DisplayName { get; set; }
    public string? GitHubRepo { get; set; }
    public Dictionary<string, string> GameSourceRepos { get; set; } = new();

    /// <summary>
    /// Per-game absolute file paths that the author picked via the Scripts-tab Browse button.
    /// The wrapped ZIP build always pulls scripts from these paths so the user can keep their
    /// scripts anywhere on disk — they don't have to live inside the source folder. Author-only
    /// metadata: never written into the public index.json.
    /// </summary>
    public Dictionary<string, GameScriptSources> GameScriptSources { get; set; } = new();
    public DateTime LastOpenedAt { get; set; }

    /// <summary>
    /// SHA256 of the index.json bytes this machine last successfully published for this project.
    /// It exists to tell two very different situations apart when the local file and the live one
    /// disagree at open time: if the local file still matches what was last published, the LIVE
    /// copy moved on (published elsewhere) and is the one to keep; if it doesn't, the local file
    /// carries edits that were never published, and taking the live copy would destroy them.
    /// Null before this machine has ever published the project.
    /// </summary>
    public string? LastPublishedIndexSha256 { get; set; }
}

/// <summary>
/// Holds the absolute file paths the author picked for the three lifecycle script slots
/// of one game. Any field can be null when that slot is disabled or hasn't been browsed
/// to yet.
/// </summary>
public sealed class GameScriptSources
{
    public string? PreInstall { get; set; }
    public string? PostInstall { get; set; }
    public string? PostUninstall { get; set; }
}
