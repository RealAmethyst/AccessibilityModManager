using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
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
        _store = NewStore(_configService);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private static ClaimSigningKeyStore NewStore(AuthorConfigService config) =>
        new(config, new PublisherHeadStore(config, TestLogger.Create()), TestLogger.Create());

    private static ClaimSigningKeyStore NewStore(string root) =>
        NewStore(new AuthorConfigService(TestLogger.Create(), root));

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
        var store = NewStore(_root);

        using var signer = store.OpenSigner(AnchorFor(signing));
        Assert.NotNull(signer);
    }

    [Fact]
    public void Creating_a_second_key_for_one_plugin_is_refused()
    {
        _store.Create("amethyst", Passphrase);

        var ex = Assert.Throws<InvalidOperationException>(() => _store.Create("amethyst", "another"));
        Assert.Contains("already recorded", ex.Message);
    }

    [Fact]
    public void A_second_plugin_gets_its_own_key()
    {
        // One key per author was the first shape and it was wrong twice over: a single compromise
        // would reach every plugin the author publishes, and a second plugin could not be published
        // at all, because creating its key was refused on the grounds that a key already existed.
        var first = _store.Create("amethyst", Passphrase);
        var second = _store.Create("someone-else", "their passphrase");

        Assert.NotEqual(first.PublicKeyFingerprint, second.PublicKeyFingerprint);
        Assert.NotEqual(first.PrivateKeyPath, second.PrivateKeyPath);

        using var signer = _store.OpenSigner(AnchorFor(second));
        Assert.NotNull(signer);
    }

    [Fact]
    public void A_key_for_one_plugin_cannot_sign_for_another()
    {
        var first = _store.Create("amethyst", Passphrase);
        _store.Create("someone-else", "their passphrase");

        // The registry names the other plugin's id but this plugin's key material.
        var crossed = AnchorFor(first) with { PluginId = "someone-else" };

        // Refused on the key id, which is the first of the three bindings to disagree — the point
        // being that the lookup is per plugin, so one plugin's key is never even reachable for
        // another's claims.
        var ex = Assert.Throws<InvalidOperationException>(() => _store.OpenSigner(crossed));
        Assert.Contains("the registry names", ex.Message);
    }

    [Fact]
    public void A_missing_key_file_does_not_license_creating_a_replacement()
    {
        // The registry vouches for that key. Quietly making another would leave every claim already
        // published unverifiable until the registry was re-signed — which is what the backup is for.
        var signing = _store.Create("amethyst", Passphrase);
        File.Delete(signing.PrivateKeyPath);

        var ex = Assert.Throws<InvalidOperationException>(() => _store.Create("amethyst", "another"));
        Assert.Contains("import your backup", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\escape")]
    [InlineData("with/slash")]
    [InlineData("")]
    public void A_plugin_id_that_could_escape_the_key_directory_is_refused(string pluginId)
    {
        // The id arrives from a project file or a registry entry, and used to go straight into
        // Path.Combine. Key file names now come from the key's own fingerprint, so an id cannot
        // express a path at all; this is the check in front of that.
        Assert.ThrowsAny<Exception>(() => _store.Create(pluginId, Passphrase));
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
        _store.Export("amethyst", backup, "backup passphrase");

        // A completely separate profile, as a new PC would be.
        var otherRoot = Path.Combine(_root, "other-machine");
        var otherStore = NewStore(otherRoot);

        var restored = otherStore.Import(backup, "backup passphrase", "amethyst", expectedFingerprint: original.PublicKeyFingerprint);

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
        _store.Export("amethyst", backup, "different passphrase");

        var otherStore = NewStore(Path.Combine(_root, "elsewhere"));

        Assert.Throws<InvalidOperationException>(() =>
            otherStore.Import(backup, Passphrase, "amethyst"));

        var ok = otherStore.Import(backup, "different passphrase", "amethyst");
        Assert.Equal(signing.PublicKeyFingerprint, ok.PublicKeyFingerprint);
    }

    [Fact]
    public void Importing_the_wrong_key_is_refused_before_it_can_do_damage()
    {
        // Restoring the wrong backup would produce claims nothing could verify, and the author
        // would only find out after publishing.
        _store.Create("amethyst", Passphrase);

        var strangerRoot = Path.Combine(_root, "stranger");
        var strangerStore = NewStore(strangerRoot);
        var strangerKey = strangerStore.Create("amethyst", "theirs");
        var strangerBackup = Path.Combine(strangerRoot, "theirs.pem");
        strangerStore.Export("amethyst", strangerBackup, "theirs");

        var target = NewStore(Path.Combine(_root, "target"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            target.Import(strangerBackup, "theirs", "amethyst",
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
        var weakBackup = Path.Combine(_root, "weak.json");
        File.WriteAllText(weakBackup, System.Text.Json.JsonSerializer.Serialize(new ClaimKeyBackup
        {
            PluginId = "amethyst",
            KeyId = original.KeyId,
            PublicKeyFingerprint = ClaimTrustContext.PublicKeyFingerprint(weak.ExportSubjectPublicKeyInfoPem()),
            PrivateKeyPem = weak.ExportEncryptedPkcs8PrivateKeyPem("theirs",
                new System.Security.Cryptography.PbeParameters(
                    System.Security.Cryptography.PbeEncryptionAlgorithm.Aes256Cbc,
                    System.Security.Cryptography.HashAlgorithmName.SHA256, 100_000))
        }));

        Assert.Throws<ClaimFormatException>(() =>
            _store.Import(weakBackup, "theirs", "amethyst"));

        // And the key that was already there is untouched.
        Assert.Equal(original.PublicKeyFingerprint,
            new AuthorConfigService(TestLogger.Create(), _root).Load().ClaimSigningKeys["amethyst"].PublicKeyFingerprint);
    }

    [Fact]
    public void A_wrong_backup_passphrase_reports_plainly()
    {
        var signing = _store.Create("amethyst", Passphrase);
        var backup = Path.Combine(_root, "backup.pem");
        _store.Export("amethyst", backup, "right");

        var other = NewStore(Path.Combine(_root, "other"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            other.Import(backup, "wrong", "amethyst"));
        Assert.Contains("passphrase is wrong", ex.Message);
    }

    // ---- passphrase change ----

    [Fact]
    public void Changing_the_passphrase_keeps_the_same_signing_identity()
    {
        var signing = _store.Create("amethyst", Passphrase);
        var before = signing.PublicKeyFingerprint;

        _store.ChangePassphrase("amethyst", Passphrase, "a new one");

        // Same key, so nothing already published stops verifying and no registry update is needed.
        using var signer = _store.OpenSigner(AnchorFor(signing));
        Assert.Equal(before, _configService.Load().ClaimSigningKeys["amethyst"].PublicKeyFingerprint);

        var claim = signer.Sign(ClaimKind.Header, new ClaimIdentity { Kind = ClaimKind.Header },
            1, ClaimAudience.Everyone, "{}");
        new ClaimVerifier(AnchorFor(signing)).Verify(claim.PayloadBytes, claim.Signature);
    }

    [Fact]
    public void Changing_the_passphrase_with_the_wrong_current_one_is_refused()
    {
        _store.Create("amethyst", Passphrase);

        var ex = Assert.Throws<InvalidOperationException>(() => _store.ChangePassphrase("amethyst", "not it", "new"));
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
