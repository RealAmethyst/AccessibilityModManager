using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.AuthorTool.ViewModels;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// The catalog-signing screen, driven without a window.
///
/// <para>Two things here are worth more than the happy paths: that a mistyped passphrase cannot
/// create a key nobody can open, and that the characters are wiped after use. The second is the kind
/// of property that regresses silently — nothing fails, nothing looks different, and the passphrase
/// simply stays in memory.</para>
/// </summary>
public sealed class ClaimSigningViewModelTests : IDisposable
{
    private const string PluginId = "amethyst";

    private readonly string _root;
    private readonly ClaimSigningKeyStore _keys;
    private readonly List<string> _dialogs = [];

    private string? _saveTo;
    private string? _openFrom;
    private bool _confirmAnswer = true;

    public ClaimSigningViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "signui-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);

        var config = new AuthorConfigService(TestLogger.Create(), _root);
        _heads = new PublisherHeadStore(config, TestLogger.Create());
        _keys = new ClaimSigningKeyStore(config, _heads, TestLogger.Create());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private PublisherHeadStore _heads = null!;

    /// <summary>Default: the registry was read and names no key — the pre-signing state.</summary>
    private ClaimSigningViewModel Create(RegistryTrustState? trust = null) => new(
        PluginId, _keys, _heads, TestLogger.Create(),
        showInfoDialog: (title, _) => _dialogs.Add(title),
        confirmDialog: (_, _) => _confirmAnswer,
        browseToSave: (_, _, _) => _saveTo,
        browseToOpen: (_, _, _) => _openFrom,
        registryTrust: trust ?? RegistryTrustState.NoKeyAnchored());

    private static char[] Pass(string text) => text.ToCharArray();

    // ---- creating ----

    [Fact]
    public void A_key_is_created_and_recorded()
    {
        var vm = Create();
        Assert.False(vm.HasKey);

        vm.CreateKey("amethyst-2026-07", Pass("correct horse"), Pass("correct horse"));

        Assert.True(vm.HasKey);
        Assert.Equal("amethyst-2026-07", _keys.TryGet(PluginId)!.KeyId);
        Assert.NotNull(vm.PublicKeyPem);
    }

    [Fact]
    public void A_mistyped_passphrase_creates_nothing()
    {
        // The failure this prevents is unrecoverable in the worst way: a key encrypted under a
        // passphrase the author never meant to type, discovered only when it will not open.
        var vm = Create();

        vm.CreateKey("amethyst-2026-07", Pass("correct horse"), Pass("correct hoarse"));

        Assert.False(vm.HasKey);
        Assert.Null(_keys.TryGet(PluginId));
        Assert.Contains("The passphrases don't match", _dialogs);
    }

    [Fact]
    public void An_empty_passphrase_creates_nothing()
    {
        var vm = Create();

        vm.CreateKey("amethyst-2026-07", [], []);

        Assert.False(vm.HasKey);
        Assert.Contains("A passphrase is needed", _dialogs);
    }

    [Fact]
    public void An_empty_key_name_falls_back_to_the_suggested_one()
    {
        var vm = Create();

        vm.CreateKey("   ", Pass("pp"), Pass("pp"));

        Assert.Equal(vm.SuggestedKeyId, _keys.TryGet(PluginId)!.KeyId);
    }

    [Fact]
    public void A_second_key_is_refused_rather_than_replacing_the_first()
    {
        // Replacing it would make everything already published unverifiable until the registry was
        // re-signed. The store refuses; this checks the screen surfaces that rather than swallowing it.
        var vm = Create();
        vm.CreateKey("amethyst-2026-07", Pass("pp"), Pass("pp"));
        var original = _keys.TryGet(PluginId)!.PublicKeyFingerprint;
        _dialogs.Clear();

        vm.CreateKey("amethyst-2026-08", Pass("other"), Pass("other"));

        Assert.Equal(original, _keys.TryGet(PluginId)!.PublicKeyFingerprint);
        Assert.Contains("Couldn't create the key", _dialogs);
    }

    // ---- the property that regresses in silence ----

    [Fact]
    public void Passphrases_are_wiped_before_the_call_returns_including_the_paths_that_refuse()
    {
        var vm = Create();

        var made = Pass("correct horse");
        var confirmed = Pass("correct horse");
        vm.CreateKey("amethyst-2026-07", made, confirmed);
        Assert.All(made, c => Assert.Equal('\0', c));
        Assert.All(confirmed, c => Assert.Equal('\0', c));

        // Including the paths that refused: a rejected passphrase is exactly as worth wiping, and
        // it is the branch most likely to return early past a cleanup step. This checks the
        // buffers are clear by the time the call RETURNS — the wiping happens before any dialog is
        // shown, which is a stronger property than this can observe from out here.
        var mismatchedA = Pass("one");
        var mismatchedB = Pass("two");
        vm.ExportBackup(mismatchedA, mismatchedB);
        Assert.All(mismatchedA, c => Assert.Equal('\0', c));
        Assert.All(mismatchedB, c => Assert.Equal('\0', c));

        _openFrom = null; // the picker is cancelled, so import returns early
        var importing = Pass("three");
        vm.ImportBackup(importing);
        Assert.All(importing, c => Assert.Equal('\0', c));
    }

    // ---- backing up ----

    [Fact]
    public void There_is_nothing_to_back_up_before_a_key_exists()
    {
        var vm = Create();

        vm.ExportBackup(Pass("pp"), Pass("pp"));

        Assert.Contains("There is no key to back up", _dialogs);
    }

    [Fact]
    public void A_backup_is_written_where_the_author_chose()
    {
        var vm = Create();
        vm.CreateKey("amethyst-2026-07", Pass("pp"), Pass("pp"));
        _saveTo = Path.Combine(_root, "backup.json");
        _dialogs.Clear();

        vm.ExportBackup(Pass("backup pass"), Pass("backup pass"));

        Assert.True(File.Exists(_saveTo));
        Assert.Contains("Backup written", _dialogs);
    }

    [Fact]
    public void Cancelling_the_save_picker_writes_nothing()
    {
        var vm = Create();
        vm.CreateKey("amethyst-2026-07", Pass("pp"), Pass("pp"));
        _saveTo = null;
        _dialogs.Clear();

        vm.ExportBackup(Pass("backup pass"), Pass("backup pass"));

        Assert.Empty(_dialogs);
        Assert.DoesNotContain(
            Directory.GetFiles(_root, "*.json", SearchOption.TopDirectoryOnly),
            f => Path.GetFileName(f) == "backup.json");
    }

    [Fact]
    public void A_mistyped_backup_passphrase_writes_nothing()
    {
        var vm = Create();
        vm.CreateKey("amethyst-2026-07", Pass("pp"), Pass("pp"));
        _saveTo = Path.Combine(_root, "backup.json");
        _dialogs.Clear();

        vm.ExportBackup(Pass("one"), Pass("two"));

        Assert.False(File.Exists(_saveTo));
        Assert.Contains("The passphrases don't match", _dialogs);
    }

    // ---- restoring ----

    [Fact]
    public void Declining_the_confirmation_restores_nothing()
    {
        var vm = Create();
        vm.CreateKey("amethyst-2026-07", Pass("pp"), Pass("pp"));
        _saveTo = Path.Combine(_root, "backup.json");
        vm.ExportBackup(Pass("backup pass"), Pass("backup pass"));

        // A fresh machine: same backup, no key recorded.
        var elsewhere = new ClaimSigningViewModelTests();
        try
        {
            elsewhere._openFrom = _saveTo;
            elsewhere._confirmAnswer = false;
            var fresh = elsewhere.Create();

            fresh.ImportBackup(Pass("backup pass"));

            Assert.False(fresh.HasKey);
        }
        finally
        {
            elsewhere.Dispose();
        }
    }

    [Fact]
    public void A_restored_key_is_marked_so_the_next_publish_asks_where_it_left_off()
    {
        var vm = Create();
        vm.CreateKey("amethyst-2026-07", Pass("pp"), Pass("pp"));
        _saveTo = Path.Combine(_root, "backup.json");
        vm.ExportBackup(Pass("backup pass"), Pass("backup pass"));

        var elsewhere = new ClaimSigningViewModelTests();
        try
        {
            elsewhere._openFrom = _saveTo;
            var fresh = elsewhere.Create();

            fresh.ImportBackup(Pass("backup pass"));

            Assert.True(fresh.HasKey);

            // The doubt belongs to the key: a backup proves authenticity, never freshness, so the
            // first publish afterwards has to ask.
            Assert.True(elsewhere._keys.TryGet(PluginId)!.ImportedFromBackup);
            Assert.Contains("Key restored", elsewhere._dialogs);
        }
        finally
        {
            elsewhere.Dispose();
        }
    }

    [Fact]
    public void A_backup_holding_a_DIFFERENT_key_can_never_replace_the_one_here()
    {
        // The worst thing this screen could do. Restoring overwrites the recorded key AND deletes
        // the file it replaces, so a valid backup of some other key would destroy the key the
        // registry vouches for — permanently, if that key had no backup of its own. Changing which
        // key signs a catalog is a registry operation; a restore may only ever restore the same key.
        var other = new ClaimSigningViewModelTests();
        try
        {
            var elsewhere = other.Create();
            elsewhere.CreateKey("someone-elses-key", Pass("pp"), Pass("pp"));
            other._saveTo = Path.Combine(other._root, "other.json");
            elsewhere.ExportBackup(Pass("other backup"), Pass("other backup"));

            var vm = Create();
            vm.CreateKey("amethyst-2026-07", Pass("pp"), Pass("pp"));
            var mine = _keys.TryGet(PluginId)!;
            var myKeyFile = mine.PrivateKeyPath;
            _dialogs.Clear();

            _openFrom = other._saveTo;
            vm.ImportBackup(Pass("other backup"));

            // Same key, same file, still openable.
            Assert.Equal(mine.PublicKeyFingerprint, _keys.TryGet(PluginId)!.PublicKeyFingerprint);
            Assert.True(File.Exists(myKeyFile));
            Assert.Contains("Couldn't restore that backup", _dialogs);
        }
        finally
        {
            other.Dispose();
        }
    }

    [Fact]
    public void Restoring_the_same_key_is_still_allowed()
    {
        // The case the refusal above must not break: the key file is lost or damaged and the author
        // restores the very key already recorded here.
        var vm = Create();
        vm.CreateKey("amethyst-2026-07", Pass("pp"), Pass("pp"));
        var fingerprint = _keys.TryGet(PluginId)!.PublicKeyFingerprint;
        _saveTo = Path.Combine(_root, "backup.json");
        vm.ExportBackup(Pass("backup pass"), Pass("backup pass"));
        _dialogs.Clear();

        _openFrom = _saveTo;
        vm.ImportBackup(Pass("backup pass"));

        Assert.Equal(fingerprint, _keys.TryGet(PluginId)!.PublicKeyFingerprint);
        Assert.Contains("Key restored", _dialogs);
    }

    [Fact]
    public void Restoring_is_refused_while_a_publish_is_unsettled()
    {
        // Changing which key this machine holds while a journalled publish is outstanding would
        // leave that attempt recorded against a key that is no longer here, and nothing afterwards
        // could work out what had been sent.
        var vm = Create();
        vm.CreateKey("amethyst-2026-07", Pass("pp"), Pass("pp"));
        _saveTo = Path.Combine(_root, "backup.json");
        vm.ExportBackup(Pass("backup pass"), Pass("backup pass"));

        _heads.WritePending(new string('c', 64), PluginId, committed: null,
            new PendingPublish
            {
                Generation = 1,
                ManifestHash = new string('a', 64),
                BaseManifestHash = null,
                IndexSha256 = new string('b', 64)
            },
            "{}"u8.ToArray());
        _dialogs.Clear();
        _openFrom = _saveTo;

        vm.ImportBackup(Pass("backup pass"));

        Assert.Contains("Finish the publish that's in progress first", _dialogs);
    }

    [Fact]
    public void The_backup_may_not_reuse_the_key_passphrase()
    {
        // They are separate on purpose: the key passphrase never leaves this machine, the backup
        // travels. Reusing one for both means either turning up somewhere it shouldn't hands over
        // the other.
        var vm = Create();
        vm.CreateKey("amethyst-2026-07", Pass("shared secret"), Pass("shared secret"));
        _saveTo = Path.Combine(_root, "backup.json");
        _dialogs.Clear();

        vm.ExportBackup(Pass("shared secret"), Pass("shared secret"));

        Assert.False(File.Exists(_saveTo));
        Assert.Contains("Use a different passphrase for the backup", _dialogs);
    }

    // ---- the recovery mode ----

    [Fact]
    public void Recovery_mode_does_not_offer_to_create_a_key_or_claim_the_catalog_is_unsigned()
    {
        // Opened because the registry names a key this machine lacks. Creating one here would make
        // an unrelated key that signs nothing anyone trusts — and the old summary announced "your
        // catalog publishes unsigned, as it always has", which in this exact state is false.
        var vm = Create(RegistryTrustState.Anchored(new string('f', 64), "amethyst-2026-07"));

        Assert.False(vm.CanCreate);
        Assert.DoesNotContain("unsigned", vm.KeySummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restore that backup", vm.KeySummary, StringComparison.OrdinalIgnoreCase);

        vm.CreateKey("sneaky", Pass("pp"), Pass("pp"));

        Assert.False(vm.HasKey);
        Assert.Contains("This catalog already has a key", _dialogs);
    }

    // ---- what the REGISTRY says, on a machine that holds nothing ----
    //
    // The replacement-machine case, and the one where getting it wrong strands recovery through the
    // act meant to perform it. With an empty local store there is nothing to compare a backup
    // against, so the registry has to be the thing that decides.

    [Fact]
    public void On_an_empty_machine_a_backup_of_the_wrong_key_is_refused_against_the_registry()
    {
        // Make a key elsewhere and back it up — a plausible wrong file: an old key, a test key, a
        // different plugin's backup renamed.
        var other = new ClaimSigningViewModelTests();
        try
        {
            var elsewhere = other.Create();
            elsewhere.CreateKey("not-the-live-key", Pass("pp"), Pass("pp"));
            other._saveTo = Path.Combine(other._root, "wrong.json");
            elsewhere.ExportBackup(Pass("wrong backup"), Pass("wrong backup"));

            // This machine holds NOTHING, and the registry names some other key.
            var vm = Create(RegistryTrustState.Anchored(new string('a', 64), "amethyst-2026-07"));
            _openFrom = other._saveTo;

            vm.ImportBackup(Pass("wrong backup"));

            // Refused. Accepting it would announce "this machine can sign again", make the wrong key
            // the one later restores are checked against, and leave the RIGHT backup refused.
            Assert.False(vm.HasKey);
            Assert.Contains("Couldn't restore that backup", _dialogs);
        }
        finally
        {
            other.Dispose();
        }
    }

    [Fact]
    public void On_an_empty_machine_the_backup_the_registry_names_is_accepted()
    {
        // The other half: the refusal above must not block the recovery it exists to protect.
        var origin = new ClaimSigningViewModelTests();
        try
        {
            var source = origin.Create();
            source.CreateKey("amethyst-2026-07", Pass("pp"), Pass("pp"));
            var fingerprint = origin._keys.TryGet(PluginId)!.PublicKeyFingerprint;
            origin._saveTo = Path.Combine(origin._root, "right.json");
            source.ExportBackup(Pass("right backup"), Pass("right backup"));

            var vm = Create(RegistryTrustState.Anchored(fingerprint, "amethyst-2026-07"));
            _openFrom = origin._saveTo;

            vm.ImportBackup(Pass("right backup"));

            Assert.True(vm.HasKey);
            Assert.Equal(fingerprint, _keys.TryGet(PluginId)!.PublicKeyFingerprint);
        }
        finally
        {
            origin.Dispose();
        }
    }

    [Fact]
    public void An_anchored_catalog_never_offers_to_create_a_key()
    {
        // Reachable from the toolbar, not just from a failed publish: on a replacement machine the
        // author might sensibly open this screen before ever pressing Publish. Creating here would
        // make a key that signs nothing anyone trusts AND block restoring the real one.
        var vm = Create(RegistryTrustState.Anchored(new string('a', 64), "amethyst-2026-07"));

        Assert.False(vm.CanCreate);
        Assert.True(vm.RestoreOnly);

        vm.CreateKey("sneaky", Pass("pp"), Pass("pp"));

        Assert.False(vm.HasKey);
        Assert.Contains("This catalog already has a key", _dialogs);
    }

    [Fact]
    public void An_unreadable_registry_is_not_permission_to_create()
    {
        // "Could not tell" is not "no key". Treating it as permission is how a key gets made for a
        // catalog that already has one, precisely when the network is the thing that is broken.
        var vm = Create(RegistryTrustState.Unreadable("no route to host"));

        Assert.False(vm.CanCreate);

        vm.CreateKey("amethyst-2026-07", Pass("pp"), Pass("pp"));

        Assert.False(vm.HasKey);
        Assert.Contains("The registry couldn't be read", _dialogs);
    }

    [Fact]
    public void The_summary_names_the_key_the_registry_wants_when_this_machine_has_none()
    {
        var vm = Create(RegistryTrustState.Anchored(new string('a', 64), "amethyst-2026-07"));

        Assert.Contains("amethyst-2026-07", vm.KeySummary, StringComparison.Ordinal);
        Assert.DoesNotContain("unsigned", vm.KeySummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Without_a_key_the_summary_says_nothing_about_whether_the_catalog_is_signed()
    {
        // It cannot know. The registry decides, and this screen never reads it.
        Assert.DoesNotContain("unsigned", Create().KeySummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_wrong_backup_passphrase_restores_nothing()
    {
        var vm = Create();
        vm.CreateKey("amethyst-2026-07", Pass("pp"), Pass("pp"));
        _saveTo = Path.Combine(_root, "backup.json");
        vm.ExportBackup(Pass("backup pass"), Pass("backup pass"));

        var elsewhere = new ClaimSigningViewModelTests();
        try
        {
            elsewhere._openFrom = _saveTo;
            var fresh = elsewhere.Create();

            fresh.ImportBackup(Pass("not the backup pass"));

            Assert.False(fresh.HasKey);
            Assert.Contains("Couldn't restore that backup", elsewhere._dialogs);
        }
        finally
        {
            elsewhere.Dispose();
        }
    }
}
