using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

public sealed class AuthorConfigService
{
    private static readonly string DefaultConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager-Author");

    /// <summary>
    /// Where this instance keeps its config. Overridable so tests can exercise the real save and
    /// load paths — including the passphrase protection — without touching the author's actual
    /// config, which holds live credentials and a signing identity.
    /// </summary>
    private readonly string ConfigDirectory;

    private string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;
    private AuthorConfig? _cached;

    public AuthorConfigService(ILogger logger, string? configDirectoryOverride = null)
    {
        _logger = logger;
        ConfigDirectory = configDirectoryOverride ?? DefaultConfigDirectory;
    }

    public static string GetReposDirectory() => Path.Combine(DefaultConfigDirectory, "repos");

    /// <summary>Directory this instance stores its files in — key files live alongside the config.</summary>
    public string StorageDirectory => ConfigDirectory;

    public AuthorConfig Load()
    {
        if (_cached != null) return _cached;

        try
        {
            if (!File.Exists(ConfigFile))
            {
                _cached = new AuthorConfig();
                return _cached;
            }

            var json = File.ReadAllText(ConfigFile);
            _cached = JsonSerializer.Deserialize<AuthorConfig>(json, JsonOptions) ?? new AuthorConfig();
            // Decrypt the SSH passphrase back to cleartext for in-memory use. The explicit flag
            // decides — never the value's shape (audit finding 38: a passphrase that itself
            // started with "dpapi:" was stored cleartext and destroyed on the next load).
            // Flag-less values are legacy: either the old prefix format or original cleartext.
            if (_cached.ServerUpload is { KeyPassphrase.Length: > 0 } su)
            {
                su.KeyPassphrase = su.KeyPassphraseProtected
                    ? UnprotectByFlag(su.KeyPassphrase)
                    : UnprotectLegacy(su.KeyPassphrase);
                su.KeyPassphraseProtected = false; // in memory the value is always cleartext
            }

            // The claim-signing passphrase has never existed unprotected, so there is no legacy
            // shape to tolerate here: a value without the flag was never written by this code and
            // is not treated as a passphrase.
            foreach (var signing in _cached.ClaimSigningKeys.Values)
            {
                if (signing is not { Passphrase.Length: > 0, PassphraseProtected: true }) continue;
                signing.Passphrase = UnprotectClaimSecret(signing.Passphrase);
                signing.PassphraseProtected = false;
            }

            return _cached;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load author config from {Path} — starting fresh", ConfigFile);
            _cached = new AuthorConfig();
            return _cached;
        }
    }

    public void Save(AuthorConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);

        // Encrypt the SSH passphrase at rest (DPAPI, current user) instead of storing cleartext.
        // Swap the ciphertext in only for serialization, then restore the cleartext so the rest of
        // the app keeps using the passphrase normally. Encryption is UNCONDITIONAL and recorded in
        // the explicit flag — the in-memory value is cleartext by convention, so any passphrase
        // content is legal, including one that happens to start with "dpapi:".
        var plainPassphrase = config.ServerUpload?.KeyPassphrase;
        var hasPassphrase = config.ServerUpload != null && !string.IsNullOrEmpty(plainPassphrase);
        if (hasPassphrase)
        {
            config.ServerUpload!.KeyPassphrase = ProtectSecret(plainPassphrase!);
            config.ServerUpload.KeyPassphraseProtected = true;
        }
        else if (config.ServerUpload != null)
        {
            config.ServerUpload.KeyPassphraseProtected = false;
        }

        // Same treatment for the claim-signing passphrase, under its own entropy so the two blobs
        // are not interchangeable: a copy of one must never decrypt in the other's slot.
        var plainClaimPassphrases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (pluginId, signing) in config.ClaimSigningKeys)
        {
            if (string.IsNullOrEmpty(signing.Passphrase))
            {
                signing.PassphraseProtected = false;
                continue;
            }

            plainClaimPassphrases[pluginId] = signing.Passphrase;
            signing.Passphrase = ProtectClaimSecret(signing.Passphrase);
            signing.PassphraseProtected = true;
        }

        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            // Atomic: a crash mid-write must not leave a truncated config that loses the recorded
            // key identity — recovering that costs a registry re-sign.
            var temp = ConfigFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, ConfigFile, overwrite: true);
        }
        finally
        {
            if (hasPassphrase)
            {
                config.ServerUpload!.KeyPassphrase = plainPassphrase!;
                config.ServerUpload.KeyPassphraseProtected = false;
            }
            foreach (var (pluginId, plain) in plainClaimPassphrases)
            {
                if (!config.ClaimSigningKeys.TryGetValue(pluginId, out var signing)) continue;
                signing.Passphrase = plain;
                signing.PassphraseProtected = false;
            }
        }
        _cached = config;
    }

    // DPAPI protection for the SSH key passphrase. The keyPassphraseProtected flag (not the
    // value's shape) says whether the stored string is ciphertext. Entropy pins the blob to this
    // specific use; DPAPI's per-user key is the secret.
    private const string LegacyDpapiPrefix = "dpapi:";
    private static readonly byte[] PassphraseEntropy = "AMM:Author:ServerUpload:v1"u8.ToArray();

    /// <summary>
    /// Distinct entropy for the claim-signing passphrase. Without it, a DPAPI blob from the SSH
    /// slot would decrypt in the signing slot and vice versa — the same Windows user key protects
    /// both, so only the entropy keeps them apart.
    /// </summary>
    private static readonly byte[] ClaimSigningEntropy = "AMM:Author:ClaimSigning:v1"u8.ToArray();

    private static string ProtectClaimSecret(string plain)
    {
        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), ClaimSigningEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Returns empty when the blob cannot be decrypted — which means the config came from another
    /// Windows account or another machine. Signing then fails loudly and the author imports their
    /// key backup, rather than the tool silently signing with something unexpected.
    /// </summary>
    private string UnprotectClaimSecret(string stored)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(stored), ClaimSigningEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "Couldn't decrypt the saved claim-signing passphrase (config from another user or machine?) — " +
                "the signing key will need to be imported again on this machine");
            return "";
        }
    }

    private static string ProtectSecret(string plain)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), PassphraseEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Decrypts a value the flag marks as protected (base64 DPAPI blob, no prefix).</summary>
    private string UnprotectByFlag(string stored)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(stored), PassphraseEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't decrypt the saved SSH passphrase (config copied from another user/machine?) — clearing it");
            return "";
        }
    }

    /// <summary>
    /// Handles values saved before the flag existed: the old "dpapi:"-prefixed ciphertext, or
    /// original pre-encryption cleartext (used as-is; the next Save writes flagged ciphertext).
    /// </summary>
    private string UnprotectLegacy(string stored)
    {
        if (!stored.StartsWith(LegacyDpapiPrefix, StringComparison.Ordinal))
            return stored;
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(stored[LegacyDpapiPrefix.Length..]), PassphraseEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        // A flagless value that LOOKS encrypted but won't decrypt is the very case finding 38
        // was about: a real passphrase that happened to start with "dpapi:", stored as-is by
        // the old prefix-sniffing code. Keep it — erasing it would destroy the one value the
        // fix exists to protect. The next Save rewrites it as flagged ciphertext.
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            _logger.Warning(
                "A saved SSH passphrase looks prefixed but doesn't decrypt — treating it as the literal passphrase it probably is");
            return stored;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't decrypt the saved SSH passphrase (config copied from another user/machine?) — clearing it");
            return "";
        }
    }

    public void RecordRecent(string projectPath, string? displayName = null, string? gitHubRepo = null)
    {
        var config = Load();
        var existing = config.RecentProjects.FirstOrDefault(p =>
            string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.LastOpenedAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(displayName)) existing.DisplayName = displayName;
            if (!string.IsNullOrEmpty(gitHubRepo)) existing.GitHubRepo = gitHubRepo;
        }
        else
        {
            config.RecentProjects.Add(new RecentProject
            {
                Path = projectPath,
                DisplayName = displayName,
                GitHubRepo = gitHubRepo,
                LastOpenedAt = DateTime.UtcNow
            });
        }

        config.LastOpenedProjectPath = projectPath;
        config.RecentProjects = config.RecentProjects
            .OrderByDescending(p => p.LastOpenedAt)
            .Take(20)
            .ToList();

        Save(config);
    }

    public void RemoveRecent(string projectPath)
    {
        var config = Load();
        config.RecentProjects.RemoveAll(p =>
            string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(config.LastOpenedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
            config.LastOpenedProjectPath = null;
        Save(config);
    }

    public RecentProject? GetRecent(string projectPath)
    {
        var config = Load();
        return config.RecentProjects.FirstOrDefault(p =>
            string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Records the fingerprint of the index.json this machine just published for a project.
    /// See <see cref="RecentProject.LastPublishedIndexSha256"/> for what it's used to decide.
    /// </summary>
    public void SetLastPublishedIndexSha(string projectPath, string sha256)
    {
        var config = Load();
        var project = config.RecentProjects.FirstOrDefault(p =>
            string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        if (project == null) return;

        project.LastPublishedIndexSha256 = sha256;
        Save(config);
    }

    public string? GetLastPublishedIndexSha(string projectPath) =>
        GetRecent(projectPath)?.LastPublishedIndexSha256;

    public void SetGameSourceRepo(string projectPath, string gameId, string sourceRepo)
    {
        var config = Load();
        var project = config.RecentProjects.FirstOrDefault(p =>
            string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        if (project == null) return;

        project.GameSourceRepos[gameId] = sourceRepo;
        Save(config);
    }

    public string? GetGameSourceRepo(string projectPath, string gameId)
    {
        var project = GetRecent(projectPath);
        if (project == null) return null;
        return project.GameSourceRepos.TryGetValue(gameId, out var repo) ? repo : null;
    }

    public ServerUploadConfig? GetServerUploadConfig() => Load().ServerUpload;

    public void SaveServerUploadConfig(ServerUploadConfig? config)
    {
        var c = Load();
        c.ServerUpload = config;
        Save(c);
    }

    public GameScriptSources? GetGameScriptSources(string projectPath, string gameId)
    {
        var project = GetRecent(projectPath);
        if (project == null) return null;
        return project.GameScriptSources.TryGetValue(gameId, out var sources) ? sources : null;
    }

    public void SetGameScriptSources(string projectPath, string gameId, GameScriptSources sources)
    {
        var config = Load();
        var project = config.RecentProjects.FirstOrDefault(p =>
            string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        if (project == null) return;

        // Strip the entry entirely when all three slots are empty so the config doesn't
        // accumulate noise for games that never touched the Scripts tab.
        if (string.IsNullOrEmpty(sources.PreInstall) &&
            string.IsNullOrEmpty(sources.PostInstall) &&
            string.IsNullOrEmpty(sources.PostUninstall))
        {
            if (!project.GameScriptSources.Remove(gameId)) return;
        }
        else
        {
            project.GameScriptSources[gameId] = sources;
        }

        Save(config);
    }
}
