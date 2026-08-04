using AccessibilityModManager.AuthorTool.Services;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// What the editor does with each publish outcome.
///
/// <para>This is the half of publishing that has never been covered. Every decision the coordinator
/// makes is tested to the last branch, and then a view model turns those decisions into side
/// effects — recording a folder as published, letting a release gate change, telling the author it
/// worked — and until now nothing checked that step at all. The failure it guards against is quiet
/// by nature: the publish really did succeed, so nothing looks wrong, and the damage shows up on the
/// next project-open when a folder gets replaced because something claimed it was already
/// published.</para>
/// </summary>
public sealed class PublishPresentationTests
{
    private static PublishResult Result(
        PublishStatus status, bool localSourceIsLive = false, bool startedHistory = false) =>
        new(status, "Title", "Message")
        {
            LocalSourceIsLive = localSourceIsLive,
            StartedHistory = startedHistory
        };

    // ---- the invariant that matters most, over every status there is ----

    [Theory]
    [MemberData(nameof(EveryPresentableStatus))]
    public void A_folder_is_only_ever_recorded_as_published_when_the_catalog_says_so(PublishStatus status)
    {
        foreach (var local in new[] { true, false })
        {
            var presentation = PublishPresentation.For(Result(status, localSourceIsLive: local));

            // The mark exists to tell a stale folder from an unpublished one, and project-open
            // replaces stale folders without asking. Recording one the catalog does not describe
            // hands the next open permission to destroy work.
            if (presentation.RecordLocalSourceAsPublished)
                Assert.True(presentation.CatalogMatchesLocal);
        }
    }

    [Theory]
    [MemberData(nameof(EveryPresentableStatus))]
    public void Nothing_claims_the_folder_is_live_unless_the_publish_said_it_was(PublishStatus status)
    {
        var presentation = PublishPresentation.For(Result(status, localSourceIsLive: false));

        Assert.False(presentation.CatalogMatchesLocal);
        Assert.False(presentation.RecordLocalSourceAsPublished);
    }

    public static TheoryData<PublishStatus> EveryPresentableStatus() =>
        [.. Enum.GetValues<PublishStatus>().Where(s => s != PublishStatus.NotSigned)];

    // ---- a publish that put this folder live ----

    [Fact]
    public void A_publish_of_this_folder_records_it_and_lets_the_gate_change_follow()
    {
        var presentation = PublishPresentation.For(
            Result(PublishStatus.Published, localSourceIsLive: true));

        Assert.True(presentation.CatalogMatchesLocal);
        Assert.True(presentation.RecordLocalSourceAsPublished);
        Assert.False(presentation.PromptForFreshKeyBackup);

        // No dialog: the author pressed Publish and watched it happen. The status line is enough.
        Assert.False(presentation.ShowDialog);
        Assert.Equal("Message", presentation.StatusMessage);
    }

    [Fact]
    public void A_resend_succeeds_without_any_of_that()
    {
        var presentation = PublishPresentation.For(
            Result(PublishStatus.Published, localSourceIsLive: false));

        // It genuinely published — a generation went live — and yet every side effect keyed to
        // "this folder is now live" must stay off, because what went live was an interrupted
        // attempt's bytes and anything edited since is still unpublished.
        Assert.False(presentation.CatalogMatchesLocal);
        Assert.False(presentation.RecordLocalSourceAsPublished);

        // And it is worth stopping for: a status line is exactly what gets skimmed past, and the
        // thing to be understood here is that the edits did not go out.
        Assert.True(presentation.ShowDialog);
    }

    [Fact]
    public void The_first_publish_of_a_history_asks_for_a_fresh_key_backup()
    {
        var presentation = PublishPresentation.For(
            Result(PublishStatus.Published, localSourceIsLive: true, startedHistory: true));

        Assert.True(presentation.PromptForFreshKeyBackup);
        Assert.True(presentation.CatalogMatchesLocal);
    }

    [Fact]
    public void Discovering_the_first_publish_had_landed_asks_for_one_too()
    {
        // The history started either way, so the pre-bootstrap backup is stale either way.
        var presentation = PublishPresentation.For(
            Result(PublishStatus.Recovered, startedHistory: true));

        Assert.True(presentation.PromptForFreshKeyBackup);
        Assert.False(presentation.RecordLocalSourceAsPublished);
        Assert.True(presentation.ShowDialog);
    }

    // ---- the outcomes where nothing was sent ----

    [Fact]
    public void A_catalog_already_describing_this_folder_is_recorded_but_not_committed()
    {
        var presentation = PublishPresentation.For(
            Result(PublishStatus.AlreadyUpToDate, localSourceIsLive: true));

        Assert.True(presentation.CatalogMatchesLocal);
        Assert.True(presentation.RecordLocalSourceAsPublished);

        // Nothing went out, so there is nothing for local history to record.
        Assert.False(presentation.ShowDialog);
    }

    [Fact]
    public void Cancelling_says_so_quietly()
    {
        var presentation = PublishPresentation.For(Result(PublishStatus.Cancelled));

        Assert.False(presentation.ShowDialog);
        Assert.False(presentation.CatalogMatchesLocal);
    }

    [Theory]
    [InlineData(PublishStatus.Refused)]
    [InlineData(PublishStatus.SigningKeyMissing)]
    [InlineData(PublishStatus.LockHeld)]
    [InlineData(PublishStatus.Interrupted)]
    [InlineData(PublishStatus.RecoveryRequired)]
    public void Everything_the_author_has_to_act_on_is_put_in_front_of_them(PublishStatus status)
    {
        var presentation = PublishPresentation.For(Result(status));

        Assert.True(presentation.ShowDialog);
        Assert.False(presentation.CatalogMatchesLocal);
    }

    [Fact]
    public void An_interrupted_publish_is_never_softened_into_a_status_line()
    {
        // The one outcome where nothing at all may be assumed about the live catalog. The message
        // that used to be shown here claimed the live index was unchanged, which is exactly the
        // wrong direction to be wrong in.
        var presentation = PublishPresentation.For(Result(PublishStatus.Interrupted));

        Assert.True(presentation.ShowDialog);
    }

    // ---- the branch the caller must take instead ----

    [Fact]
    public void A_catalog_with_no_key_anchored_for_it_is_not_an_outcome_to_present()
    {
        // It means the unsigned publish path has not run yet. Presenting it would report "not
        // signed" as though it were a refusal and stop publishing altogether — for every plugin,
        // today, since none of them anchor a key. Failing loudly is the only safe reading.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PublishPresentation.For(Result(PublishStatus.NotSigned)));
    }

    [Fact]
    public void The_backup_prompt_names_the_plugin_it_is_about()
    {
        var (title, message) = PublishPresentation.FreshBackupPrompt("amethyst");

        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.Contains("amethyst", message, StringComparison.Ordinal);
    }

    // ---- applying an outcome, observed ----
    //
    // Deciding correctly and then applying the decision wrongly costs exactly as much as deciding
    // wrongly, and for a while only the first half was covered: deleting the guard on the recording
    // step left every test above green. These watch what actually gets called.

    private sealed class Spy
    {
        public int Recorded;
        public int BackupOffers;
        public readonly List<string> Dialogs = [];
        public string? Status;

        public PublishEffects Effects => new(
            RecordPublishedSource: () => Recorded++,
            ShowDialog: (title, _) => Dialogs.Add(title),
            SetStatus: status => Status = status,
            OfferKeyBackup: () => BackupOffers++);
    }

    [Fact]
    public async Task Publishing_this_folder_records_it_once()
    {
        var spy = new Spy();

        var matches = await PublishPresentation.ApplyAsync(
            Result(PublishStatus.Published, localSourceIsLive: true), "amethyst", spy.Effects);

        Assert.True(matches);
        Assert.Equal(1, spy.Recorded);
        Assert.Empty(spy.Dialogs);
        Assert.Equal("Message", spy.Status);
    }

    [Fact]
    public async Task A_resend_records_nothing_and_commits_nothing()
    {
        // The one that matters. It published — a generation went live — but what went live was an
        // interrupted attempt's bytes, so the folder still holds work nobody has published. Marking
        // it published here is what lets the next project-open replace that work without asking.
        var spy = new Spy();

        var matches = await PublishPresentation.ApplyAsync(
            Result(PublishStatus.Published, localSourceIsLive: false), "amethyst", spy.Effects);

        Assert.False(matches);
        Assert.Equal(0, spy.Recorded);
        Assert.Single(spy.Dialogs);
    }

    [Fact]
    public async Task Discovering_a_publish_had_landed_records_nothing()
    {
        var spy = new Spy();

        var matches = await PublishPresentation.ApplyAsync(
            Result(PublishStatus.Recovered), "amethyst", spy.Effects);

        Assert.False(matches);
        Assert.Equal(0, spy.Recorded);
    }

    [Fact]
    public async Task Starting_the_history_offers_to_export_a_fresh_backup()
    {
        var spy = new Spy();

        await PublishPresentation.ApplyAsync(
            Result(PublishStatus.Published, localSourceIsLive: true, startedHistory: true),
            "amethyst", spy.Effects);

        // An OFFER, not a notice. This is the moment the author's existing backup stops being able
        // to recover the catalog, and a message they can only acknowledge leaves them to go and
        // find the right screen — which is how it does not get done.
        Assert.Equal(1, spy.BackupOffers);
        Assert.Equal(1, spy.Recorded);

        // And the publish itself stays a status line, so the offer is the only thing that stops them.
        Assert.Empty(spy.Dialogs);
    }

    [Fact]
    public async Task An_ordinary_publish_does_not_ask_about_backups()
    {
        var spy = new Spy();

        await PublishPresentation.ApplyAsync(
            Result(PublishStatus.Published, localSourceIsLive: true), "amethyst", spy.Effects);

        Assert.Equal(0, spy.BackupOffers);
    }

    [Fact]
    public void The_backup_prompt_is_worded_as_a_question()
    {
        var (title, message) = PublishPresentation.FreshBackupPrompt("amethyst");

        Assert.EndsWith("?", title, StringComparison.Ordinal);
        Assert.EndsWith("?", message.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_touches_nothing_but_the_dialog_and_the_status_line()
    {
        var spy = new Spy();

        var matches = await PublishPresentation.ApplyAsync(
            Result(PublishStatus.RecoveryRequired), "amethyst", spy.Effects);

        Assert.False(matches);
        Assert.Equal(0, spy.Recorded);
        Assert.Equal(["Title"], spy.Dialogs);
        Assert.Equal("Title", spy.Status);
    }

    [Theory]
    [MemberData(nameof(EveryPresentableStatus))]
    public async Task Nothing_is_recorded_for_an_outcome_that_did_not_put_this_folder_live(
        PublishStatus status)
    {
        var spy = new Spy();

        var matches = await PublishPresentation.ApplyAsync(
            Result(status, localSourceIsLive: false), "amethyst", spy.Effects);

        Assert.False(matches);
        Assert.Equal(0, spy.Recorded);
    }
}
