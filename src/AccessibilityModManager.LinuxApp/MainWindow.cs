using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Detection;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Infrastructure.Patreon;
using Serilog;

namespace AccessibilityModManager.LinuxApp;

internal sealed class MainWindow : Window
{
    private readonly ILogger logger = CreateLogger();
    private readonly HttpClient httpClient = new();
    private readonly SecretServicePatreonAccountStore patreonStore = new();
    private readonly PatreonService patreon;
    private readonly ContentControl page = new();
    private readonly TabControl tabs = new SectionTabs();
    private readonly ListBox modsList = new();
    private readonly ListBox authorsList = new();
    private readonly ListBox userSourcesList = new();
    private readonly TextBox newSourceAddress = new();
    private readonly StackPanel filters = new() { Spacing = 6 };
    private readonly TextBlock modsStatus = new() { Focusable = true };
    private readonly TextBlock authorsStatus = new() { Focusable = true };
    private readonly TextBlock settingsStatus = new() { Focusable = true };
    private readonly TextBlock patreonStatus = new() { Focusable = true };
    private readonly Button patreonSignIn = new();
    private readonly Button patreonRefresh = new() { Content = "Refresh Patreon status" };
    private readonly Button refreshMods = new() { Content = "Refresh Mods" };
    private readonly Button openMod = new() { Content = "Open mod details" };
    private readonly ComboBox defaultChannel = new();
    private LinuxCatalog? catalog;
    private LinuxModEntry? currentMod;
    private LinuxAuthorEntry? currentAuthor;
    private TextBlock? activeDetailsStatus;
    private bool detailsFromAuthor;
    private bool updateBusy;
    private readonly Button checkUpdates = new() { Content = "Check for manager updates" };
    private readonly TextBlock updateStatus = new() { Focusable = true };
    private bool busy;
    private bool sourceBusy;
    private bool patreonBusy;
    private bool startupFocusPending = true;
    private bool focusModsWhenReady;
    private HashSet<string> selectedTags = [];
    private HashSet<string> selectedLanguages = [];
    private HashSet<string> selectedAuthors = [];

    public MainWindow()
    {
        patreon = new PatreonService(
            new PatreonClient(httpClient, PatreonAppRegistry.Manager, logger),
            patreonStore, new PatreonEntitlementCache(),
            httpClient, logger);
        Title = "Accessibility Mod Manager";
        Width = 900;
        Height = 650;
        MinWidth = 600;
        MinHeight = 400;
        AutomationProperties.SetName(this, "Accessibility Mod Manager");

        tabs.Items.Add(new TabItem { Header = "Mods", Content = BuildModsPage() });
        tabs.Items.Add(new TabItem { Header = "Authors", Content = BuildAuthorsPage() });
        tabs.Items.Add(new TabItem { Header = "Settings", Content = BuildSettingsPage() });
        tabs.SelectedIndex = 0;
        page.Content = tabs;
        Content = page;

        AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (page.Content == tabs && tabs.SelectedIndex == 0 && catalog is not null &&
                (e.Key == Key.Tab || e.Key == Key.Down) &&
                FocusManager?.GetFocusedElement() is null)
            {
                FocusModsList();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Escape || page.Content == tabs ||
                e.Source is ComboBox { IsDropDownOpen: true }) return;
            Back();
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Activated += (_, _) => QueueStartupFocus();
        Opened += async (_, _) =>
        {
            await LoadSettingsAsync();
            try { await patreon.LoadAsync(); }
            catch (Exception ex)
            {
                logger.Warning(ex, "Couldn't load the Linux Patreon session");
                patreonStatus.Text = "Patreon session unavailable. Open Settings for details.";
            }
            UpdatePatreonControls();
            await RefreshModsAsync(focusList: true);
            _ = CheckManagerUpdateAsync(false);
        };
        Closed += (_, _) => httpClient.Dispose();
    }

    private Control BuildModsPage()
    {
        var root = new Grid { Margin = new Thickness(16, 12, 16, 16) };
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        var heading = new StackPanel { Spacing = 8 };
        heading.Children.Add(new TextBlock { Text = "Mods", FontSize = 24, FontWeight = Avalonia.Media.FontWeight.Bold });
        refreshMods.HorizontalAlignment = HorizontalAlignment.Left;
        refreshMods.Click += async (_, _) => await RefreshModsAsync(focusList: true);
        heading.Children.Add(refreshMods);
        root.Children.Add(heading);

        var body = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(240)));
        body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var filterScroll = new ScrollViewer { Content = filters, Margin = new Thickness(0, 0, 12, 0) };
        var filterHeading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filterHeading.Children.Add(new TextBlock { Text = "Filters", FontSize = 16 });
        var clear = new Button { Content = "Clear" };
        AutomationProperties.SetName(clear, "Clear all filters");
        clear.Click += async (_, _) =>
        {
            selectedTags.Clear(); selectedLanguages.Clear(); selectedAuthors.Clear();
            await SaveFiltersAsync();
            RebuildFilters();
            FilterMods();
        };
        filterHeading.Children.Add(clear);
        filters.Children.Add(filterHeading);
        body.Children.Add(filterScroll);

        var listArea = new Grid { Margin = new Thickness(12, 0, 0, 0) };
        listArea.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        listArea.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        listArea.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetColumn(listArea, 1);
        body.Children.Add(listArea);
        AutomationProperties.SetName(modsList, "Mods");
        modsList.ContainerPrepared += (_, e) =>
        {
            if (modsList.ItemsSource is IReadOnlyList<LinuxModEntry> entries &&
                e.Index >= 0 && e.Index < entries.Count)
                AutomationProperties.SetName(e.Container, entries[e.Index].ToString());
            e.Container.Loaded += (_, _) =>
            {
                if (e.Container == modsList.ContainerFromIndex(modsList.SelectedIndex) &&
                    (startupFocusPending || focusModsWhenReady))
                    QueueSelectedModFocus();
            };
            if (e.Index == modsList.SelectedIndex &&
                (startupFocusPending || focusModsWhenReady))
                QueueSelectedModFocus();
        };
        modsList.SelectionChanged += (_, _) =>
        {
            openMod.IsEnabled = modsList.SelectedItem is LinuxModEntry;
            if (startupFocusPending || focusModsWhenReady) QueueSelectedModFocus();
        };
        modsList.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || modsList.SelectedItem is not LinuxModEntry mod) return;
            ShowModDetails(mod);
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        modsList.DoubleTapped += (_, _) =>
        {
            if (modsList.SelectedItem is LinuxModEntry mod) ShowModDetails(mod);
        };
        listArea.Children.Add(modsList);
        openMod.IsEnabled = false;
        openMod.HorizontalAlignment = HorizontalAlignment.Left;
        openMod.Margin = new Thickness(0, 8, 0, 0);
        openMod.Click += (_, _) =>
        {
            if (modsList.SelectedItem is LinuxModEntry mod) ShowModDetails(mod);
        };
        Grid.SetRow(openMod, 1);
        listArea.Children.Add(openMod);
        modsStatus.Margin = new Thickness(0, 8, 0, 0);
        modsStatus.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        Grid.SetRow(modsStatus, 2);
        listArea.Children.Add(modsStatus);
        return root;
    }

    private Control BuildAuthorsPage()
    {
        var root = new Grid { Margin = new Thickness(16, 12, 16, 16) };
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.Children.Add(new TextBlock { Text = "Authors", FontSize = 24, FontWeight = Avalonia.Media.FontWeight.Bold });
        AutomationProperties.SetName(authorsList, "Authors");
        authorsList.ContainerPrepared += (_, e) =>
        {
            if (authorsList.ItemsSource is IReadOnlyList<AuthorRow> entries &&
                e.Index >= 0 && e.Index < entries.Count)
                AutomationProperties.SetName(e.Container, entries[e.Index].ToString());
        };
        authorsList.Margin = new Thickness(0, 12, 0, 0);
        authorsList.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || authorsList.SelectedItem is not AuthorRow row) return;
            ShowAuthorDetails(row.Author);
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        authorsList.DoubleTapped += (_, _) =>
        {
            if (authorsList.SelectedItem is AuthorRow row) ShowAuthorDetails(row.Author);
        };
        Grid.SetRow(authorsList, 1);
        root.Children.Add(authorsList);
        var sourceSection = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 0) };
        sourceSection.Children.Add(new TextBlock { Text = "Sources you added" });
        AutomationProperties.SetName(userSourcesList, "Sources you added");
        userSourcesList.MaxHeight = 130;
        userSourcesList.ContainerPrepared += (_, e) =>
        {
            if (userSourcesList.ItemsSource is IReadOnlyList<UserPluginSource> entries &&
                e.Index >= 0 && e.Index < entries.Count)
                AutomationProperties.SetName(e.Container, DescribeSource(entries[e.Index]));
        };
        userSourcesList.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Delete || userSourcesList.SelectedItem is not UserPluginSource source) return;
            _ = RemoveSourceAsync(source);
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        sourceSection.Children.Add(userSourcesList);
        var removeSource = new Button { Content = "Remove source" };
        removeSource.Click += async (_, _) =>
        {
            if (userSourcesList.SelectedItem is UserPluginSource source)
                await RemoveSourceAsync(source);
        };
        sourceSection.Children.Add(removeSource);
        Grid.SetRow(sourceSection, 2);
        root.Children.Add(sourceSection);

        var sourceEntry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0) };
        sourceEntry.Children.Add(new TextBlock { Text = "Add a source", VerticalAlignment = VerticalAlignment.Center });
        AutomationProperties.SetName(newSourceAddress, "Add a source");
        AutomationProperties.SetHelpText(newSourceAddress, "Full https address of the author's index.json file");
        newSourceAddress.MinWidth = 360;
        newSourceAddress.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _ = AddSourceAsync();
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        sourceEntry.Children.Add(newSourceAddress);
        var addSource = new Button { Content = "Add source" };
        addSource.Click += async (_, _) => await AddSourceAsync();
        sourceEntry.Children.Add(addSource);
        Grid.SetRow(sourceEntry, 3);
        root.Children.Add(sourceEntry);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var open = new Button { Content = "Open author details" };
        open.Click += (_, _) =>
        {
            if (authorsList.SelectedItem is AuthorRow row) ShowAuthorDetails(row.Author);
        };
        var refresh = new Button { Content = "Refresh Authors" };
        refresh.Click += async (_, _) => await RefreshModsAsync(focusList: false);
        actions.Children.Add(open);
        actions.Children.Add(refresh);
        AutomationProperties.SetLiveSetting(authorsStatus, AutomationLiveSetting.Polite);
        actions.Children.Add(authorsStatus);
        Grid.SetRow(actions, 4);
        root.Children.Add(actions);
        return root;
    }

    private Control BuildSettingsPage()
    {
        var root = new StackPanel { Spacing = 10, Margin = new Thickness(16, 12, 16, 16), MaxWidth = 600,
            HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(new TextBlock { Text = "Settings", FontSize = 24, FontWeight = Avalonia.Media.FontWeight.Bold });
        root.Children.Add(new TextBlock { Text = "Manager version " + CurrentVersion.ToString(3) });
        checkUpdates.Click += async (_, _) => await CheckManagerUpdateAsync(true);
        root.Children.Add(checkUpdates);
        AutomationProperties.SetLiveSetting(updateStatus, AutomationLiveSetting.Polite);
        root.Children.Add(updateStatus);
        root.Children.Add(new TextBlock { Text = "Default channel" });
        defaultChannel.Items.Add("stable");
        defaultChannel.Items.Add("beta");
        AutomationProperties.SetName(defaultChannel, "Default channel");
        root.Children.Add(defaultChannel);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = "Save Settings" };
        save.Click += async (_, _) =>
        {
            try
            {
                var channel = defaultChannel.SelectedItem as string ?? "stable";
                await new ConfigService(logger).UpdateAsync(config => config.DefaultChannel = channel);
                settingsStatus.Text = "Settings saved.";
                settingsStatus.Focus();
            }
            catch (Exception ex) { ReportError(ex); }
        };
        actions.Children.Add(save);
        var logs = new Button { Content = "Open Logs" };
        logs.Click += (_, _) => OpenFolder(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibilityModManager", "logs"));
        actions.Children.Add(logs);
        root.Children.Add(actions);
        root.Children.Add(new TextBlock { Text = "Patreon", FontSize = 18 });
        AutomationProperties.SetName(patreonStatus, "Patreon status");
        root.Children.Add(patreonStatus);
        var patreonActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        patreonSignIn.Click += async (_, _) => await SignInOrOutOfPatreonAsync();
        patreonRefresh.Click += async (_, _) => await RefreshPatreonAsync();
        patreonActions.Children.Add(patreonSignIn);
        patreonActions.Children.Add(patreonRefresh);
        root.Children.Add(patreonActions);
        AutomationProperties.SetLiveSetting(settingsStatus, AutomationLiveSetting.Polite);
        root.Children.Add(settingsStatus);
        return new ScrollViewer { Content = root };
    }

    private static Version CurrentVersion => typeof(MainWindow).Assembly.GetName().Version!;

    private async Task CheckManagerUpdateAsync(bool manual)
    {
        if (updateBusy) return;
        updateBusy = true;
        checkUpdates.IsEnabled = false;
        try
        {
            var checker = new UpdateChecker(httpClient, logger);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var update = await checker.CheckForUpdateAsync(CurrentVersion, timeout.Token);
            updateStatus.Text = checker.LastError is not null ? "Could not check for updates. Try again later."
                : update is null ? "No newer Linux manager release is available."
                : $"Manager version {update.Version} is available.";
            if (update is not null && !busy && !sourceBusy && !patreonBusy)
            {
                startupFocusPending = false;
                focusModsWhenReady = false;
                await new ManagerUpdateDialog(checker, update, Close).ShowDialog(this);
            }
            else if (manual) updateStatus.Focus();
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Linux manager update check failed");
            updateStatus.Text = "Could not check for updates. Try again later.";
            if (manual) updateStatus.Focus();
        }
        finally { updateBusy = false; checkUpdates.IsEnabled = true; }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var config = await new ConfigService(logger).LoadAsync();
            defaultChannel.SelectedItem = config.DefaultChannel is "beta" ? "beta" : "stable";
            selectedTags = config.SelectedTagFilters.ToHashSet(StringComparer.OrdinalIgnoreCase);
            selectedLanguages = config.SelectedLanguageFilters.ToHashSet(StringComparer.OrdinalIgnoreCase);
            selectedAuthors = config.SelectedAuthorFilters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private void UpdatePatreonControls()
    {
        var account = patreon.CurrentAccount;
        patreonStatus.Text = account is null
            ? "Not signed in. Sign in to see Patreon-gated releases."
            : "Signed in as " + (account.FullName ?? account.Email ?? "your Patreon account") + "." +
              (patreonStore.IsSessionOnly
                  ? " This sign-in lasts until you log out of Linux because the password store is unavailable."
                  : "");
        patreonSignIn.Content = account is null ? "Sign in to Patreon" : "Sign out of Patreon";
        patreonSignIn.IsEnabled = !patreonBusy;
        patreonRefresh.IsVisible = account is not null;
        patreonRefresh.IsEnabled = !patreonBusy;
    }

    private async Task SignInOrOutOfPatreonAsync()
    {
        if (patreonBusy) return;
        patreonBusy = true;
        UpdatePatreonControls();
        try
        {
            if (patreon.IsSignedIn)
            {
                await patreon.SignOutAsync(revokeOnPatreon: true, CancellationToken.None);
                settingsStatus.Text = "Signed out of Patreon.";
            }
            else
            {
                settingsStatus.Text = "Opening Patreon sign-in in your browser.";
                await patreon.SignInAsync(CancellationToken.None,
                    new Progress<string>(message => settingsStatus.Text = message));
                var refreshed = await patreon.RefreshEntitlementsAsync(CancellationToken.None);
                var account = patreon.CurrentAccount;
                var name = account?.FullName ?? account?.Email ?? "your Patreon account";
                settingsStatus.Text = refreshed
                    ? patreonStore.IsSessionOnly
                        ? "Signed in to Patreon as " + name + " for this Linux desktop session."
                        : "Signed in to Patreon as " + name + "."
                    : "Patreon could not confirm this account. Please sign in again.";
            }
            await RefreshModsAsync(focusList: false);
            settingsStatus.Focus();
        }
        catch (Exception ex) { ReportError(ex, settingsStatus); }
        finally
        {
            patreonBusy = false;
            UpdatePatreonControls();
        }
    }

    private async Task RefreshPatreonAsync()
    {
        if (patreonBusy || !patreon.IsSignedIn) return;
        patreonBusy = true;
        UpdatePatreonControls();
        try
        {
            var refreshed = await patreon.RefreshEntitlementsAsync(CancellationToken.None);
            await RefreshModsAsync(focusList: false);
            settingsStatus.Text = refreshed ? "Patreon status refreshed." :
                "Patreon session expired. Sign in again.";
            settingsStatus.Focus();
        }
        catch (Exception ex) { ReportError(ex, settingsStatus); }
        finally
        {
            patreonBusy = false;
            UpdatePatreonControls();
        }
    }

    private async Task RefreshModsAsync(bool focusList)
    {
        if (busy) return;
        busy = true;
        refreshMods.IsEnabled = false;
        ShowMessage("Detecting mods...");
        try
        {
            catalog = await new LinuxCatalogService(httpClient, logger, patreon).LoadAsync();
            authorsList.ItemsSource = catalog.Authors.Select(author => new AuthorRow(author)).ToArray();
            if (authorsList.ItemCount > 0 && authorsList.SelectedIndex < 0) authorsList.SelectedIndex = 0;
            userSourcesList.ItemsSource = catalog.UserSources;
            RebuildFilters();
            FilterMods();
            var message = $"Found {catalog.Mods.Count} mods.";
            if (catalog.FromCache) message += " Showing the last verified offline catalog.";
            if (catalog.Unavailable.Count > 0)
                message += " Could not check: " + string.Join(", ", catalog.Unavailable) + ".";
            ShowMessage(message);
            if (focusList && tabs.SelectedIndex == 0)
            {
                if (startupFocusPending) QueueStartupFocus();
                else FocusWhenLoaded(modsList, () => FocusModsList());
            }
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            busy = false;
            refreshMods.IsEnabled = true;
        }
    }

    private void RebuildFilters()
    {
        while (filters.Children.Count > 1) filters.Children.RemoveAt(filters.Children.Count - 1);
        if (catalog is null) return;
        AddFilterGroup("Tags", catalog.Mods.SelectMany(mod => mod.Game.Tags), selectedTags);
        AddFilterGroup("Languages", catalog.Mods.SelectMany(mod => mod.Game.Languages), selectedLanguages);
        AddFilterGroup("Authors", catalog.Mods.Select(mod => mod.Author.Author), selectedAuthors);
    }

    private void AddFilterGroup(string title, IEnumerable<string> values, HashSet<string> selected)
    {
        var group = new StackPanel { Spacing = 3, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
        {
            var check = new CheckBox { Content = value, IsChecked = selected.Contains(value) };
            AutomationProperties.SetName(check, value);
            check.IsCheckedChanged += async (_, _) =>
            {
                if (check.IsChecked == true) selected.Add(value); else selected.Remove(value);
                FilterMods();
                await SaveFiltersAsync();
            };
            group.Children.Add(check);
        }
        var expander = new FilterExpander(title, group);
        filters.Children.Add(expander);
    }

    private async Task SaveFiltersAsync()
    {
        try
        {
            await new ConfigService(logger).UpdateAsync(config =>
            {
                config.SelectedTagFilters = selectedTags.Order().ToList();
                config.SelectedLanguageFilters = selectedLanguages.Order().ToList();
                config.SelectedAuthorFilters = selectedAuthors.Order().ToList();
            });
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private void FilterMods()
    {
        if (catalog is null) return;
        var previous = modsList.SelectedItem as LinuxModEntry;
        var visible = catalog.Mods.Where(mod =>
            (selectedTags.Count == 0 || mod.Game.Tags.Any(selectedTags.Contains)) &&
            (selectedLanguages.Count == 0 || mod.Game.Languages.Any(selectedLanguages.Contains)) &&
            (selectedAuthors.Count == 0 || selectedAuthors.Contains(mod.Author.Author))).ToArray();
        modsList.ItemsSource = visible;
        modsList.SelectedItem = previous is null ? null : visible.FirstOrDefault(mod =>
            mod.Author.Id == previous.Author.Id && mod.Game.GameId == previous.Game.GameId);
        if (modsList.SelectedIndex < 0 && visible.Length > 0) modsList.SelectedIndex = 0;
        openMod.IsEnabled = modsList.SelectedItem is LinuxModEntry;
    }

    private bool FocusModsList()
    {
        if (modsList.ItemCount == 0)
        {
            focusModsWhenReady = false;
            return refreshMods.Focus();
        }
        if (modsList.SelectedIndex < 0) modsList.SelectedIndex = 0;
        var item = modsList.ContainerFromIndex(modsList.SelectedIndex) as Control;
        if (item?.IsLoaded == true && item.Focus() && item.IsFocused)
        {
            focusModsWhenReady = false;
            startupFocusPending = false;
            logger.Information("Focused selected mod row: {Name}", AutomationProperties.GetName(item));
            return true;
        }
        focusModsWhenReady = true;
        logger.Information("Selected mod row is not ready for focus: index {Index}, container {ContainerReady}, loaded {Loaded}",
            modsList.SelectedIndex, item is not null, item?.IsLoaded);
        refreshMods.Focus();
        return false;
    }

    private void QueueSelectedModFocus()
    {
        if (!IsActive || page.Content != tabs || tabs.SelectedIndex != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsActive || page.Content != tabs || tabs.SelectedIndex != 0 ||
                (!startupFocusPending && !focusModsWhenReady)) return;
            FocusModsList();
        }, DispatcherPriority.Loaded);
    }

    private void QueueStartupFocus()
    {
        if (!startupFocusPending || catalog is null || !IsActive) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!startupFocusPending || !IsActive || page.Content != tabs || tabs.SelectedIndex != 0) return;
            if (!modsList.IsLoaded) return;
            // A selected row can already be Avalonia's internal focus target before the
            // accessibility client sees the window. Move focus through a stable control
            // after activation so AT-SPI reports a fresh focus change on the selected row.
            refreshMods.Focus();
            Dispatcher.UIThread.Post(() =>
            {
                if (!startupFocusPending || !IsActive || page.Content != tabs || tabs.SelectedIndex != 0)
                    return;
                FocusModsList();
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    private static void FocusWhenLoaded(Control control, Action focus)
    {
        if (control.IsLoaded)
        {
            Dispatcher.UIThread.Post(focus, DispatcherPriority.Loaded);
            return;
        }
        void OnLoaded(object? sender, RoutedEventArgs args)
        {
            control.Loaded -= OnLoaded;
            Dispatcher.UIThread.Post(focus, DispatcherPriority.Loaded);
        }
        control.Loaded += OnLoaded;
    }

    private void FocusAuthorsList()
    {
        if (authorsList.ItemCount == 0) return;
        if (authorsList.SelectedIndex < 0) authorsList.SelectedIndex = 0;
        if (authorsList.ContainerFromIndex(authorsList.SelectedIndex) is Control item) item.Focus();
        else authorsList.Focus();
    }

    private async Task AddSourceAsync()
    {
        if (sourceBusy) return;
        if (string.IsNullOrWhiteSpace(newSourceAddress.Text))
        {
            ReportAuthorStatus("Type the address of the source you want to add first.");
            return;
        }
        sourceBusy = true;
        try
        {
            var configService = new ConfigService(logger);
            var config = await configService.LoadAsync();
            var registry = (await new PluginRegistryClient(httpClient, logger,
                new RegistrySignatureVerifier(RegistryTrustKey.PublicKeyPem, logger))
                .FetchRegistryAsync(new Uri(config.PluginRegistryUrl))).Value;
            var installed = await new ReceiptStore(logger).InstalledPluginIdsAsync();
            var preview = await new UserSourceAdder(new PluginRepoClient(httpClient, logger), logger)
                .PreviewAsync(newSourceAddress.Text, registry.Plugins, config.UserPluginSources,
                    installed, config.KnownPluginAddresses);
            if (!preview.CanAdd)
            {
                ReportAuthorStatus("That source wasn't added. " + preview.Refusal);
                return;
            }

            var warning = $"{preview.DisplayName} offers {preview.GameCount} mods.\n\n" +
                "Anyone can publish a source. This source has not been reviewed by Amethyst. " +
                "If you later install one of its mods, it can put files in game folders and run " +
                "supported setup steps with your user account's permissions. Adding the source " +
                "installs nothing now. Only add it if you trust its author.\n\n" +
                $"Developer: {preview.DisplayName}\nDeveloper id: {preview.PluginId}\n" +
                $"Address: {preview.IndexUrl}";
            if (!await ConfirmationDialog.ShowAsync(this, "Add a source you trust?", warning,
                    "I understand, add source"))
            {
                ReportAuthorStatus("Cancelled. Nothing was added.");
                return;
            }

            var committed = false;
            await configService.UpdateAsync(current =>
            {
                if (CatalogSourceResolver.CanAdd(registry.Plugins, current.UserPluginSources,
                        installed, preview.PluginId, preview.IndexUrl,
                        current.KnownPluginAddresses) is not null) return;
                current.UserPluginSources.Add(UserSourceAdder.Accept(preview, DateTimeOffset.UtcNow));
                current.KnownPluginAddresses[preview.PluginId] = preview.IndexUrl;
                committed = true;
            });
            if (!committed)
            {
                ReportAuthorStatus("That source wasn't added because its developer id is now in use.");
                return;
            }
            newSourceAddress.Text = "";
            await RefreshModsAsync(focusList: false);
            ReportAuthorStatus("Added " + preview.DisplayName + ".");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Couldn't add a source");
            ReportAuthorStatus("Couldn't add that source. " + CatalogRefusedException.SpeakableReason(ex));
        }
        finally { sourceBusy = false; }
    }

    private async Task RemoveSourceAsync(UserPluginSource source)
    {
        if (sourceBusy) return;
        sourceBusy = true;
        try
        {
            var name = string.IsNullOrWhiteSpace(source.DisplayName) ? source.PluginId : source.DisplayName;
            if (!await ConfirmationDialog.ShowAsync(this, "Remove source",
                    $"Remove {name}? Installed mods remain installed and can still be uninstalled. " +
                    "This stops new installs and updates from that source.", "Remove source"))
            {
                ReportAuthorStatus(name + " was kept. Nothing was removed.");
                return;
            }
            await new ConfigService(logger).UpdateAsync(config =>
                config.UserPluginSources.RemoveAll(item =>
                    string.Equals(SafeId.Canonical(item.PluginId), SafeId.Canonical(source.PluginId),
                        StringComparison.OrdinalIgnoreCase)));
            await RefreshModsAsync(focusList: false);
            ReportAuthorStatus($"Removed {name}. Mods you already installed are still installed.");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Couldn't remove source {PluginId}", source.PluginId);
            ReportAuthorStatus("Couldn't remove that source. " + ex.Message);
        }
        finally { sourceBusy = false; }
    }

    private void ReportAuthorStatus(string message)
    {
        authorsStatus.Text = message;
        AutomationProperties.SetName(authorsStatus, message);
        authorsStatus.Focus();
    }

    private static string DescribeSource(UserPluginSource source) =>
        $"{(string.IsNullOrWhiteSpace(source.DisplayName) ? source.PluginId : source.DisplayName)}, " +
        source.IndexUrl;

    private void ShowModDetails(LinuxModEntry mod, bool fromAuthor = false,
        string? completionMessage = null)
    {
        currentMod = mod;
        detailsFromAuthor = fromAuthor;
        var root = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var back = new Button { Content = "Back" };
        AutomationProperties.SetName(back, "Back");
        back.Click += (_, _) => Back();
        header.Children.Add(back);
        header.Children.Add(new TextBlock { Text = mod.Game.DisplayName, FontSize = 24,
            FontWeight = Avalonia.Media.FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(header);
        var gameActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (mod.Install is not null)
        {
            root.Children.Add(new TextBlock { Text = "Install path: " + mod.Install.InstallPath,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if ((mod.InstalledVersion is not null || mod.OtherSetup is not null) &&
                (!string.IsNullOrWhiteSpace(mod.Game.EffectiveSteamAppId) ||
                 mod.InstalledTarget == ReleaseTarget.Linux))
            {
                var play = new Button { Content = "Play" };
                AutomationProperties.SetName(play, "Play " + mod.Game.DisplayName);
                play.Click += async (_, _) =>
                {
                    try { await PlayGameAsync(mod); }
                    catch (Exception ex) { ReportError(ex); }
                };
                gameActions.Children.Add(play);
            }
            var folder = new Button { Content = "Open Folder" };
            folder.Click += (_, _) => OpenFolder(mod.Install.InstallPath);
            gameActions.Children.Add(folder);
        }
        if (mod.Install is null)
        {
            if (NativeGameInstaller.Available(mod.Game).Count > 0)
            {
                var installGame = new Button { Content = "Install game or emulator" };
                AutomationProperties.SetName(installGame, "Install game or emulator for " + mod.Game.DisplayName);
                installGame.Click += async (_, _) => await InstallGameAsync(mod);
                gameActions.Children.Add(installGame);
            }
            else if (mod.Game.Dependencies.Any(dependency => dependency.IsGameInstaller))
                root.Children.Add(new TextBlock
                {
                    Text = "The author has not provided a supported native Linux game or emulator installer. If you already installed it, choose its folder.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                });
            var browse = new Button { Content = "Browse for Folder" };
            AutomationProperties.SetName(browse, "Browse for " + mod.Game.DisplayName + " folder");
            browse.Click += async (_, _) => await BrowseForGameAsync(mod);
            gameActions.Children.Add(browse);
        }
        root.Children.Add(gameActions);
        var author = new Button { Content = "Developer" };
        AutomationProperties.SetName(author, "More from " + mod.Author.Author);
        author.Click += (_, _) => ShowAuthorDetails(mod.Author);
        root.Children.Add(author);
        if (mod.OtherSetup is not null)
        {
            root.Children.Add(new TextBlock
            {
                Text = mod.OtherSetup.PluginId == mod.Author.Id
                    ? "Your existing Proton installation of this author's mod is still active. " +
                      "It was installed under an older catalog game ID. Keep launching it through Steam. " +
                      "Import its installation record below to manage it here."
                    : "This game already has a Proton mod setup from another author. " +
                      "Keep using Steam Play; this catalog entry cannot replace that setup automatically.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            if (mod.OtherSetup.PluginId == mod.Author.Id && mod.Install is not null)
            {
                var import = new Button { Content = "Import existing installation" };
                import.Click += async (_, _) => await ImportExistingSetupAsync(mod);
                root.Children.Add(import);
            }
        }

        var channels = mod.Releases.Select(release => release.Channel).Distinct(StringComparer.Ordinal).ToArray();
        var channelChoice = new ComboBox { ItemsSource = channels };
        AutomationProperties.SetName(channelChoice, "Channel");
        if (channels.Length > 1)
        {
            root.Children.Add(new TextBlock { Text = "Channel" });
            root.Children.Add(channelChoice);
        }
        var card = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        card.Children.Add(new TextBlock { Text = $"{mod.ModName} by {mod.Author.Author} — {mod.Status}",
            FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        if (!string.IsNullOrWhiteSpace(mod.Game.Description))
        {
            var description = new TextBox { Text = mod.Game.Description, IsReadOnly = true,
                AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            AutomationProperties.SetName(description, "Mod description");
            card.Children.Add(description);
        }
        card.Children.Add(new TextBlock { Text = "Version" });
        var versionChoice = new ComboBox { MinWidth = 150 };
        AutomationProperties.SetName(versionChoice, "Version");
        card.Children.Add(versionChoice);
        var availability = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(availability, AutomationLiveSetting.Polite);
        card.Children.Add(availability);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var install = new Button { Content = "Install" };
        var update = new Button { Content = "Update" };
        var uninstall = new Button { Content = "Uninstall" };
        var changelog = new Button { Content = "View changelog" };
        AutomationProperties.SetName(install, "Install");
        AutomationProperties.SetName(update, "Update");
        AutomationProperties.SetName(uninstall, "Uninstall");
        AutomationProperties.SetName(changelog, "View changelog");
        actions.Children.Add(install); actions.Children.Add(update);
        actions.Children.Add(uninstall); actions.Children.Add(changelog);
        card.Children.Add(actions);
        var downloadProgress = new ProgressBar { Minimum = 0, Maximum = 100, IsVisible = false };
        AutomationProperties.SetName(downloadProgress, "Package download progress");
        card.Children.Add(downloadProgress);
        root.Children.Add(card);
        var detailsStatus = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Focusable = true, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetLiveSetting(detailsStatus, AutomationLiveSetting.Polite);
        detailsStatus.Text = completionMessage;
        activeDetailsStatus = detailsStatus;
        root.Children.Add(detailsStatus);

        if (mod.InstalledTarget == ReleaseTarget.XivLauncher && mod.Install?.SteamRootPath is { } xivSteam)
        {
            var loginSettings = new Button { Content = "XIVLauncher login settings" };
            loginSettings.Click += async (_, _) =>
            {
                if (busy) return;
                busy = true;
                loginSettings.IsEnabled = false;
                try
                {
                    detailsStatus.Text = "Preparing the accessible XIVLauncher.";
                    await Task.Run(() => new LinuxModInstaller(httpClient, logger).CreateXivLauncherSetup().EnsureReadyAsync(mod.Install));
                    var executable = Path.Combine(XlmInstaller.ToolDirectory(xivSteam), "xlcore", "XIVLauncher.Core");
                    Process.Start(new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!,
                        ArgumentList = { "--login-settings" }
                    });
                    detailsStatus.Text = "XIVLauncher login settings opened. Play the game from Steam after saving.";
                }
                catch (Exception ex) { detailsStatus.Text = ex.Message; }
                finally { busy = false; loginSettings.IsEnabled = true; }
            };
            gameActions.Children.Insert(Math.Min(1, gameActions.Children.Count), loginSettings);
        }

        void UpdateReleases()
        {
            var chosenChannel = channelChoice.SelectedItem as string;
            var releases = mod.Releases.Where(release => release.Channel == chosenChannel)
                .OrderByDescending(release => release.Version, VersionComparer.Instance)
                .ThenBy(release => ReleaseTarget.ForRuntime(release.TargetPlatform) == ReleaseTarget.Proton ? 0 : 1)
                .ToArray();
            versionChoice.ItemsSource = releases.Select(release => new ReleaseChoice(release)).ToArray();
            versionChoice.SelectedIndex = releases.Length > 0 ? 0 : -1;
        }
        void UpdateActions()
        {
            var release = (versionChoice.SelectedItem as ReleaseChoice)?.Release;
            var reason = release is null ? "Choose a release." : InstallUnavailableReason(mod, release);
            availability.Text = reason ?? "";
            install.IsVisible = mod.InstalledVersion is null;
            install.IsEnabled = release is not null && reason is null && mod.InstalledVersion is null;
            uninstall.IsVisible = mod.InstalledVersion is not null;
            uninstall.IsEnabled = mod.InstalledVersion is not null &&
                (mod.InstalledTarget is ReleaseTarget.Linux or ReleaseTarget.XivLauncher || mod.OwnSetup is not null);
            update.IsVisible = mod.InstalledVersion is not null && release is not null &&
                VersionComparer.Instance.Compare(release.Version, mod.InstalledVersion) != 0;
            update.IsEnabled = update.IsVisible && reason is null &&
                ReleaseTarget.Normalize(mod.InstalledTarget) ==
                (ReleaseTarget.ForRuntime(release?.TargetPlatform) is ReleaseTarget.Linux or ReleaseTarget.XivLauncher
                    ? ReleaseTarget.ForRuntime(release?.TargetPlatform) : ReleaseTarget.Proton) &&
                (mod.InstalledTarget is ReleaseTarget.Linux or ReleaseTarget.XivLauncher || mod.OwnSetup is not null);
            update.Content = release is not null && mod.InstalledVersion is not null &&
                VersionComparer.Instance.Compare(release.Version, mod.InstalledVersion) < 0
                    ? "Downgrade" : "Update";
            AutomationProperties.SetName(update, (string)update.Content!);
            changelog.IsVisible = release is not null &&
                (!string.IsNullOrWhiteSpace(release.Notes) || release.ChangelogUrl is not null);
        }
        channelChoice.SelectionChanged += (_, _) => UpdateReleases();
        versionChoice.SelectionChanged += (_, _) => UpdateActions();
        install.Click += async (_, _) => await ApplyReleaseAsync(mod, (versionChoice.SelectedItem as ReleaseChoice)?.Release,
            update: false, detailsStatus, downloadProgress);
        update.Click += async (_, _) => await ApplyReleaseAsync(mod, (versionChoice.SelectedItem as ReleaseChoice)?.Release,
            update: true, detailsStatus, downloadProgress);
        uninstall.Click += async (_, _) => await UninstallAsync(mod, detailsStatus);
        changelog.Click += async (_, _) =>
        {
            if (versionChoice.SelectedItem is ReleaseChoice { Release: var release })
                await ShowTextDialogAsync("Changelog", release.Notes ??
                    "The author provided a changelog link: " + release.ChangelogUrl);
        };
        var preferredChannel = defaultChannel.SelectedItem as string ?? "stable";
        channelChoice.SelectedItem = channels.Contains(preferredChannel) ? preferredChannel : channels.FirstOrDefault();
        UpdateReleases();
        UpdateActions();
        page.Content = new ScrollViewer { Content = root };
        FocusWhenLoaded(completionMessage is null ? back : detailsStatus, () =>
        {
            if (currentMod != mod) return;
            if (completionMessage is null) back.Focus(); else detailsStatus.Focus();
        });
    }

    private static string? InstallUnavailableReason(LinuxModEntry mod, ModRelease release)
    {
        if (mod.Install is null) return "Choose the game's installation folder first.";
        var target = ReleaseTarget.ForRuntime(release.TargetPlatform);
        if (target == ReleaseTarget.XivLauncher)
        {
            if (mod.OwnSetup is not null || mod.OtherSetup is not null)
                return "Remove the existing Proton launch setup before installing XIVLauncher.";
            if (!mod.Game.Dependencies.Any(d => d.Fix?.Xlm is not null))
                return "The author needs to add the XIVLauncher on Linux (XLM) dependency.";
            try { _ = XivLauncherPaths.Resolve(mod.Install); return null; }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { return ex.Message; }
        }
        if (target == ReleaseTarget.Windows && mod.Game.Dependencies.Any(d => d.Fix?.Xlm is not null))
            return "Choose an XIVLauncher on Linux release. This Windows package uses a different launcher setup.";
        if (target == ReleaseTarget.Linux)
        {
            if (string.IsNullOrWhiteSpace(mod.Game.LinuxExeName))
                return "This catalog has no native Linux executable for the game.";
            var executable = PathSafety.CombineContained(mod.Install.InstallPath,
                mod.Game.LinuxExeName.Replace('\\', '/'));
            return File.Exists(executable) ? null : "The native Linux executable is missing from this game folder.";
        }
        if (string.IsNullOrWhiteSpace(mod.Game.EffectiveSteamAppId) || mod.Install.SteamRootPath is null)
            return "This Windows release needs a game detected through Steam for Proton setup.";
        if (mod.Install.ProtonPrefixPath is null)
            return "Launch the game once through Steam with Proton, then refresh Mods.";
        if (mod.OtherSetup is not null)
            return mod.OtherSetup.PluginId == mod.Author.Id
                ? "An earlier local installation of this mod already owns the Steam setup. " +
                  "Keep using Steam Play; its ownership must be migrated before an update."
                : "Another author owns this game's Steam mod setup.";
        return null;
    }

    private sealed record ReleaseChoice(ModRelease Release)
    {
        public override string ToString() => FormatRelease(Release);
    }

    private static string FormatRelease(ModRelease release) =>
        release.Version + ", " + ReleaseTarget.DisplayName(release.TargetPlatform);

    private void ShowAuthorDetails(LinuxAuthorEntry plugin)
    {
        currentMod = null;
        currentAuthor = plugin;
        detailsFromAuthor = false;
        var root = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
        var back = new Button { Content = "Back" };
        AutomationProperties.SetName(back, "Back");
        back.Click += (_, _) => Back();
        root.Children.Add(back);
        root.Children.Add(new TextBlock { Text = plugin.Author, FontSize = 24,
            FontWeight = Avalonia.Media.FontWeight.Bold });
        root.Children.Add(new TextBlock { Text = plugin.Description,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (plugin.Website is { } website) AddLink("Website", website);
        foreach (var (label, uri) in plugin.Links) AddLink(label, uri);
        if (links.Children.Count > 0) root.Children.Add(links);
        var authorMods = catalog?.Mods.Where(mod => mod.Author.Id == plugin.Id).ToArray() ?? [];
        var list = new ListBox { ItemsSource = authorMods };
        AutomationProperties.SetName(list, "Mods by " + plugin.Author);
        list.ContainerPrepared += (_, e) =>
        {
            if (e.Index >= 0 && e.Index < authorMods.Length)
                AutomationProperties.SetName(e.Container, authorMods[e.Index].ToString());
        };
        list.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || list.SelectedItem is not LinuxModEntry mod) return;
            ShowModDetails(mod, fromAuthor: true);
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.Children.Add(list);
        var authorStatus = new TextBlock { Focusable = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(authorStatus, AutomationLiveSetting.Polite);
        root.Children.Add(authorStatus);
        page.Content = new ScrollViewer { Content = root };
        activeDetailsStatus = authorStatus;
        FocusWhenLoaded(back, () =>
        {
            if (currentAuthor == plugin && currentMod is null) back.Focus();
        });

        void AddLink(string label, Uri uri)
        {
            var button = new Button { Content = label };
            AutomationProperties.SetName(button, plugin.Author + " " + label);
            button.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo("xdg-open", uri.AbsoluteUri) { UseShellExecute = false }); }
                catch (Exception ex) { ReportError(ex); }
            };
            links.Children.Add(button);
        }
    }

    private void Back()
    {
        if (detailsFromAuthor && currentAuthor is not null && currentMod is not null)
        {
            currentMod = null;
            ShowAuthorDetails(currentAuthor);
            return;
        }
        page.Content = tabs;
        currentMod = null;
        currentAuthor = null;
        activeDetailsStatus = null;
        FocusWhenLoaded(tabs, () =>
        {
            if (tabs.SelectedIndex == 1) FocusAuthorsList(); else FocusModsList();
        });
    }

    private async Task ApplyReleaseAsync(LinuxModEntry mod, ModRelease? release, bool update,
        TextBlock detailsStatus, ProgressBar downloadProgress)
    {
        if (release is null || busy) return;
        busy = true;
        try
        {
            string? localPackagePath = null;
            if (release.Patreon is { } gate && string.IsNullOrWhiteSpace(gate.ServerUrl))
            {
                if (!string.IsNullOrWhiteSpace(gate.PostId))
                    Process.Start(new ProcessStartInfo("xdg-open",
                        "https://www.patreon.com/posts/" + gate.PostId) { UseShellExecute = false });
                detailsStatus.Text = "Choose the ZIP for this Patreon release. Its SHA-256 will be checked before installation.";
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Choose " + mod.Game.DisplayName + " version " + release.Version + " ZIP",
                    AllowMultiple = false
                });
                if (files.Count == 0) return;
                localPackagePath = files[0].TryGetLocalPath() ??
                    throw new InvalidOperationException("The selected package must be a local file.");
            }
            SteamAccountConfig? account = null;
            string? proton = null;
            if (release.TargetPlatform == ReleaseTarget.XivLauncher)
            {
                account = await ChooseXivAccountAsync(mod);
                if (account is null) return;
                if (!await ConfirmationDialog.ShowAsync(this, "Set up XIVLauncher",
                        "Install this plugin and XIVLauncher's Steam compatibility tool? The manager will select XLCore [XLM], " +
                        "replace this game's Steam launch options with its speech setup, and register the plugin in XIVLauncher. " +
                        "Your previous Steam settings will be saved for uninstall. XLM is shared and will remain installed after removing the mod.", "Install and configure")) return;
                if (!await EnsureSteamClosedAsync(mod)) return;
            }
            else if (ReleaseTarget.ForRuntime(release.TargetPlatform) != ReleaseTarget.Linux)
            {
                var selection = await ChooseSetupAsync(mod);
                if (selection is null) return;
                account = selection.Value.Account;
                proton = selection.Value.Proton;
                if (!await EnsureSteamClosedAsync(mod)) return;
            }
            detailsStatus.Text = "Downloading and checking the selected release.";
            downloadProgress.Value = 0;
            downloadProgress.IsVisible = true;
            var lastBucket = -1;
            var progress = new Progress<ProgressInfo>(step =>
            {
                downloadProgress.Value = step.Percentage;
                if (release.TargetPlatform == ReleaseTarget.XivLauncher || step.StatusText.StartsWith("Preparing the verified", StringComparison.Ordinal))
                {
                    detailsStatus.Text = step.StatusText;
                    return;
                }
                var bucket = (int)(step.Percentage / 10);
                if (bucket <= lastBucket) return;
                lastBucket = bucket;
                detailsStatus.Text = $"Downloading package, {Math.Min(bucket * 10, 100)} percent.";
            });
            var host = new DependencyDialogHost(this, message => detailsStatus.Text = message, logger);
            await new LinuxModInstaller(httpClient, logger).InstallAsync(mod, release,
                account, proton, update, host,
                patreon, localPackagePath, progress);
            downloadProgress.IsVisible = false;
            var playInstruction = release.TargetPlatform == ReleaseTarget.XivLauncher
                ? "Reopen Steam and press Play for FINAL FANTASY XIV Online. XIVLauncher will complete its first-time setup."
                : mod.Game.EffectiveSteamAppId is not null
                ? "Press Play in Steam to check this mod."
                : "Launch the native game to check this mod.";
            await UpdateInstalledStateAsync(mod, update
                ? "Update complete. " + playInstruction
                : "Install complete. " + playInstruction);
        }
        catch (Exception ex) { ReportError(ex, detailsStatus); }
        finally { downloadProgress.IsVisible = false; busy = false; }
    }

    private async Task UninstallAsync(LinuxModEntry mod, TextBlock detailsStatus)
    {
        if (busy) return;
        busy = true;
        try
        {
            SteamAccountConfig? account = null;
            string? proton = null;
            if (mod.InstalledTarget == ReleaseTarget.XivLauncher)
            {
                if (!await EnsureSteamClosedAsync(mod)) return;
            }
            else if (mod.InstalledTarget != ReleaseTarget.Linux)
            {
                var selection = await ChooseSetupAsync(mod);
                if (selection is null) return;
                account = selection.Value.Account;
                proton = selection.Value.Proton;
                if (!await EnsureSteamClosedAsync(mod)) return;
            }
            await new LinuxModInstaller(httpClient, logger).UninstallAsync(mod,
                account, proton);
            await UpdateInstalledStateAsync(mod,
                mod.InstalledTarget == ReleaseTarget.Linux
                    ? "Uninstall complete. The native game files were restored."
                    : "Uninstall complete. The previous Steam launch option was restored.");
        }
        catch (Exception ex) { ReportError(ex, detailsStatus); }
        finally { busy = false; }
    }

    private async Task<bool> EnsureSteamClosedAsync(LinuxModEntry mod)
    {
        if (!SteamShutdownService.IsSteamRunning()) return true;
        if (!await ConfirmationDialog.ShowAsync(this, "Close Steam?",
                "The manager needs Steam closed while it changes this game's launch settings. " +
                "Close any running Steam game first. Should the manager ask Steam to close, " +
                "then continue automatically? You can reopen Steam when the operation finishes.",
                "Close Steam and continue")) return false;
        var steamRoot = mod.Install?.SteamRootPath ??
            throw new InvalidOperationException("Steam did not report its installation folder.");
        activeDetailsStatus!.Text = "Waiting for Steam to close.";
        await new SteamShutdownService().StopAsync(steamRoot);
        return true;
    }

    private async Task ImportExistingSetupAsync(LinuxModEntry mod)
    {
        if (busy || mod.Install is null || mod.OtherSetup is null) return;
        var oldId = mod.OtherSetup.GameId;
        if (!await ConfirmationDialog.ShowAsync(this, "Import existing installation",
                $"Import this author's existing Proton installation from catalog ID {oldId} " +
                $"to {mod.Game.GameId}? The manager will back up both records. " +
                "Your game files, Steam launch option and Proton prefix will not be changed.",
                "Import installation")) return;
        busy = true;
        var importedRecord = false;
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AccessibilityModManager");
            var backup = await new ProtonSetupIdentityMigration(Path.Combine(root, "proton-setups"),
                new ReceiptStore(logger), logger).ImportAsync(mod.Install, mod.OtherSetup);
            logger.Information("Existing installation record backup: {Backup}", backup);
            importedRecord = true;
        }
        catch (Exception ex) { ReportError(ex, activeDetailsStatus); }
        finally { busy = false; }
        if (!importedRecord) return;
        await RefreshModsAsync(focusList: false);
        if (catalog?.Mods.FirstOrDefault(item => item.Author.Id == mod.Author.Id &&
                item.Game.GameId == mod.Game.GameId) is { } imported)
            ShowModDetails(imported, completionMessage:
                "Existing Proton installation imported. Keep launching it from Steam. " +
                "A verified Proton release is needed before updating this mod on Linux.");
    }

    private async Task UpdateInstalledStateAsync(LinuxModEntry mod, string message)
    {
        if (catalog is null) return;
        var receipt = await new ReceiptStore(logger).LoadAsync(mod.Game.GameId, mod.Author.Id);
        var setups = mod.Install is null || string.IsNullOrWhiteSpace(mod.Game.EffectiveSteamAppId)
            ? [] : ProtonSteamSetupLookup.Find(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AccessibilityModManager", "proton-setups"),
                mod.Game.EffectiveSteamAppId, mod.Install.InstallPath);
        var ownSetup = setups.FirstOrDefault(state =>
            state.GameId == mod.Game.GameId && state.PluginId == mod.Author.Id);
        var updated = mod with
        {
            InstalledVersion = receipt?.InstalledVersion,
            InstalledTarget = receipt?.TargetPlatform,
            OwnSetup = ownSetup
        };
        catalog = catalog with { Mods = catalog.Mods.Select(item =>
            item.Author.Id == mod.Author.Id && item.Game.GameId == mod.Game.GameId ? updated : item).ToArray() };
        currentMod = updated;
        FilterMods();
        ShowModDetails(updated, detailsFromAuthor, message);
    }

    private async Task BrowseForGameAsync(LinuxModEntry mod)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose " + mod.Game.DisplayName + " installation folder",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            var path = folders[0].TryGetLocalPath() ??
                throw new InvalidOperationException("Choose a local game folder.");
            if (!new GameVerifier(logger).VerifyInstallPath(mod.Game, path))
                throw new InvalidOperationException("That folder does not match the game files in this catalog.");
            await new ConfigService(logger).UpdateAsync(config => config.KnownGameOverrides[mod.Game.GameId] = path);
            await RefreshModsAsync(focusList: false);
            var updated = catalog?.Mods.FirstOrDefault(item =>
                item.Author.Id == mod.Author.Id && item.Game.GameId == mod.Game.GameId);
            if (updated is not null)
                ShowModDetails(updated, detailsFromAuthor, "Game folder saved and verified.");
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private sealed record GameInstallerChoice(Dependency Dependency)
    {
        public override string ToString() => Dependency.Id;
    }

    private async Task InstallGameAsync(LinuxModEntry mod)
    {
        if (busy) return;
        busy = true;
        string? installedPath = null;
        try
        {
            var choices = NativeGameInstaller.Available(mod.Game).Select(dependency => new GameInstallerChoice(dependency)).ToArray();
            var selected = choices.Length == 1 ? choices[0] : await ChoiceDialog.ChooseAsync(this,
                "Game or emulator installer", "Choose the Linux installer to use.", choices);
            if (selected is null) return;
            if (!await ConfirmationDialog.ShowAsync(this, "Install game or emulator",
                    $"Download {selected.Dependency.Id} for {mod.Game.DisplayName} from {selected.Dependency.Fix?.DownloadUrl}? " +
                    "Choose the installation folder next. Files will be extracted directly there. " +
                    "Any replaced files will be backed up; other files will be kept.",
                    "Choose installation location")) return;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose where to install " + mod.Game.DisplayName, AllowMultiple = false
            });
            if (folders.Count == 0) return;
            var destination = folders[0].TryGetLocalPath() ?? throw new InvalidOperationException("Choose a local folder.");
            var status = activeDetailsStatus ?? modsStatus;
            status.Text = "Downloading and verifying the Linux game or emulator.";
            status.Focus();
            var host = new DependencyDialogHost(this, message => status.Text = message, logger);
            var dependencyInstaller = new DependencyAutoInstaller(httpClient, new DependencyReceiptStore(logger), logger);
            installedPath = await new NativeGameInstaller(dependencyInstaller).InstallAsync(mod.Game,
                selected.Dependency, destination, host);
            await new ConfigService(logger).UpdateAsync(config =>
            {
                config.KnownGameOverrides[mod.Game.GameId] = installedPath;
                config.InstalledEmulators["linux:" + mod.Game.LinuxExeName] = installedPath;
            });
        }
        catch (Exception ex)
        {
            ReportError(installedPath is null ? ex : new InvalidOperationException(
                $"The files were installed to {installedPath}, but saving their location failed. Use Browse for Folder to select that folder.", ex));
            installedPath = null;
        }
        finally { busy = false; }
        if (installedPath is null) return;
        await RefreshModsAsync(focusList: false);
        var updated = catalog?.Mods.FirstOrDefault(item => item.Author.Id == mod.Author.Id && item.Game.GameId == mod.Game.GameId);
        if (updated is not null)
            ShowModDetails(updated, detailsFromAuthor, "Game or emulator installed. You can now install a compatible mod release.");
    }

    private async Task<SteamAccountConfig?> ChooseXivAccountAsync(LinuxModEntry mod)
    {
        if (mod.Install is null) throw new InvalidOperationException("Install FINAL FANTASY XIV Online through Steam first.");
        _ = XivLauncherPaths.Resolve(mod.Install);
        var accounts = LinuxModInstaller.FindAccounts(mod.Install);
        if (accounts.Count == 0) throw new InvalidOperationException("No Steam account settings were found. Open Steam once first.");
        return accounts.Count == 1 ? accounts[0] : await ChoiceDialog.ChooseAsync(this,
            "Steam account", "Choose the Steam account you use for XIV.", accounts);
    }

    private async Task<(SteamAccountConfig Account, string Proton)?> ChooseSetupAsync(LinuxModEntry mod)
    {
        if (mod.Install is null)
            throw new InvalidOperationException("Steam did not detect this game. Install it through Steam first.");
        var accounts = LinuxModInstaller.FindAccounts(mod.Install);
        if (accounts.Count == 0) throw new InvalidOperationException("No Steam account settings file was found.");
        SteamAccountConfig account;
        if (accounts.Count == 1) account = accounts[0];
        else
        {
            var picked = await ChoiceDialog.ChooseAsync(this, "Steam account",
                "Choose the Steam account you use for this game.", accounts);
            if (picked is null) return null;
            account = picked;
        }
        var versions = LinuxModInstaller.FindProtonVersions(mod.Install);
        if (versions.Count == 0)
            throw new InvalidOperationException("No Proton executable was found. Install Proton in Steam first.");
        string proton;
        if (versions.Count == 1) proton = versions[0];
        else
        {
            var picked = await ChoiceDialog.ChooseAsync(this, "Proton version",
                "Choose the Proton version Steam uses for this game.", versions);
            if (picked is null) return null;
            proton = picked;
        }
        return (account, proton);
    }

    private async Task PlayGameAsync(LinuxModEntry mod)
    {
        if (busy || mod.Install is null) return;
        busy = true;
        try
        {
            var status = activeDetailsStatus ?? modsStatus;
            var catalogNotice = "";
            if (mod.InstalledTarget == ReleaseTarget.XivLauncher)
                await new LinuxModInstaller(httpClient, logger).CreateXivLauncherSetup().EnsureReadyAsync(mod.Install);
            else if (mod.Game.Dependencies.Any(dep => dep.Fix?.AutoInstall is not null))
            {
                status.Text = "Checking for dependency updates...";
                var updater = new DependencyUpdates(new DependencyAutoInstaller(httpClient, new DependencyReceiptStore(logger), logger), logger);
                await updater.EnsureReadyToLaunchAsync(mod.Install, mod.InstalledTarget == ReleaseTarget.Linux ? ReleaseTarget.Linux : ReleaseTarget.Proton);
                var fetched = await new PluginRepoClient(httpClient, logger, responseDeadlineOverride: TimeSpan.FromSeconds(5)).FetchPluginIndexAsync(mod.Author.Source);
                if (fetched.FromCache)
                    catalogNotice = "Live dependency updates could not be checked. Using the installed files. ";
                else
                {
                    var definition = fetched.Value.Games.SingleOrDefault(game => game.GameId == mod.Game.GameId)
                        ?? throw new InvalidOperationException("This game is no longer in its author's catalog. Refresh Mods.");
                    var game = new GameInstall { Game = definition, PluginId = mod.Author.Id,
                        InstallPath = mod.Install.InstallPath, IsValid = true };
                    var target = mod.InstalledTarget == ReleaseTarget.Linux ? ReleaseTarget.Linux : ReleaseTarget.Proton;
                    var pending = await updater.FindAsync(game, target);
                    if (pending.Count > 0)
                    {
                        var message = "Updated dependencies are available: " + string.Join(", ", pending.Select(update => update.Dependency.Id)) +
                            ". Update before launching " + mod.Game.DisplayName + "? Close any other running copy first. " +
                            "Replaced emulator files will be backed up. Files outside the update are kept.";
                        if (pending.Any(update => update.PreviouslyUntracked))
                            message += " An older installation has no recorded download version; this one-time update will establish it.";
                        if (!await ConfirmationDialog.ShowAsync(this, "Update dependencies before playing", message, "Update and play"))
                        { status.Text = "Launch cancelled."; return; }
                        var host = new DependencyDialogHost(this, text => status.Text = text, logger);
                        var progress = new Progress<ProgressInfo>(step => status.Text = step.StatusText);
                        await updater.ApplyAsync(game, target, pending, host, progress, default);
                    }
                }
            }
            PlayGame(mod);
            status.Text = catalogNotice + "Launching " + mod.Game.DisplayName + ".";
        }
        finally { busy = false; }
    }

    private static void PlayGame(LinuxModEntry mod)
    {
        if (mod.Game.EffectiveSteamAppId is { } appId && appId.All(char.IsAsciiDigit))
        {
            Process.Start(new ProcessStartInfo("xdg-open", $"steam://rungameid/{appId}")
                { UseShellExecute = false });
            return;
        }
        if (mod.InstalledTarget == ReleaseTarget.Linux && mod.Install is { } install &&
            !string.IsNullOrWhiteSpace(mod.Game.LinuxExeName))
        {
            var executable = PathSafety.CombineContained(install.InstallPath,
                mod.Game.LinuxExeName.Replace('\\', '/'));
            if (!File.Exists(executable))
                throw new FileNotFoundException("The native Linux game executable is missing.", executable);
            Process.Start(new ProcessStartInfo(executable)
                { WorkingDirectory = install.InstallPath, UseShellExecute = false });
        }
    }

    private void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false }); }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task ShowTextDialogAsync(string title, string text)
    {
        var dialog = new Window { Title = title, Width = 600, Height = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        var body = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 300 };
        root.Children.Add(body);
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Left };
        close.Click += (_, _) => dialog.Close();
        root.Children.Add(close);
        dialog.Content = root;
        dialog.Opened += (_, _) => body.Focus();
        await dialog.ShowDialog(this);
    }

    private void ShowMessage(string message, bool focus = false)
    {
        modsStatus.Text = message;
        AutomationProperties.SetName(modsStatus, "Status: " + message);
        if (focus) modsStatus.Focus();
    }

    private void ReportError(Exception ex, TextBlock? target = null)
    {
        logger.Error(ex, "Linux manager action failed");
        target ??= page.Content == tabs && tabs.SelectedIndex == 2 ? settingsStatus : activeDetailsStatus;
        if (target is null) ShowMessage(ex.Message, focus: true);
        else { target.Text = ex.Message; target.Focus(); }
    }

    private static ILogger CreateLogger()
    {
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibilityModManager", "logs");
        Directory.CreateDirectory(logs);
        return new LoggerConfiguration().MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logs, "linux-manager-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();
    }

    private sealed record AuthorRow(LinuxAuthorEntry Author)
    {
        public override string ToString() => Author.Author;
    }
}
