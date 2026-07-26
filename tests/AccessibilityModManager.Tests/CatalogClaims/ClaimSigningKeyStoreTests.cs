using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// The signing key's lifecycle: create, sign, back up, restore, rotate the passphrase.
///
/// The recovery cases matter most. The stored passphrase is DPAPI-protected, which ties it to one
/// Windows account on one machine — so without a working export and import, a dead disk would mean
/// issuing a new key and re-signing the registry to recover.
///
/// Everything runs against a temporary config directory; nothing here can reach the author's real
/// config or key.
/// </summary>
public sealed class ClaimSigningKeyStoreTests : IDisposable
{
    private readonly string _root;
    private readonly ClaimSigningKeyStore _store;
    private readonly AuthorConfigService _configService;
    private const string Passphrase = "correct horse battery staple";

    public ClaimSigningKeyStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "claimkey-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
        _configService = new AuthorConfigService(TestLogger.Create(), _root);
        _store = new ClaimSigningKeyStore(_configService, TestLogger.Create());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private static ClaimTrustAnchor AnchorFor(ClaimSigningConfig signing, string? url = null) => new()
    {
        PluginId = signing.PluginId,
        RepoIndexUrl = url ?? "https://accessibilitymods.com/registry/plugins/amethyst/index.json",
        Scheme = ClaimTrustAnchor.SchemeV1,
        KeyId = signing.KeyId,
        Algorithm = ClaimTrustAnchor.AlgorithmRsaPssSha256,
        PublicKeyPem = signing.PublicKeyPem
    };

    [Fact]
    public void Creating_a_key_records_it_and_can_sign_with_it()
    {
        var signing = _store.Create("amethyst", Passphrase);

        Assert.True(File.Exists(signing.PrivateKeyPath));
        Assert.NotEmpty(signing.PublicKeyFingerprint);
        Assert.Equal(signing.PublicKeyFingerprint, ClaimTrustContext.PublicKeyFingerprint(signing.PublicKeyPem));

        using var signer = _store.OpenSigner(AnchorFor(signing));
        var claim = signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header },
            1, ClaimAudience.Everyone, "{}");

        new ClaimVerifier(AnchorFor(signing)).Verify(claim.PayloadBytes, claim.Signature);
    }

    [Fact]
    public void The_private_key_never_appears_in_the_config_file()
    {
        _store.Create("amethyst", Passphrase);

        var configText = File.ReadAllText(Path.Combine(_root, "config.json"));

        Assert.DoesNotContain("PRIVATE KEY", configText, StringComparison.Ordinal);
        // And the passphrase is encrypted at rest, not sitting there in the clear.
        Assert.DoesNotContain(Passphrase, configText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_passphrase_survives_a_reload()
    {
        var signing = _store.Create("amethyst", Passphrase);

        // A fresh service instance re-reads from disk and must decrypt the passphrase, or signing
        // would start prompting mid-publish — the thing this store exists to avoid.
        var reloaded = new AuthorConfigService(TestLogger.Create(), _root);
        var store = new ClaimSigningKeyStore(reloaded, TestLogger.Create());

        using var signer = store.OpenSigner(AnchorFor(signing));
        Assert.NotNull(signer);
    }

    [Fact]
    public void Creating_a_second_key_is_refused()
    {
        _store.Create("amethyst", Passphrase);

        var ex = Assert.Throws<InvalidOperationException>(() => _store.Create("amethyst", "another"));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void Signing_is_refused_when_the_registry_vouches_for_a_different_key()
    {
        // The anchor comes from the signed registry. If the key on this disk is not the one the
        // registry names, every claim signed with it would verify nowhere — so it fails here.
        var signing = _store.Create("amethyst", Passphrase);

        var otherKey = ClaimTestKeys.Secondary;
        var wrongAnchor = AnchorFor(signing) with { PublicKeyPem = otherKey.ExportSubjectPublicKeyInfoPem() };

        // Refused on the stored fingerprint, before the key file is even opened — the mismatch is
        // knowable from config alone, and the author gets told which of the two is out of date
        // rather than a cryptographic complaint.
        var ex = Assert.Throws<InvalidOperationException>(() => _store.OpenSigner(wrongAnchor));
        Assert.Contains("not the one the registry currently publishes", ex.Message);
    }

    // ---- the recovery path ----

    [Fact]
    public void A_backup_can_be_restored_on_another_machine()
    {
        var original = _store.Create("amethyst", Passphrase);
        var backup = Path.Combine(_root, "backup", "amethyst-key.pem");
        _store.Export(backup, "backup passphrase");

        // A completely separate profile, as a new PC would be.
        var otherRoot = Path.Combine(_root, "other-machine");
        var otherConfig = new AuthorConfigService(TestLogger.Create(), otherRoot);
        var otherStore = new ClaimSigningKeyStore(otherConfig, TestLogger.Create());

        var restored = otherStore.Import(backup, "backup passphrase", "amethyst", original.KeyId,
            expectedFingerprint: original.PublicKeyFingerprint);

        Assert.Equal(original.PublicKeyFingerprint, restored.PublicKeyFingerprint);

        // And it really is the same signing identity: claims made there verify against the
        // registry entry published for the original.
        using var signer = otherStore.OpenSigner(AnchorFor(restored));
        var claim = signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header },
            1, ClaimAudience.Everyone, "{}");
        new ClaimVerifier(AnchorFor(original)).Verify(claim.PayloadBytes, claim.Signature);
    }

    [Fact]
    public void An_export_uses_its_own_passphrase_not_the_local_one()
    {
        // A backup readable only by this Windows account would be useless in the situation backups
        // exist for, so the export is protected by a passphrase the author chooses.
        var signing = _store.Create("amethyst", Passphrase);
        var backup = Path.Combine(_root, "backup.pem");
        _store.Export(backup, "different passphrase");

        var otherStore = new ClaimSigningKeyStore(new AuthorConfigService(TestLogger.Create(),
            Path.Combine(_root, "elsewhere")), TestLogger.Create());

        Assert.Throws<InvalidOperationException>(() =>
            otherStore.Import(backup, Passphrase, "amethyst", signing.KeyId));

        var ok = otherStore.Import(backup, "different passphrase", "amethyst", signing.KeyId);
        Assert.Equal(signing.PublicKeyFingerprint, ok.PublicKeyFingerprint);
    }

    [Fact]
    public void Importing_the_wrong_key_is_refused_before_it_can_do_damage()
    {
        // Restoring the wrong backup would produce claims nothing could verify, and the author
        // would only find out after publishing.
        _store.Create("amethyst", Passphrase);

        var strangerRoot = Path.Combine(_root, "stranger");
        var strangerStore = new ClaimSigningKeyStore(new AuthorConfigService(TestLogger.Create(), strangerRoot), TestLogger.Create());
        var strangerKey = strangerStore.Create("amethyst", "theirs");
        var strangerBackup = Path.Combine(strangerRoot, "theirs.pem");
        strangerStore.Export(strangerBackup, "theirs");

        var target = new ClaimSigningKeyStore(new AuthorConfigService(TestLogger.Create(),
            Path.Combine(_root, "target")), TestLogger.Create());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            target.Import(strangerBackup, "theirs", "amethyst", strangerKey.KeyId,
                expectedFingerprint: "0000000000000000000000000000000000000000000000000000000000000000"));
        Assert.Contains("not the one the registry", ex.Message);
    }

    [Fact]
    public void Importing_a_key_of_the_wrong_size_is_refused_before_anything_is_written()
    {
        // The size rule held at creation, signing and verification but not here — so an import
        // could report success, replace a working key with an unsupported one, and only fail at
        // the next publish, with the original already gone.
        var original = _store.Create("amethyst", Passphrase);

        using var weak = System.Security.Cryptography.RSA.Create(3072);
        var weakBackup = Path.Combine(_root, "weak.pem");
        File.WriteAllText(weakBackup, weak.ExportEncryptedPkcs8PrivateKeyPem("theirs",
            new System.Security.Cryptography.PbeParameters(
                System.Security.Cryptography.PbeEncryptionAlgorithm.Aes256Cbc,
                System.Security.Cryptography.HashAlgorithmName.SHA256, 100_000)));

        Assert.Throws<ClaimFormatException>(() =>
            _store.Import(weakBackup, "theirs", "amethyst", original.KeyId));

        // And the key that was already there is untouched.
        Assert.Equal(original.PublicKeyFingerprint,
            new AuthorConfigService(TestLogger.Create(), _root).Load().ClaimSigning!.PublicKeyFingerprint);
    }

    [Fact]
    public void A_wrong_backup_passphrase_reports_plainly()
    {
        var signing = _store.Create("amethyst", Passphrase);
        var backup = Path.Combine(_root, "backup.pem");
        _store.Export(backup, "right");

        var other = new ClaimSigningKeyStore(new AuthorConfigService(TestLogger.Create(),
            Path.Combine(_root, "other")), TestLogger.Create());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            other.Import(backup, "wrong", "amethyst", signing.KeyId));
        Assert.Contains("passphrase is wrong", ex.Message);
    }

    // ---- passphrase change ----

    [Fact]
    public void Changing_the_passphrase_keeps_the_same_signing_identity()
    {
        var signing = _store.Create("amethyst", Passphrase);
        var before = signing.PublicKeyFingerprint;

        _store.ChangePassphrase(Passphrase, "a new one");

        // Same key, so nothing already published stops verifying and no registry update is needed.
        using var signer = _store.OpenSigner(AnchorFor(signing));
        Assert.Equal(before, _configService.Load().ClaimSigning!.PublicKeyFingerprint);

        var claim = signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header },
            1, ClaimAudience.Everyone, "{}");
        new ClaimVerifier(AnchorFor(signing)).Verify(claim.PayloadBytes, claim.Signature);
    }

    [Fact]
    public void Changing_the_passphrase_with_the_wrong_current_one_is_refused()
    {
        _store.Create("amethyst", Passphrase);

        var ex = Assert.Throws<InvalidOperationException>(() => _store.ChangePassphrase("not it", "new"));
        Assert.Contains("current passphrase is wrong", ex.Message);
    }

    [Fact]
    public void A_missing_key_file_is_reported_as_a_recoverable_situation()
    {
        var signing = _store.Create("amethyst", Passphrase);
        File.Delete(signing.PrivateKeyPath);

        var ex = Assert.Throws<InvalidOperationException>(() => _store.OpenSigner(AnchorFor(signing)));
        Assert.Contains("Import your backup", ex.Message);
    }
}
