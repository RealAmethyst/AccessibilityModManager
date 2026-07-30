using AccessibilityModManager.AuthorTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace AccessibilityModManager.AuthorTool.ViewModels;

/// <summary>
/// Creating, backing up and restoring the key this author signs one plugin's catalog with.
///
/// <para>This screen does not publish a catalog and does not change the registry. Creating a key is
/// private to this machine; signing starts only when a registry naming the key is published, which
/// is a separate act on a different screen. That separation is the point — a key made by accident
/// must never be able to change how publishing behaves.</para>
///
/// <para>Two things here are NOT reversible, and neither is described as though it were. Export
/// writes an encrypted backup wherever the author chooses, which may be a shared or synchronised
/// folder. And restoring replaces the key this machine holds, deleting the file it replaces — which
/// is why a restore that would bring in a DIFFERENT key is refused outright rather than
/// confirmed.</para>
///
/// <para>Passphrases arrive as <c>char[]</c> from the view's PasswordBox rather than as strings.
/// Each operation is split in two: an inner method that consumes the characters and decides, and an
/// outer one that wipes them and only then speaks. Doing the wipe in a <c>finally</c> around the
/// whole thing would not be enough — the dialogs come after, and a dialog waits for a person, so the
/// characters would sit in memory for as long as it takes them to read it.</para>
/// </summary>
public sealed partial class ClaimSigningViewModel(
    string pluginId,
    ClaimSigningKeyStore keys,
    PublisherHeadStore heads,
    ILogger logger,
    Action<string, string> showInfoDialog,
    Func<string, string, bool> confirmDialog,
    // (title, suggested file name, filter)
    Func<string, string, string, string?> browseToSave,
    // (title, filter, initial directory) — the same shape the rest of the tool uses.
    Func<string, string, string?, string?> browseToOpen,
    bool restoreOnly = false) : ObservableObject
{
    [ObservableProperty]
    private string? _statusMessage;

    public string PluginId => pluginId;

    /// <summary>
    /// True when the screen was opened because publishing found the registry naming a key this
    /// machine does not have. Creating a key is then exactly the wrong move — it would make an
    /// unrelated key that signs nothing anybody trusts — so it is not offered.
    /// </summary>
    public bool RestoreOnly => restoreOnly;

    public bool CanCreate => !restoreOnly && !HasKey;

    /// <summary>The default key id, which is also what the registry will name.</summary>
    public string SuggestedKeyId { get; } = $"{pluginId}-{DateTime.UtcNow:yyyy-MM}";

    private ClaimSigningConfig? Current => keys.TryGet(pluginId);

    public bool HasKey => Current is not null;

    public string KeySummary
    {
        get
        {
            if (Current is { } signing)
            {
                var restored = signing.ImportedFromBackup
                    ? " Restored from a backup, so the first publish will ask you to confirm where the catalog had got to."
                    : "";

                return $"Key '{signing.KeyId}' for '{pluginId}'. Fingerprint {signing.PublicKeyFingerprint}." + restored;
            }

            // Deliberately says nothing about whether the catalog is signed. It used to claim the
            // catalog "publishes unsigned, as it always has", which is precisely false in the state
            // this screen is most often opened in — where the registry DOES anchor a key and
            // publishing is refusing because this machine lacks it.
            return restoreOnly
                ? $"No signing key for '{pluginId}' on this machine, and the registry names one. " +
                  "Restore your backup to publish again."
                : $"No signing key for '{pluginId}' on this machine.";
        }
    }

    /// <summary>
    /// The public half, in the form the registry entry carries. This is the only part that ever
    /// leaves the machine as itself.
    /// </summary>
    public string? PublicKeyPem => Current?.PublicKeyPem;

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(KeySummary));
        OnPropertyChanged(nameof(PublicKeyPem));
    }

    /// <summary>
    /// Whether a publish is journalled but unsettled. Changing which key this machine holds while
    /// one is outstanding would leave that attempt recorded against a key that is no longer here,
    /// and nothing afterwards could work out what had been sent.
    /// </summary>
    private bool HasUnsettledPublish() => heads.RecordsFor(pluginId).Any(r => r.Pending is not null);

    private const string UnsettledPublish =
        "There is a publish that was started and never confirmed. Settle it first — choose Publish " +
        "index, which reads the server and either finishes it or explains why it cannot. Changing " +
        "the key while it is outstanding would leave that attempt recorded against a key this " +
        "machine no longer holds.";

    /// <summary>What an operation decided, so the caller can wipe secrets before saying it aloud.</summary>
    private readonly record struct Outcome(string? FailureTitle, string? FailureBody, string? Value)
    {
        public static Outcome Failed(string title, string body) => new(title, body, null);
        public static Outcome Did(string value) => new(null, null, value);
        public static Outcome Nothing => new(null, null, null);
    }

    // ---- creating ----

    public void CreateKey(string keyId, char[] passphrase, char[] confirmation)
    {
        Outcome outcome;
        try
        {
            outcome = DoCreate(keyId, passphrase, confirmation);
        }
        finally
        {
            Array.Clear(passphrase);
            Array.Clear(confirmation);
        }

        if (outcome.FailureTitle is not null)
        {
            showInfoDialog(outcome.FailureTitle, outcome.FailureBody!);
            return;
        }

        if (outcome.Value is null) return;

        Refresh();
        StatusMessage = $"Created key '{outcome.Value}'. Nothing has changed for anyone else yet.";
        logger.Information("Created claim signing key for {PluginId}", pluginId);

        showInfoDialog("Key created — back it up now",
            $"The key for '{pluginId}' exists on this machine and nowhere else.\n\n" +
            "Export a backup before going any further. If this machine is lost before you do, the " +
            "catalog can't be updated again until a new key is published in the registry.\n\n" +
            "Nothing about your published catalog has changed. Signing only starts when the registry " +
            "names this key.");
    }

    private Outcome DoCreate(string keyId, char[] passphrase, char[] confirmation)
    {
        if (restoreOnly)
        {
            return Outcome.Failed("This catalog already has a key",
                "The registry names a signing key for it. A new one made here would sign nothing " +
                "anyone trusts. Restore your backup instead.");
        }

        if (passphrase.Length == 0)
        {
            return Outcome.Failed("A passphrase is needed",
                "The key file is encrypted with it. Without one, anyone who reads this machine's " +
                "files can publish as you.");
        }

        if (!passphrase.AsSpan().SequenceEqual(confirmation))
        {
            return Outcome.Failed("The passphrases don't match",
                "Type the same passphrase twice. Nothing was created.");
        }

        try
        {
            return Outcome.Did(keys.Create(pluginId, passphrase,
                string.IsNullOrWhiteSpace(keyId) ? SuggestedKeyId : keyId.Trim()).KeyId);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Couldn't create a signing key for {PluginId}", pluginId);
            return Outcome.Failed("Couldn't create the key", ex.Message);
        }
    }

    // ---- backing up ----

    public void ExportBackup(char[] passphrase, char[] confirmation)
    {
        Outcome outcome;
        try
        {
            outcome = DoExport(passphrase, confirmation);
        }
        finally
        {
            Array.Clear(passphrase);
            Array.Clear(confirmation);
        }

        if (outcome.FailureTitle is not null)
        {
            showInfoDialog(outcome.FailureTitle, outcome.FailureBody!);
            return;
        }

        if (outcome.Value is null) return;

        StatusMessage = "Backup written.";
        logger.Information("Exported the claim signing backup for {PluginId}", pluginId);

        showInfoDialog("Backup written",
            $"Saved to:\n\n{outcome.Value}\n\n" +
            "Keep it somewhere that survives this machine, and keep the passphrase somewhere else. " +
            "Anyone with both can publish as you — so if that location is shared or synchronised, " +
            "treat it accordingly.\n\n" +
            "One thing worth knowing: this backup holds the key and how far your catalog has got. " +
            "After your first signed publish it is out of date, and the tool will ask you to export " +
            "it again. That second copy is the one that can actually recover you.");
    }

    private Outcome DoExport(char[] passphrase, char[] confirmation)
    {
        if (!HasKey)
            return Outcome.Failed("There is no key to back up", "Create the signing key first.");

        if (passphrase.Length == 0)
        {
            return Outcome.Failed("A passphrase is needed",
                "The backup is encrypted with it, and deliberately not tied to this computer — a " +
                "backup only this machine could open would be no backup at all. That also means the " +
                "passphrase is the only thing protecting it.");
        }

        if (!passphrase.AsSpan().SequenceEqual(confirmation))
        {
            return Outcome.Failed("The passphrases don't match",
                "Type the same passphrase twice. Nothing was written.");
        }

        // Enforced, not merely advised. Reusing the key's own passphrase couples two secrets that
        // are separate on purpose: the key passphrase never leaves this machine, while the backup
        // travels. Compared as a span against the stored value, so no new copy is made.
        if (Current is { Passphrase: { Length: > 0 } stored } &&
            passphrase.AsSpan().SequenceEqual(stored.AsSpan()))
        {
            return Outcome.Failed("Use a different passphrase for the backup",
                "This is the same one that protects the key on this machine. The backup travels and " +
                "the key does not, so one of them turning up somewhere it shouldn't must not hand " +
                "over the other.");
        }

        var destination = browseToSave(
            "Save the key backup",
            $"{pluginId}-signing-key-backup.json",
            "Key backup (*.json)|*.json|All files (*.*)|*.*");
        if (string.IsNullOrWhiteSpace(destination)) return Outcome.Nothing;

        try
        {
            keys.Export(pluginId, destination, passphrase);
            return Outcome.Did(destination);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Couldn't export the key backup for {PluginId}", pluginId);
            return Outcome.Failed("Couldn't write the backup", ex.Message);
        }
    }

    // ---- restoring ----

    public void ImportBackup(char[] passphrase)
    {
        Outcome outcome;
        try
        {
            outcome = DoImport(passphrase);
        }
        finally
        {
            Array.Clear(passphrase);
        }

        if (outcome.FailureTitle is not null)
        {
            showInfoDialog(outcome.FailureTitle, outcome.FailureBody!);
            return;
        }

        if (outcome.Value is null) return;

        Refresh();
        StatusMessage = "Key restored. The next publish will ask you to confirm where the catalog had got to.";
        logger.Information("Imported a claim signing backup for {PluginId}", pluginId);

        showInfoDialog("Key restored",
            "This machine can sign for the catalog again.\n\n" +
            "The next publish will ask you one question first: whether the publish this backup " +
            "remembers really is the most recent one. Nothing here can check that — only you know.");
    }

    private Outcome DoImport(char[] passphrase)
    {
        if (HasUnsettledPublish())
            return Outcome.Failed("Finish the publish that's in progress first", UnsettledPublish);

        if (passphrase.Length == 0)
        {
            return Outcome.Failed("A passphrase is needed",
                "The backup is encrypted with the passphrase it was exported under.");
        }

        var source = browseToOpen(
            "Open a key backup", "Key backup (*.json)|*.json|All files (*.*)|*.*", null);
        if (string.IsNullOrWhiteSpace(source)) return Outcome.Nothing;

        // Restoring REPLACES the key this machine holds and deletes the file it replaces. When there
        // is already a key, the only restore allowed is one of that SAME key — recovering a lost or
        // damaged file. A backup carrying a different key is refused rather than confirmed: it would
        // destroy the key the registry vouches for, and no question asked in a dialog makes that
        // recoverable. Changing which key signs a catalog is a registry operation, not a restore.
        var expected = Current?.PublicKeyFingerprint;

        if (!confirmDialog("Restore this key?",
                (expected is null
                    ? $"This makes the key in that file the one this machine signs '{pluginId}' with.\n\n"
                    : $"This restores the key already recorded here — '{Current!.KeyId}', fingerprint " +
                      $"{expected}. A backup holding any other key is refused, because restoring it " +
                      "would destroy this one.\n\n") +
                "The first publish afterwards will ask you to confirm that the publish the backup " +
                "names really is the latest one. That question exists because no file can prove it is " +
                "current — if you published again after taking this backup, continuing from it would " +
                "sign a second version of a publish that already exists.\n\n" +
                "Restore it?"))
        {
            StatusMessage = "Nothing was restored.";
            return Outcome.Nothing;
        }

        try
        {
            keys.Import(source, passphrase, pluginId, expected);
            return Outcome.Did(source);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Couldn't import a key backup for {PluginId}", pluginId);
            return Outcome.Failed("Couldn't restore that backup", ex.Message);
        }
    }
}
