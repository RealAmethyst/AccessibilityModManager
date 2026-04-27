using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AccessibilityModManager.App.ViewModels;

namespace AccessibilityModManager.App.Views;

public partial class GameDetailsView : UserControl
{
    public GameDetailsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // IsVisibleChanged is the correct hook for "the page just appeared" — fires when the
        // wrapping Grid's Visibility flips from Collapsed to Visible (driven by IsDetailsOpen).
        // Without this, when the previously-focused Games ListView item is hidden, WPF falls
        // back to focusing the Window root and the screen reader says "Accessibility Mod Manager
        // window" instead of landing on something useful inside the new page.
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Detach from the previous VM so we don't leak handlers when the user navigates
        // between Game Details overlays.
        if (e.OldValue is GameDetailsViewModel oldGameVm)
            oldGameVm.OperationCompleted -= OnOperationCompleted;
        if (e.OldValue is INotifyPropertyChanged oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;

        if (e.NewValue is INotifyPropertyChanged newVm)
            newVm.PropertyChanged += OnViewModelPropertyChanged;
        if (e.NewValue is GameDetailsViewModel newGameVm)
            newGameVm.OperationCompleted += OnOperationCompleted;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            // Land on Back so the user can tab forward to Play, Open Folder, Channel,
            // mod list, Dependencies, etc. and discover the page layout by keyboard.
            // Deferred so the visual tree finishes laying out the now-visible content.
            _ = Dispatcher.BeginInvoke(
                new Action(() => BackButton.Focus()),
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void OnOperationCompleted()
    {
        // After a successful install/update/uninstall, focus the first mod card's wrapping
        // ContentControl so the screen reader announces its updated state via
        // AutomationProperties.Name (the AnnouncementText binding).
        _ = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (ModGroupsList.Items.Count == 0) return;
                var container = ModGroupsList.ItemContainerGenerator.ContainerFromIndex(0);
                if (FindVisualChild<ContentControl>(container) is { } card)
                    card.Focus();
            }),
            DispatcherPriority.ApplicationIdle);
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>
    /// Same edit-blocking pattern as ChangelogDialog: the description TextBox stays
    /// "editable" so NVDA treats it as focus-mode-navigable text (arrow keys move the caret),
    /// but typing, deleting, pasting, and cutting are all swallowed so the user can't actually
    /// modify the bound description. A plain IsReadOnly=true would punt NVDA into browse mode
    /// and lose focus-mode navigation.
    /// </summary>
    private void DescriptionBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = true;

    private void DescriptionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Back or Key.Delete or Key.Enter or Key.Return)
            e.Handled = true;
    }

    /// <summary>
    /// Wires per-instance CommandBindings that swallow paste / cut. PreviewTextInput catches
    /// typed characters but not clipboard paste or right-click cut — the CommandBindings cover
    /// that gap. Done in Loaded because the TextBox lives in a DataTemplate, so each row in
    /// the mod-list gets its own instance with its own CommandBindings collection.
    /// </summary>
    private void DescriptionBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        // Idempotent guard — Loaded can fire more than once if the row recycles.
        if (box.Tag is bool wired && wired) return;
        box.Tag = true;

        box.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Paste,
            (_, ev) => ev.Handled = true,
            (_, ev) => { ev.CanExecute = false; ev.Handled = true; }));
        box.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Cut,
            (_, ev) => ev.Handled = true,
            (_, ev) => { ev.CanExecute = false; ev.Handled = true; }));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // WPF's AutomationProperties.LiveSetting marks an element as a live region but does NOT
        // automatically fire LiveRegionChanged when its bound text updates. Without the explicit
        // event, screen readers don't pick up the change. Raise it manually here for the status
        // line so install/uninstall errors actually get announced.
        if (e.PropertyName == nameof(GameDetailsViewModel.StatusMessage))
        {
            // Skip empty transitions — when we clear StatusMessage after a successful op
            // the dialog already announced the result; firing LiveRegionChanged here would
            // make NVDA say "blank".
            if (DataContext is GameDetailsViewModel vm && string.IsNullOrEmpty(vm.StatusMessage))
                return;

            var peer = UIElementAutomationPeer.FromElement(StatusMessageBlock)
                       ?? UIElementAutomationPeer.CreatePeerForElement(StatusMessageBlock);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
