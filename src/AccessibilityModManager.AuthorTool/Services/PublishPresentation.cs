namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// The four things applying a publish outcome can do, handed in so that applying one is testable
/// without a window.
/// </summary>
/// <param name="RecordPublishedSource">
/// Remembers this folder's bytes as published. The guarded one — calling it when the catalog does
/// not describe the folder is what lets a later project-open replace unpublished work.
/// </param>
/// <param name="ShowDialog">Title then message, both read aloud.</param>
/// <param name="CommitHistoryAsync">The best-effort local git commit.</param>
/// <param name="SetStatus">The status line, which is a screen-reader live region.</param>
/// <param name="OfferSigningSetup">
/// Asks whether to open the catalog-signing screen, and opens it on yes. Only ever reached when the
/// registry anchors a key this machine does not have.
/// </param>
/// <param name="OfferKeyBackup">
/// Asks whether to export a fresh key backup, and opens the screen to do it on yes. A notice with
/// only an acknowledgement would leave the author to find the screen themselves, at the one moment
/// their existing backup has stopped being able to recover the catalog.
/// </param>
public readonly record struct PublishEffects(
    Action RecordPublishedSource,
    Action<string, string> ShowDialog,
    Func<Task> CommitHistoryAsync,
    Action<string> SetStatus,
    Action? OfferSigningSetup = null,
    Action? OfferKeyBackup = null);

/// <summary>
/// What the editor does with a <see cref="PublishResult"/>: what to say, what to record, and what
/// the caller is now allowed to do next.
///
/// <para>It is a separate type for one reason. The decisions the coordinator makes are covered by
/// tests to the last branch; what a view model then DOES with them was not covered at all, because
/// reaching that code means constructing an eighteen-argument view model that loads a project off
/// disk and opens dialogs. So the part that can be got wrong quietly — recording a folder as
/// published when it is not, telling the author their work went out when the bytes that went out
/// were an interrupted attempt's — lives here instead.</para>
///
/// <para>Both halves live here, and that is the point rather than an accident of layout.
/// <see cref="For"/> alone was tested first, and deleting the guard on the recording step in the
/// view model then kept every test green — the decision was right and nothing checked that it was
/// obeyed. <see cref="ApplyAsync"/> is what the caller uses, so the guards are covered by the same
/// tests as the decisions.</para>
/// </summary>
public sealed record PublishPresentation(bool ShowDialog, string StatusMessage)
{
    /// <summary>
    /// Whether the live catalog now describes what is in this folder. Gates work that must only
    /// happen once users can actually see the change — notably the release-gate change, which is
    /// what the server enforces and must never move ahead of the catalog that explains it.
    /// </summary>
    public bool CatalogMatchesLocal { get; init; }

    /// <summary>
    /// Whether to remember this folder's bytes as published. Only ever true together with
    /// <see cref="CatalogMatchesLocal"/> — the mark's whole job is telling a later project-open
    /// "this folder is merely stale" apart from "this folder holds work nobody has published", and
    /// project-open replaces the first without asking.
    /// </summary>
    public bool RecordLocalSourceAsPublished { get; init; }

    /// <summary>Whether to make the local git commit that records what went out.</summary>
    public bool CommitLocalHistory { get; init; }

    /// <summary>
    /// Whether to tell the author their key backup no longer covers what it was taken for. True
    /// exactly when this publish started the signed history: a backup made before generation 1
    /// holds the key but no publishing head, and an imported key with no head may neither start a
    /// history nor continue one. So the backup taken during setup is precisely the one that cannot
    /// recover the thing it was taken for, and the moment that becomes true is here.
    /// </summary>
    public bool PromptForFreshKeyBackup { get; init; }

    /// <summary>
    /// Whether to offer the catalog-signing screen afterwards. True only when the registry anchors
    /// a key this machine does not have — the one refusal the author can act on immediately, by
    /// restoring their backup. Every other refusal is about the catalog or the server, and pointing
    /// at the key screen would be misdirection.
    /// </summary>
    public bool OfferSigningSetup { get; init; }

    /// <summary>
    /// Maps a finished publish onto what the editor should do about it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PublishStatus.NotSigned"/>, which is not an outcome to present. It means the
    /// registry anchors no key for this plugin, so the caller's own unsigned publish path applies
    /// and has not run yet. Throwing makes forgetting that branch a loud failure instead of a
    /// publish that silently stops and reports "not signed" as though it were a refusal.
    /// </exception>
    public static PublishPresentation For(PublishResult result)
    {
        switch (result.Status)
        {
            case PublishStatus.Published:
                return new PublishPresentation(
                    // A plain success is a status line; the author watched it happen. A resend is
                    // not, because it succeeded at sending something OTHER than what is in the
                    // folder, and that is exactly what a status line gets skimmed past.
                    ShowDialog: !result.LocalSourceIsLive,
                    result.Message)
                {
                    CatalogMatchesLocal = result.LocalSourceIsLive,
                    RecordLocalSourceAsPublished = result.LocalSourceIsLive,
                    CommitLocalHistory = result.LocalSourceIsLive,
                    PromptForFreshKeyBackup = result.StartedHistory
                };

            case PublishStatus.AlreadyUpToDate:
                // Nothing was sent, so there is nothing for local history to record — but the
                // catalog does describe this folder, and saying so is what stops the next open
                // asking about a difference that is not there.
                //
                // Read off the result rather than assumed from the status, even though only one
                // path can produce this one and it does establish the fact. Asserting it here would
                // make this the second place that decides whether a folder is live, and the two
                // could then disagree silently — which is the shape of every serious defect this
                // design has had.
                return new PublishPresentation(ShowDialog: false, result.Message)
                {
                    CatalogMatchesLocal = result.LocalSourceIsLive,
                    RecordLocalSourceAsPublished = result.LocalSourceIsLive
                };

            case PublishStatus.Recovered:
                // An earlier publish turned out to have landed. What is live is that publish, not
                // this folder, so nothing here may be recorded as published.
                return new PublishPresentation(ShowDialog: true, result.Title)
                {
                    PromptForFreshKeyBackup = result.StartedHistory
                };

            case PublishStatus.Cancelled:
                return new PublishPresentation(ShowDialog: false, result.Message);

            case PublishStatus.NotSigned:
                throw new ArgumentOutOfRangeException(nameof(result), result.Status,
                    "A catalog with no signing key anchored in the registry publishes by the " +
                    "unsigned path, which the caller must run instead of presenting this.");

            case PublishStatus.SigningKeyMissing:
                // The status line says what HAPPENED, not what is still true. The author may restore
                // their key from the screen this offers, and the live region is set afterwards —
                // leaving "this machine can't sign for this catalog" there would announce, to
                // someone who had just fixed it, that it was still broken.
                return new PublishPresentation(ShowDialog: true,
                    "That publish wasn't sent. Choose Publish index again when the key is in place.")
                {
                    OfferSigningSetup = true
                };

            // Refused, LockHeld, Interrupted, RecoveryRequired — each says something the author has
            // to act on, and each is worded for them already. Interrupted is the one that must never
            // be softened into a status line: it is the only outcome where nothing at all can be
            // assumed about what is live.
            default:
                return new PublishPresentation(ShowDialog: true, result.Title);
        }
    }

    /// <summary>
    /// Decides what this outcome means and carries it out, in one place.
    ///
    /// <para>Deciding and applying live together on purpose. Splitting them left five one-line
    /// applications in a view model that no test could reach — and deleting the guard on the
    /// riskiest of them (recording the folder as published) kept every test green while arming the
    /// next project-open to replace work that was never published. A decision nothing checks is
    /// applied is not really checked.</para>
    /// </summary>
    /// <returns>Whether the live catalog now describes the folder the candidate came from.</returns>
    public static async Task<bool> ApplyAsync(PublishResult result, string pluginId, PublishEffects effects)
    {
        var presentation = For(result);

        if (presentation.RecordLocalSourceAsPublished) effects.RecordPublishedSource();
        if (presentation.ShowDialog) effects.ShowDialog(result.Title, result.Message);

        if (presentation.PromptForFreshKeyBackup) effects.OfferKeyBackup?.Invoke();

        if (presentation.OfferSigningSetup) effects.OfferSigningSetup?.Invoke();

        if (presentation.CommitLocalHistory) await effects.CommitHistoryAsync();

        // Last, so that what remains in the live region is what remains true after every dialog
        // has been dismissed.
        effects.SetStatus(presentation.StatusMessage);
        return presentation.CatalogMatchesLocal;
    }

    /// <summary>
    /// The follow-up asked after the history's first publish. A QUESTION rather than a notice: it
    /// is the one moment the author's existing backup stops being able to recover the thing it was
    /// taken for, and leaving them to go and find the right screen afterwards is how it does not
    /// get done. Yes opens the screen.
    ///
    /// <para>Separate from the result's own message because it is a different subject — the publish
    /// worked; what changed is what the old backup is now worth.</para>
    /// </summary>
    public static (string Title, string Message) FreshBackupPrompt(string pluginId) => (
        "Export a fresh key backup?",
        $"'{pluginId}' now has a signed history, and this machine is the only one that knows where " +
        "that history is up to.\n\n" +
        "Any backup you took before this publish holds the signing key but no publishing position, " +
        "and a key with no position can neither start a history nor continue one — so on a " +
        "replacement machine it could not publish this catalog at all.\n\n" +
        "Open catalog signing now to export a fresh one?");
}
