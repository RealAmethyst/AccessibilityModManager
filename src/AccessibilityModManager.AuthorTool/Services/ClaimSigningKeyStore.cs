using System.IO;
using System.Security.Cryptography;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// Creates, unlocks, backs up and rotates the key this author signs catalog claims with.
///
/// The key never leaves the machine except as a deliberate export. Two separate protections are at
/// work and it matters that they are not confused: the key FILE is encrypted with a passphrase
/// (portable — an export can be restored anywhere), while the stored passphrase is DPAPI-protected
/// (not portable — bound to this Windows user on this machine). That is why an export asks for its
/// own passphrase: a backup that only this machine could open would be no backup at all.
/// </summary>
public sealed class ClaimSigningKeyStore(AuthorConfigService configService, ILogger logger)
{
    private const int KeySizeBits = 4096;

    /// <summary>Iteration count for the key file's passphrase derivation.</summary>
    private static readonly PbeParameters KeyEncryption =
        new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    /// <summary>Keys live beside the config that points at them, so a test instance is fully
    /// self-contained and can never write into the author's real key directory.</summary>
    private string KeyDirectory => Path.Combine(configService.StorageDirectory, "keys");

    /// <summary>
    /// Generates a new signing key for a plugin and records it in the author config.
    ///
    /// Refuses to replace an existing key: the current one is vouched for by the signed registry,
    /// and quietly generating another would leave every published claim unverifiable until the
    /// registry was re-signed. Rotation is a deliberate act with its own path.
    /// </summary>
    public ClaimSigningConfig Create(string pluginId, ReadOnlySpan<char> passphrase, string? keyId = null)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            throw new ArgumentException("A plugin id is required.", nameof(pluginId));
        if (passphrase.IsEmpty)
            throw new ArgumentException("A passphrase is required.", nameof(passphrase));

        var config = configService.Load();
        if (config.ClaimSigning is { PrivateKeyPath.Length: > 0 } existing && File.Exists(existing.PrivateKeyPath))
        {
            throw new InvalidOperationException(
                "A signing key already exists for this author. Replacing it would make every claim " +
                "already published unverifiable until the registry is re-signed with the new key. " +
                "Rotate deliberately instead.");
        }

        keyId ??= $"{pluginId}-{DateTime.UtcNow:yyyy-MM}";

        using var rsa = RSA.Create(KeySizeBits);
        var encryptedPem = rsa.ExportEncryptedPkcs8PrivateKeyPem(passphrase, KeyEncryption);
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();

        Directory.CreateDirectory(KeyDirectory);
        var path = Path.Combine(KeyDirectory, $"{pluginId}-claim-signing.pem");
        WriteKeyFile(path, encryptedPem);

        var signing = new ClaimSigningConfig
        {
            PluginId = pluginId,
            KeyId = keyId,
            PrivateKeyPath = path,
            Passphrase = passphrase.ToString(),
            PublicKeyPem = publicPem,
            PublicKeyFingerprint = ClaimTrustContext.PublicKeyFingerprint(publicPem)
        };

        config.ClaimSigning = signing;
        configService.Save(config);

        logger.Information("Created claim signing key {KeyId} for plugin {PluginId} ({Fingerprint})",
            keyId, pluginId, signing.PublicKeyFingerprint);

        return signing;
    }

    /// <summary>
    /// Opens a signer for the configured key, bound to the given anchor.
    ///
    /// The anchor comes from the SIGNED REGISTRY, not from local config, so this also answers the
    /// question that actually matters before publishing: does the key on this disk still match what
    /// the registry vouches for? A mismatch throws here rather than producing claims that verify
    /// nowhere.
    /// </summary>
    public ClaimSigner OpenSigner(ClaimTrustAnchor anchor)
    {
        var signing = configService.Load().ClaimSigning
            ?? throw new InvalidOperationException(
                "No signing key is set up yet. Create one before publishing signed catalog claims.");

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

        // The key on this disk must be the key the registry vouches for — all three of the things
        // that identify it, not just whichever one happens to be checked later. Without this, a key
        // kept for one plugin could sign for another the registry names, and a key the registry has
        // moved on from could go on signing under a retired identity.
        if (!string.Equals(signing.PluginId, anchor.PluginId, StringComparison.Ordinal) ||
            !string.Equals(signing.KeyId, anchor.KeyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The signing key stored here is '{signing.KeyId}' for plugin '{signing.PluginId}', but the " +
                $"registry names '{anchor.KeyId}' for '{anchor.PluginId}'. Claims signed with it would " +
                "verify for nobody.");
        }

        if (!string.Equals(signing.PublicKeyFingerprint,
                ClaimTrustContext.PublicKeyFingerprint(anchor.PublicKeyPem), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The signing key stored here is not the one the registry currently publishes. Either the " +
                "registry was re-signed with a different key, or this machine's key is out of date.");
        }

        var pem = File.ReadAllText(signing.PrivateKeyPath);
        return new ClaimSigner(pem, signing.Passphrase, anchor);
    }

    /// <summary>
    /// Writes a portable backup, encrypted under a passphrase the author chooses.
    ///
    /// Deliberately not DPAPI-protected: a backup only this Windows account could open would be
    /// useless in the one situation backups exist for. The trade is that the export file is exactly
    /// as strong as the passphrase put on it, so it is written outside the tool's own directories
    /// wherever the author says.
    /// </summary>
    public void Export(string destinationPath, ReadOnlySpan<char> exportPassphrase)
    {
        if (exportPassphrase.IsEmpty)
            throw new ArgumentException("An export passphrase is required.", nameof(exportPassphrase));

        var signing = configService.Load().ClaimSigning
            ?? throw new InvalidOperationException("No signing key is set up yet.");

        using var rsa = LoadPrivateKey(signing);
        var exported = rsa.ExportEncryptedPkcs8PrivateKeyPem(exportPassphrase, KeyEncryption);

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        WriteKeyFile(destinationPath, exported);

        logger.Information("Exported claim signing key {KeyId} to a backup", signing.KeyId);
    }

    /// <summary>
    /// Installs a key from a backup on this machine — the other half of <see cref="Export"/>, for a
    /// new computer or a restored disk.
    ///
    /// <paramref name="expectedFingerprint"/> should be whatever the signed registry currently
    /// publishes. Importing a key the registry does not vouch for produces claims that verify
    /// nowhere, so it is refused rather than discovered later.
    /// </summary>
    public ClaimSigningConfig Import(
        string sourcePath,
        ReadOnlySpan<char> sourcePassphrase,
        string pluginId,
        string keyId,
        string? expectedFingerprint = null)
    {
        var pem = File.ReadAllText(sourcePath);

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromEncryptedPem(pem, sourcePassphrase);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "That key couldn't be opened — the passphrase is wrong, or the file isn't an encrypted key.", ex);
        }

        // Before anything is written. The size rule held at creation, signing and verification but
        // not here, so an import could report success, replace a working key with an unsupported
        // one, and only fail at the next publish — with the original already overwritten.
        ClaimKeyPolicy.Require(rsa);

        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        var fingerprint = ClaimTrustContext.PublicKeyFingerprint(publicPem);

        if (!string.IsNullOrEmpty(expectedFingerprint) &&
            !string.Equals(fingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "That key is not the one the registry currently vouches for. Claims signed with it " +
                "would not verify for anyone. Check you imported the right backup.");
        }

        // Re-encrypt under the same passphrase into this machine's key directory, so the imported
        // file lives where the tool expects it rather than wherever the backup happened to be.
        Directory.CreateDirectory(KeyDirectory);
        var path = Path.Combine(KeyDirectory, $"{pluginId}-claim-signing.pem");
        WriteKeyFile(path, rsa.ExportEncryptedPkcs8PrivateKeyPem(sourcePassphrase, KeyEncryption));

        var config = configService.Load();
        config.ClaimSigning = new ClaimSigningConfig
        {
            PluginId = pluginId,
            KeyId = keyId,
            PrivateKeyPath = path,
            Passphrase = sourcePassphrase.ToString(),
            PublicKeyPem = publicPem,
            PublicKeyFingerprint = fingerprint
        };
        configService.Save(config);

        logger.Information("Imported claim signing key {KeyId} for plugin {PluginId} ({Fingerprint})",
            keyId, pluginId, fingerprint);

        return config.ClaimSigning;
    }

    /// <summary>
    /// Re-encrypts the key file under a new passphrase. The key itself — and therefore everything
    /// already published under it — is unchanged, so this needs no registry update.
    /// </summary>
    public void ChangePassphrase(ReadOnlySpan<char> currentPassphrase, ReadOnlySpan<char> newPassphrase)
    {
        if (newPassphrase.IsEmpty)
            throw new ArgumentException("A new passphrase is required.", nameof(newPassphrase));

        var config = configService.Load();
        var signing = config.ClaimSigning
            ?? throw new InvalidOperationException("No signing key is set up yet.");

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromEncryptedPem(File.ReadAllText(signing.PrivateKeyPath), currentPassphrase);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The current passphrase is wrong.", ex);
        }

        WriteKeyFile(signing.PrivateKeyPath, rsa.ExportEncryptedPkcs8PrivateKeyPem(newPassphrase, KeyEncryption));
        signing.Passphrase = newPassphrase.ToString();
        configService.Save(config);

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
    /// where a whole one used to be. Losing the key file costs a registry re-sign to recover from.
    /// </summary>
    private static void WriteKeyFile(string path, string pem)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, pem);
        File.Move(temp, path, overwrite: true);
    }
}
