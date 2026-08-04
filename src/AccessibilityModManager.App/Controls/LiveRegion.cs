using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AccessibilityModManager.App.Controls;

/// <summary>
/// Makes a status line's bound text actually reach the screen reader.
///
/// <para><see cref="AutomationProperties.LiveSettingProperty"/> only MARKS an element as a live
/// region — WPF never raises <see cref="AutomationEvents.LiveRegionChanged"/> when the bound text
/// changes. A status line declared "Polite" and left at that is therefore silent. This app had ten
/// such declarations and exactly one view (Game Details) that raised the event by hand; the other
/// nine never announced anything, including the notice that a developer's catalog had been
/// refused.</para>
///
/// <para>Bind <c>controls:LiveRegion.Text</c> instead of <c>Text</c>: it writes the displayed text
/// and then announces it. The element still needs its own <c>AutomationProperties.LiveSetting</c>
/// — this type supplies the missing event, not the marking.</para>
/// </summary>
public static class LiveRegion
{
    /// <summary>
    /// The live text. Setting it writes <see cref="TextBlock.Text"/> first (UIA requires the
    /// displayed text to be current before the event is raised) and then queues one announcement.
    /// </summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(LiveRegion),
            new PropertyMetadata(null, OnTextChanged));

    public static void SetText(DependencyObject element, string? value)
        => element.SetValue(TextProperty, value);

    public static string? GetText(DependencyObject element)
        => (string?)element.GetValue(TextProperty);

    /// <summary>
    /// Announce WITHOUT owning the displayed text: bind <see cref="TextBlock.Text"/> as normal and
    /// set this only when the current state is worth interrupting for.
    ///
    /// <para>The split exists because most status lines are routine counts. A refresh sets a filter
    /// count, a "found N mods" summary and a "loaded N developers" summary within moments of each
    /// other, and announcing each one turned an ordinary refresh into three overlapping sentences.
    /// Amethyst's rule after hearing it: counts stay quiet, problems and things you actually did
    /// still speak.</para>
    ///
    /// <para>What gets SPOKEN is the element's text, not this value — <see cref="AutomationEvents.LiveRegionChanged"/>
    /// tells the screen reader to re-read the region. So this is a trigger, and the view model
    /// changes it only on the occasions worth hearing. Assign the displayed text FIRST.</para>
    /// </summary>
    public static readonly DependencyProperty AnnouncementProperty =
        DependencyProperty.RegisterAttached(
            "Announcement",
            typeof(string),
            typeof(LiveRegion),
            new PropertyMetadata(null, OnAnnouncementChanged));

    public static void SetAnnouncement(DependencyObject element, string? value)
        => element.SetValue(AnnouncementProperty, value);

    public static string? GetAnnouncement(DependencyObject element)
        => (string?)element.GetValue(AnnouncementProperty);

    /// <summary>
    /// Per-element announcement generation. Several updates can land in one dispatcher turn (a
    /// refresh writes "Loading…" and then its summary); only the newest survives to speak, so the
    /// user hears where they ended up rather than every intermediate state.
    /// </summary>
    private static readonly DependencyProperty GenerationProperty =
        DependencyProperty.RegisterAttached(
            "Generation",
            typeof(int),
            typeof(LiveRegion),
            new PropertyMetadata(0));

    private static void OnAnnouncementChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block) return;

        // Null/blank is the view model saying "nothing worth interrupting for" — and it is also how
        // it re-arms, so the next notable message is a real change even if it repeats the last one.
        if (string.IsNullOrWhiteSpace(e.NewValue as string)) return;

        Queue(block);
    }

    private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block) return;

        var text = e.NewValue as string;

        // Displayed text first, always — including when it is cleared.
        block.Text = text ?? string.Empty;

        // An empty value is a state being cleared, not news. Announcing it made NVDA say "blank",
        // which is why Game Details' hand-rolled version skipped it too.
        if (string.IsNullOrWhiteSpace(text)) return;

        Queue(block);
    }

    /// <summary>
    /// Schedules one announcement of whatever this element reads as by the time it runs.
    ///
    /// <para>Deferred a turn because a peer may not exist yet for an element still being realized,
    /// and UIA does not queue events for later realization — raising early raises into nothing.</para>
    /// </summary>
    private static void Queue(TextBlock block)
    {
        var generation = (int)block.GetValue(GenerationProperty) + 1;
        block.SetValue(GenerationProperty, generation);

        _ = block.Dispatcher.BeginInvoke(
            new Action(() => Announce(block, generation)),
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Speaks the text if it is still the current, visible, realized state of this element.
    ///
    /// <para>Every check here drops a stale announcement rather than deferring it. Progress text is
    /// assigned before the dialog is shown, view models set a status from their constructor, and
    /// all three main tab views stay instantiated under collapsed grids — so without these guards a
    /// hidden tab's status would be replayed the moment it became visible, announcing something the
    /// user had already moved on from.</para>
    /// </summary>
    private static void Announce(TextBlock block, int generation)
    {
        if ((int)block.GetValue(GenerationProperty) != generation) return;  // superseded
        if (!block.IsLoaded || !block.IsVisible) return;                    // not on screen
        if (PresentationSource.FromVisual(block) is null) return;           // no window yet
        if (string.IsNullOrWhiteSpace(block.Text)) return;                  // nothing to read
        if (AutomationProperties.GetLiveSetting(block) == AutomationLiveSetting.Off) return;

        var peer = UIElementAutomationPeer.FromElement(block)
                   ?? UIElementAutomationPeer.CreatePeerForElement(block);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
