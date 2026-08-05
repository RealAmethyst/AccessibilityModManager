using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// A portable backup of one plugin's signing key, and the publishing state that goes with it.
///
/// Not a bare key file, because a bare key is not enough to publish from anywhere else. Publishing
/// only ever extends the catalog this machine last confirmed, so a machine with the key and no
/// history refuses to publish — deliberately, since "I am new here" and "the server is replaying an
/// old catalog at me" are the same picture from the inside. The history has to travel with the key,
/// and the backup is the one thing an author is told to keep safe, so it travels here.
/// </summary>
public sealed record ClaimKeyBackup
{
    [JsonPropertyName("v")]
    public int V { get; init; } = 1;

    [JsonPropertyName("what")]
    public string What { get; init; } =
        "Accessibility Mod Manager catalog signing key. The private key is encrypted with the " +
        "passphrase chosen when it was exported. Keep this file safe: it is what lets you publish.";

    [JsonPropertyName("pluginId")]
    public required string PluginId { get; init; }

    [JsonPropertyName("keyId")]
    public required string KeyId { get; init; }

    [JsonPropertyName("publicKeyFingerprint")]
    public required string PublicKeyFingerprint { get; init; }

    /// <summary>Encrypted PKCS#8, under the export passphrase — never this machine's DPAPI, which
    /// would make the backup openable only where it is least needed.</summary>
    [JsonPropertyName("privateKeyPem")]
    public required string PrivateKeyPem { get; init; }

    /// <summary>Every publishing record this key has, one per trust context it has published in.</summary>
    [JsonPropertyName("publisherState")]
    public IReadOnlyList<PublisherRecord> PublisherState { get; init; } = [];

    /// <summary>The registry this machine had acted on, exactly — version AND content hash, so a
    /// restored machine is no more accepting than the one it came from.</summary>
    [JsonPropertyName("registryHighWater")]
    public RegistryHighWater? RegistryHighWater { get; init; }

    /// <summary>
    /// A signature by the key inside this bundle, over every other field.
    ///
    /// Without it the encrypted key is the only authenticated thing here and everything around it
    /// is editable plaintext: the publishing history, which plugin slot the bundle lands in, the
    /// registry high-water. Rewriting the recorded head to an older generation is enough to make
    /// the restored machine sign a second version of a publish that already exists, which is the
    /// exact equivocation the whole journal exists to prevent — with the genuine key untouched, so
    /// every check that looks only at the key passes.
    ///
    /// It authenticates; it does not make the contents FRESH. Nothing in a file can, which is why a
    /// restore is treated as uncertain state until the author says otherwise.
    /// </summary>
    [JsonPropertyName("signature")]
    public string Signature { get; init; } = "";
}

/// <summary>
/// Creates, unlocks, backs up and rotates the keys this author signs catalog claims with — one per
/// plugin, never one for the author.
///
/// The key never leaves the machine except as a deliberate export. Two separate protections are at
/// work and it matters that they are not confused: the key FILE is encrypted with a passphrase
/// (portable — an export can be restored anywhere), while the stored passphrase is DPAPI-protected
/// (not portable — bound to this Windows user on this machine). That is why an export asks for its
/// own passphrase: a backup that only this machine could open would be no backup at all.
///
/// Every write here follows the same order — write a NEW file, prove it opens, switch the config to
/// it, and only then remove the old one. The first version wrote over the live key first and saved
/// the config afterwards, so a crash in between left a key file the config could no longer unlock,
/// and recovering that costs a registry re-sign.
/// </summary>
public sealed class ClaimSigningKeyStore(
    AuthorConfigService configService, PublisherHeadStore headStore, ILogger logger)
{
    /// <summary>Iteration count for the key file's passphrase derivation.</summary>
    private static readonly PbeParameters KeyEncryption =
        new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    /// <summary>Bounds a hostile or corrupt backup before it is parsed at all. A real one is a few
    /// kilobytes.</summary>
    private const long MaxBackupBytes = 4 * 1024 * 1024;

    /// <summary>One record per trust context a key has published in — a handful, ever.</summary>
    private const int MaxBackupRecords = 256;

    private static readonly JsonSerializerOptions BackupJson = new()
    {
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Keys live beside the config that points at them, so a test instance is fully
    /// self-contained and can never write into the author's real key directory.</summary>
    private string KeyDirectory => Path.Combine(configService.StorageDirectory, "keys");

    /// <summary>
    /// File names come from the key's own fingerprint — 64 hex characters, which is its own safe
    /// name — and never from a plugin id.
    ///
    /// A plugin id arrives from a project file or a registry entry and used to go straight into
    /// <c>Path.Combine</c>, where <c>..\\..\\something</c> resolves happily outside the key
    /// directory. The containment check below is belt and braces on top of a name that cannot
    /// express a path in the first place.
    /// </summary>
    /// <param name="revision">
    /// Bumped whenever the same key is rewritten under a different passphrase, so the new copy
    /// never lands on top of the working one. That is what makes the switch atomic from the
    /// config's point of view: at every instant the recorded path names a file the recorded
    /// passphrase opens, whichever side of the crash you are on.
    /// </param>
    private string KeyPathFor(string fingerprint, int revision)
    {
        if (fingerprint.Length != 64 || !fingerprint.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("A key fingerprint is 64 lowercase hex characters.", nameof(fingerprint));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));

        var directory = Path.GetFullPath(KeyDirectory);
        var path = Path.GetFullPath(Path.Combine(directory, $"{fingerprint}.{revision}.pem"));

        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The key path resolved outside the key directory.");

        return path;
    }

    /// <summary>The revision after the one a recorded path names, or 1 when it names none.</summary>
    private static int NextRevision(string? currentPath)
    {
        var parts = Path.GetFileName(currentPath ?? "").Split('.');
        return parts.Length >= 3 && int.TryParse(parts[^2], out var revision) ? revision + 1 : 1;
    }

    /// <summary>
    /// Generates a new signing key for one plugin and records it.
    ///
    /// Refuses to replace an existing one, and refuses even when its FILE is missing: the registry
    /// vouches for that key, so quietly generating another would leave every claim already published
    /// unverifiable until the registry was re-signed. A missing file is what the backup is for.
    /// </summary>
    public ClaimSigningConfig Create(string pluginId, ReadOnlySpan<char> passphrase, string? keyId = null)
    {
        PathSafety.EnsureSafeId(pluginId, "plugin id");
        if (passphrase.IsEmpty)
            throw new ArgumentException("A passphrase is required.", nameof(passphrase));

        var config = configService.Load();
        if (config.ClaimSigningKeys.ContainsKey(pluginId))
        {
            throw new InvalidOperationException(
                $"A signing key is already recorded for plugin '{pluginId}'. Replacing it would make " +
                "every claim already published unverifiable until the registry is re-signed with the " +
                "new key. If the key file is missing, import your backup instead; if you mean to " +
                "change keys, rotate deliberately.");
        }

        keyId ??= $"{pluginId}-{DateTime.UtcNow:yyyy-MM}";

        using var rsa = RSA.Create(ClaimKeyPolicy.KeySizeBits);
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        var fingerprint = ClaimTrustContext.PublicKeyFingerprint(publicPem);

        Directory.CreateDirectory(KeyDirectory);
        var path = KeyPathFor(fingerprint, revision: 1);
        WriteKeyFile(path, rsa.ExportEncryptedPkcs8PrivateKeyPem(passphrase, KeyEncryption));

        var signing = new ClaimSigningConfig
        {
            PluginId = pluginId,
            KeyId = keyId,
            PrivateKeyPath = path,
            Passphrase = passphrase.ToString(),
            PublicKeyPem = publicPem,
            PublicKeyFingerprint = fingerprint
        };

        config.ClaimSigningKeys[pluginId] = signing;
        configService.Save(config);

        logger.Information("Created claim signing key {KeyId} for plugin {PluginId} ({Fingerprint})",
            keyId, pluginId, fingerprint);

        return signing;
    }

    /// <summary>The recorded key for a plugin, or null when there is none yet.</summary>
    public ClaimSigningConfig? TryGet(string pluginId) =>
        configService.Load().ClaimSigningKeys.GetValueOrDefault(pluginId);

    /// <summary>
    /// Opens a signer for a plugin's key, bound to the given anchor.
    ///
    /// The anchor comes from the SIGNED REGISTRY, not from local config, so this also answers the
    /// question that actually matters before publishing: is the key on this disk still the one the
    /// registry vouches for? All three parts of that identity are checked, not whichever one is
    /// convenient — otherwise a key kept for one plugin could sign for another the registry names,
    /// and a key the registry has moved on from could go on signing under a retired identity.
    /// </summary>
    /// <summary>
    /// Whether this plugin's key came out of a backup rather than being created here. See
    /// <see cref="ClaimSigningConfig.ImportedFromBackup"/> for why it decides whether a fresh signed
    /// history may be started. A plugin with no key at all answers false — there is nothing to
    /// publish with, and that is a different refusal.
    /// </summary>
    public bool WasImported(string pluginId) => TryGet(pluginId)?.ImportedFromBackup == true;

    public ClaimSigner OpenSigner(ClaimTrustAnchor anchor)
    {
        var signing = TryGet(anchor.PluginId)
            ?? throw new InvalidOperationException(
                $"No signing key is set up for plugin '{anchor.PluginId}'. Create one, or import your " +
                "backup, before publishing signed catalog claims.");

        if (string.IsNullOrEmpty(signing.Passphrase))
        {
            throw new InvalidOperationException(
                "The signing key's passphrase could not be read on this machine. If this profile was " +
                "copied from another computer, import your key backup here.");
        }

        if (!File.Exists(signing.PrivateKeyPath))
        {
            throw new InvalidOperationException(
                $"The signing key file is missing ({signing.PrivateKeyPath}). Import your backup to restore it.");
        }

        if (!string.Equals(signing.KeyId, anchor.KeyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The signing key stored here is '{signing.KeyId}', but the registry names " +
                $"'{anchor.KeyId}' for this plugin. Claims signed with it would verify for nobody.");
        }

        if (!string.Equals(signing.PublicKeyFingerprint,
                ClaimTrustContext.PublicKeyFingerprint(anchor.PublicKeyPem), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The signing key stored here is not the one the registry currently publishes. Either the " +
                "registry was re-signed with a different key, or this machine's key is out of date.");
        }

        return new ClaimSigner(File.ReadAllText(signing.PrivateKeyPath), signing.Passphrase, anchor);
    }

    /// <summary>
    /// Writes a portable backup: the key, encrypted under a passphrase the author chooses, together
    /// with the publishing state that makes it usable somewhere else.
    ///
    /// Deliberately not DPAPI-protected: a backup only this Windows account could open would be
    /// useless in the one situation backups exist for. The trade is that the file is exactly as
    /// strong as the passphrase put on it, so it is written wherever the author says rather than
    /// into the tool's own directories.
    /// </summary>
    public void Export(string pluginId, string destinationPath, ReadOnlySpan<char> exportPassphrase)
    {
        if (exportPassphrase.IsEmpty)
            throw new ArgumentException("An export passphrase is required.", nameof(exportPassphrase));

        var signing = TryGet(pluginId)
            ?? throw new InvalidOperationException($"No signing key is set up for plugin '{pluginId}'.");

        var records = headStore.RecordsFor(pluginId);

        // A backup taken mid-publish would carry the pending marker but not the exact bytes it
        // refers to, and those bytes cannot be rebuilt — the signatures are randomised, so a
        // rebuild at the same generation is the fork this all exists to prevent. A restored machine
        // would be permanently stuck on a publish it cannot finish or abandon. Settle it first.
        if (records.Any(r => r.Pending is not null))
        {
            throw new InvalidOperationException(
                "There is a publish in progress that hasn't been confirmed yet. Finish or resolve it " +
                "before taking a backup — a backup taken now would remember the attempt without the " +
                "file it was going to send.");
        }

        using var rsa = LoadPrivateKey(signing);

        var backup = new ClaimKeyBackup
        {
            PluginId = signing.PluginId,
            KeyId = signing.KeyId,
            PublicKeyFingerprint = signing.PublicKeyFingerprint,
            PrivateKeyPem = rsa.ExportEncryptedPkcs8PrivateKeyPem(exportPassphrase, KeyEncryption),
            PublisherState = records,
            RegistryHighWater = headStore.CurrentRegistryHighWater()
        };

        backup = backup with
        {
            Signature = Convert.ToBase64String(rsa.SignData(
                BackupBytesToSign(backup), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(destinationPath, JsonSerializer.Serialize(backup, BackupJson));

        logger.Information("Exported claim signing key {KeyId} with {Records} publishing record(s)",
            signing.KeyId, backup.PublisherState.Count);
    }

    /// <summary>
    /// What the bundle's signature covers: everything except the signature itself, under a domain
    /// prefix of its own so a backup signature can never be presented as a catalog claim, or the
    /// reverse.
    /// </summary>
    private static byte[] BackupBytesToSign(ClaimKeyBackup backup)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(backup with { Signature = "" }, BackupJson);
        return [.. "amm-key-backup-v1\n"u8, .. body];
    }

    /// <summary>
    /// Installs a backup on this machine — the other half of <see cref="Export"/>, for a new
    /// computer or a restored disk. Brings the publishing state with it, which is what lets the new
    /// machine carry on publishing rather than refusing to.
    /// </summary>
    /// <param name="pluginId">
    /// Which plugin the caller means to restore. The bundle does NOT get to choose its own slot:
    /// it is a file that can be edited, and a bundle that named its destination could be pointed at
    /// another plugin's configuration and history.
    /// </param>
    /// <param name="expectedFingerprint">
    /// Whatever the signed registry currently publishes, when the caller has it. Importing a key
    /// the registry does not vouch for produces claims that verify nowhere, so it is refused here
    /// rather than discovered after a publish.
    /// </param>
    public ClaimSigningConfig Import(
        string sourcePath, ReadOnlySpan<char> sourcePassphrase, string pluginId,
        string? expectedFingerprint = null)
    {
        PathSafety.EnsureSafeId(pluginId, "plugin id");

        var length = new FileInfo(sourcePath).Length;
        if (length > MaxBackupBytes)
            throw new InvalidOperationException($"That backup file is larger than {MaxBackupBytes} bytes.");

        ClaimKeyBackup backup;
        try
        {
            backup = JsonSerializer.Deserialize<ClaimKeyBackup>(File.ReadAllText(sourcePath), BackupJson)
                ?? throw new InvalidOperationException("That backup file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "That file isn't a key backup this version understands.", ex);
        }

        if (backup.V != 1) throw new InvalidOperationException("That backup was written by a newer version.");
        if (backup.PublisherState.Count > MaxBackupRecords)
            throw new InvalidOperationException("That backup carries an implausible number of publishing records.");
        if (!string.Equals(backup.PluginId, pluginId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"That backup is for plugin '{backup.PluginId}', not '{pluginId}'.");
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromEncryptedPem(backup.PrivateKeyPem, sourcePassphrase);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "That key couldn't be opened — the passphrase is wrong, or the file is damaged.", ex);
        }

        // Before anything is written. The size rule held at creation, signing and verification but
        // not here, so an import could report success, replace a working key with an unsupported
        // one, and only fail at the next publish — with the original already gone.
        ClaimKeyPolicy.Require(rsa);

        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        var fingerprint = ClaimTrustContext.PublicKeyFingerprint(publicPem);

        if (!string.Equals(fingerprint, backup.PublicKeyFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That backup's key does not match the fingerprint recorded in it.");

        // Everything OUTSIDE the encrypted key is ordinary editable text: which plugin, which key
        // id, the publishing history, the registry high-water. Checking only the key leaves all of
        // it forgeable by anyone who can write to the file — and rewinding the recorded head by one
        // generation is enough to make this machine sign a second version of a publish that already
        // exists, with the genuine key untouched and every key-only check passing.
        if (!rsa.VerifyData(BackupBytesToSign(backup), Convert.FromBase64String(backup.Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            throw new InvalidOperationException(
                "That backup has been altered since it was written — the key inside it is genuine, but " +
                "the details around it no longer match what was signed. Publishing from it is refused.");
        }

        if (!string.IsNullOrEmpty(expectedFingerprint) &&
            !string.Equals(fingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "That key is not the one the registry currently vouches for. Claims signed with it " +
                "would not verify for anyone. Check you imported the right backup.");
        }

        // Rechecked HERE, immediately before anything is written, and not only in the caller. The
        // view model asks the same question, but it asks it before a file picker and a confirmation
        // dialog — a person-length interval during which another copy of the tool, or another caller
        // in this one, can journal a publish. Replacing the key or rolling the head back while an
        // attempt is outstanding leaves that attempt recorded against a key this machine no longer
        // holds, and nothing afterwards can work out what was sent.
        if (headStore.RecordsFor(pluginId).Any(r => r.Pending is not null))
        {
            throw new InvalidOperationException(
                "There is a publish that was started and never confirmed. Settle it first by " +
                "publishing again — that reads the server and either finishes it or explains why it " +
                "cannot. Restoring a key while an attempt is outstanding would strand it.");
        }

        var config = configService.Load();
        var previous = config.ClaimSigningKeys.GetValueOrDefault(pluginId);

        // One key belongs to one plugin. Two plugins sharing a fingerprint would share a file name
        // — so importing the same key under a second plugin would write over the first plugin's
        // copy under a different passphrase, leaving that plugin unable to open its own key — and it
        // would also undo the isolation that having a key per plugin is for.
        var owner = config.ClaimSigningKeys
            .FirstOrDefault(entry =>
                !string.Equals(entry.Key, pluginId, StringComparison.Ordinal) &&
                string.Equals(entry.Value.PublicKeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        if (owner.Key is not null)
        {
            throw new InvalidOperationException(
                $"That key is already the signing key for plugin '{owner.Key}'. Each plugin needs its " +
                "own key, so that one compromise cannot reach the others.");
        }

        // New file first, and prove it opens before anything starts depending on it. Writing over
        // the live key and saving the config afterwards is how a crash in between leaves a key file
        // the config can no longer unlock.
        Directory.CreateDirectory(KeyDirectory);
        var path = KeyPathFor(fingerprint, NextRevision(previous?.PrivateKeyPath));
        WriteKeyFile(path, rsa.ExportEncryptedPkcs8PrivateKeyPem(sourcePassphrase, KeyEncryption));
        using (var check = RSA.Create()) check.ImportFromEncryptedPem(File.ReadAllText(path), sourcePassphrase);

        var signing = new ClaimSigningConfig
        {
            PluginId = pluginId,
            KeyId = backup.KeyId,
            PrivateKeyPath = path,
            Passphrase = sourcePassphrase.ToString(),
            PublicKeyPem = publicPem,
            PublicKeyFingerprint = fingerprint,
            ImportedFromBackup = true
        };

        // Publishing state before the key is usable, not after. A crash between the two used to
        // leave a working key beside a restored, retired head with no freshness guard yet written —
        // the one arrangement that lets a replayed registry put them back into service together.
        headStore.RestoreFromBackup(pluginId, backup.PublisherState, backup.RegistryHighWater);

        config.ClaimSigningKeys[pluginId] = signing;
        configService.Save(config);

        if (previous is not null &&
            !string.Equals(previous.PrivateKeyPath, path, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(previous.PrivateKeyPath);
        }

        logger.Information("Imported claim signing key {KeyId} for plugin {PluginId} with {Records} record(s)",
            signing.KeyId, signing.PluginId, backup.PublisherState.Count);

        return signing;
    }

    /// <summary>
    /// Re-encrypts a key file under a new passphrase. The key itself — and therefore everything
    /// already published under it — is unchanged, so this needs no registry update.
    /// </summary>
    public void ChangePassphrase(
        string pluginId, ReadOnlySpan<char> currentPassphrase, ReadOnlySpan<char> newPassphrase)
    {
        if (newPassphrase.IsEmpty)
            throw new ArgumentException("A new passphrase is required.", nameof(newPassphrase));

        var config = configService.Load();
        var signing = config.ClaimSigningKeys.GetValueOrDefault(pluginId)
            ?? throw new InvalidOperationException($"No signing key is set up for plugin '{pluginId}'.");

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromEncryptedPem(File.ReadAllText(signing.PrivateKeyPath), currentPassphrase);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The current passphrase is wrong.", ex);
        }

        // Same order as an import, and for the same reason: a NEW file, proven openable, then the
        // config, then the old one goes. The new copy gets its own name rather than replacing the
        // working file, because the passphrase and the path have to change together — writing the
        // new key over the old path and saving the config afterwards leaves a window where the
        // recorded passphrase no longer opens the recorded file, and a crash inside that window
        // strands the only key the registry vouches for.
        var previousPath = signing.PrivateKeyPath;
        var path = KeyPathFor(signing.PublicKeyFingerprint, NextRevision(previousPath));
        WriteKeyFile(path, rsa.ExportEncryptedPkcs8PrivateKeyPem(newPassphrase, KeyEncryption));
        using (var check = RSA.Create()) check.ImportFromEncryptedPem(File.ReadAllText(path), newPassphrase);

        signing.PrivateKeyPath = path;
        signing.Passphrase = newPassphrase.ToString();
        configService.Save(config);

        TryDelete(previousPath);
        logger.Information("Changed the passphrase on claim signing key {KeyId}", signing.KeyId);
    }

    private static RSA LoadPrivateKey(ClaimSigningConfig signing)
    {
        if (string.IsNullOrEmpty(signing.Passphrase))
        {
            throw new InvalidOperationException(
                "The signing key's passphrase could not be read on this machine. Import your key backup here.");
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromEncryptedPem(File.ReadAllText(signing.PrivateKeyPath), signing.Passphrase);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes via a temp file and a replace, so an interrupted write cannot leave a truncated key
    /// where a whole one used to be. Losing a key file costs a registry re-sign to recover from.
    /// </summary>
    private static void WriteKeyFile(string path, string pem)
    {
        DurableFile.Write(path, pem);
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { logger.Warning(ex, "Couldn't remove the superseded key file {Path}", path); }
    }
}
