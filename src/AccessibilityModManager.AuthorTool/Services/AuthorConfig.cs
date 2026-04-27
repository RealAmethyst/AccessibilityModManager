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

    /// <summary>SSH username on the VPS that owns the releases folder.</summary>
    public string User { get; set; } = "";

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
