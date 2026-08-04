using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace AccessibilityModManager.App.Controls;

/// <summary>
/// Turns a <see cref="TextBox"/> into text the user can READ but not change.
///
/// <para>Why not <c>IsReadOnly="True"</c>: that flips the automation peer to read-only, which puts
/// NVDA into browse mode and takes away caret navigation — the user can no longer arrow through the
/// text line by line, which is the entire point for a long description or changelog. So the box
/// stays nominally editable and every mutating input is swallowed instead.</para>
///
/// <para>Set <c>controls:ReadOnlyText.IsEnabled="True"</c> on the TextBox. This replaces three
/// hand-copied versions of the same handler block (mod description, changelog dialog, update
/// dialog), which had already drifted apart in what they blocked.</para>
///
/// <para>Known and accepted: because the peer reports editable, a UI Automation client calling
/// SetValue writes <see cref="TextBox.Text"/> directly, past these handlers. Every consumer binds
/// one-way from the view model, so the next update overwrites it and nothing downstream reads the
/// box — it is a cosmetic write by software the user is already running, not a way in.</para>
/// </summary>
public static class ReadOnlyText
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ReadOnlyText),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>
    /// Private wiring marker. Deliberately NOT <see cref="FrameworkElement.Tag"/>, which the
    /// previous copy used: Tag belongs to whoever consumes the control, so a template that set it
    /// for its own reasons would silently skip the protection and leave an editable box.
    /// </summary>
    private static readonly DependencyProperty IsWiredProperty =
        DependencyProperty.RegisterAttached(
            "IsWired",
            typeof(bool),
            typeof(ReadOnlyText),
            new PropertyMetadata(false));

    /// <summary>
    /// The exact CommandBinding instances this type installed, so detaching removes only those.
    /// Clearing the whole collection would take bindings the TextBox's own consumer had added.
    /// </summary>
    private static readonly DependencyProperty InstalledBindingsProperty =
        DependencyProperty.RegisterAttached(
            "InstalledBindings",
            typeof(List<CommandBinding>),
            typeof(ReadOnlyText),
            new PropertyMetadata(null));

    /// <summary>What AllowDrop was before we forced it off, so detaching puts it back.</summary>
    private static readonly DependencyProperty PreviousAllowDropProperty =
        DependencyProperty.RegisterAttached(
            "PreviousAllowDrop",
            typeof(bool),
            typeof(ReadOnlyText),
            new PropertyMetadata(false));

    /// <summary>
    /// Editor commands that change text. These bypass <see cref="UIElement.PreviewTextInput"/>
    /// entirely — WPF's text editor binds them to key gestures and inserts directly — so blocking
    /// typed characters alone left the "read-only" fields editable.
    ///
    /// <para>Space and Shift+Space are the same class of hole but cannot be listed here: WPF keeps
    /// <c>EditingCommands.Space</c> and <c>ShiftSpace</c> internal. They are stopped in
    /// <see cref="OnPreviewKeyDown"/> instead, which runs before a key gesture can invoke the
    /// command at all.</para>
    ///
    /// <para><b>TabForward and TabBackward must never be added here.</b> They were, briefly, on the
    /// reasoning that a tab is text — and they TRAPPED THE KEYBOARD. In a TextBox that does not
    /// accept tabs, Tab is how focus leaves the control, so swallowing the command left a user who
    /// moved into a description or a bio unable to get out by keyboard at all. Anything that blocks
    /// a key must be checked against what that key does for NAVIGATION, not only for editing.</para>
    /// </summary>
    private static readonly RoutedUICommand[] BlockedCommands =
    [
        ApplicationCommands.Paste,
        ApplicationCommands.Cut,
        ApplicationCommands.Undo,
        ApplicationCommands.Redo,
        EditingCommands.Backspace,
        EditingCommands.Delete,
        EditingCommands.DeleteNextWord,
        EditingCommands.DeletePreviousWord,
        EditingCommands.EnterParagraphBreak,
        EditingCommands.EnterLineBreak
    ];

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBox box) return;

        if (e.NewValue is not true)
        {
            Unwire(box);
            return;
        }

        // Idempotent: a TextBox inside a DataTemplate can be re-realized, and virtualized rows can
        // be recycled onto the same instance. Wiring twice would double every handler.
        if ((bool)box.GetValue(IsWiredProperty)) return;
        box.SetValue(IsWiredProperty, true);

        box.PreviewTextInput += OnPreviewTextInput;
        box.PreviewKeyDown += OnPreviewKeyDown;
        box.SetValue(PreviousAllowDropProperty, box.AllowDrop);
        box.AllowDrop = false;
        box.PreviewDragOver += OnPreviewDragOver;
        box.PreviewDrop += OnPreviewDrop;

        // Per-instance: CommandBindings is an instance collection, so this cannot be shared.
        var installed = new List<CommandBinding>(BlockedCommands.Length);
        foreach (var command in BlockedCommands)
        {
            var binding = new CommandBinding(
                command,
                (_, ev) => ev.Handled = true,
                (_, ev) => { ev.CanExecute = false; ev.Handled = true; });
            box.CommandBindings.Add(binding);
            installed.Add(binding);
        }
        box.SetValue(InstalledBindingsProperty, installed);
    }

    private static void Unwire(TextBox box)
    {
        if (!(bool)box.GetValue(IsWiredProperty)) return;
        box.SetValue(IsWiredProperty, false);

        box.PreviewTextInput -= OnPreviewTextInput;
        box.PreviewKeyDown -= OnPreviewKeyDown;
        box.PreviewDragOver -= OnPreviewDragOver;
        box.PreviewDrop -= OnPreviewDrop;
        box.AllowDrop = (bool)box.GetValue(PreviousAllowDropProperty);

        // Only the bindings this type added — the consumer may own others.
        if (box.GetValue(InstalledBindingsProperty) is List<CommandBinding> installed)
        {
            foreach (var binding in installed) box.CommandBindings.Remove(binding);
            box.ClearValue(InstalledBindingsProperty);
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = true;

    /// <summary>
    /// Blocks the keys that change text. Everything else — arrows, Home/End, Ctrl+C, shift
    /// selection — is deliberately left alone, because reading by caret is the whole purpose.
    ///
    /// <para>Space is in here as well as in <see cref="BlockedCommands"/>: it reaches the editor as
    /// a command rather than as text input, and belt-and-braces costs nothing on a field that is
    /// only ever read.</para>
    /// </summary>
    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Back or Key.Delete or Key.Enter or Key.Return or Key.Space)
            e.Handled = true;
    }

    private static void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnPreviewDrop(object sender, DragEventArgs e) => e.Handled = true;
}
