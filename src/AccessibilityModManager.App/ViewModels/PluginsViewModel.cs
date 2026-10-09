using System.Collections.ObjectModel;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.App.ViewModels;

public partial class PluginsViewModel : ObservableObject
{
    private readonly IPluginRegistryClient _registryClient;
    private readonly IConfigService _configService;
    private readonly IReceiptStore _receiptStore;
    private readonly UserSourceAdder _sourceAdder;
    private readonly PluginDirectoryClient? _directory;
    private IReadOnlyList<PluginDirectoryEntry> _directoryEntries = [];
    public ObservableCollection<SourceListItem> DirectorySources { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceActionLabel))]
    private SourceListItem? _selectedSource;
    public string SourceActionLabel => SelectedSource?.ActionLabel ?? "Add";
    private readonly ILogger _logger;
    private readonly Action<PluginEntry>? _navigateToDeveloperDetails;

    /// <summary>
    /// Shows the risk notice and returns whether the user accepted. Supplied by the app so this
    /// view model stays testable without a window.
    /// </summary>
    private readonly Func<SourcePreview, bool>? _confirmRisk;

    /// <summary>Confirms removing a source. Args: the source's name and how many mods it offers.</summary>
    private readonly Func<string, bool>? _confirmRemove;

    /// <summary>Raised after a source is added or removed, so the mods list re-reads its catalogs.</summary>
    public event Action? SourcesChanged;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Set only when the status is worth interrupting for. "Loaded 3 developers." is shown and left
    /// unspoken — it was one of three counts a single refresh read out.
    /// </summary>
    [ObservableProperty]
    private string? _statusAnnouncement;

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    public PluginsViewModel(
        IPluginRegistryClient registryClient,
        IConfigService configService,
        IReceiptStore receiptStore,
        UserSourceAdder sourceAdder,
        ILogger logger,
        Action<PluginEntry>? navigateToDeveloperDetails = null,
        Func<SourcePreview, bool>? confirmRisk = null,
        Func<string, bool>? confirmRemove = null,
        PluginDirectoryClient? directory = null)
    {
        _registryClient = registryClient;
        _configService = configService;
        _receiptStore = receiptStore;
        _sourceAdder = sourceAdder;
        _logger = logger;
        _navigateToDeveloperDetails = navigateToDeveloperDetails;
        _confirmRisk = confirmRisk;
        _confirmRemove = confirmRemove;
        _directory = directory;
    }

    private void RebuildSources(AppConfig config)
    {
        var selectedId = SelectedSource?.PluginId;
        DirectorySources.Clear();
        foreach (var source in SourceListItem.Build(_directoryEntries,
                     UserPluginSourceValidation.Accept(config.UserPluginSources).Accepted))
            DirectorySources.Add(source);
        SelectedSource = DirectorySources.FirstOrDefault(source => source.PluginId == selectedId)
            ?? DirectorySources.FirstOrDefault();
    }

    [RelayCommand]
    private async Task BrowseDirectoryAsync(CancellationToken ct)
    {
        if (_directory is null) return;
        IsLoading = true;
        try
        {
            RebuildSources(await _configService.LoadAsync());
            _directoryEntries = await _directory.ListAsync(ct);
            RebuildSources(await _configService.LoadAsync());
            Report($"Found {DirectorySources.Count} sources. Select a source and choose Add or Remove.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Report("Could not load available sources. Sources you already added remain listed. " + ex.Message); }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ManageSourceAsync(CancellationToken ct)
    {
        if (SelectedSource is not { } source)
        {
            Report("Select a source first.");
            return;
        }
        if (source.IsAdded)
            await RemoveSourceAsync(source);
        else
        {
            if (_directory is null) return;
            try { await _directory.RequireAvailableAsync(source.IndexUrl, ct); }
            catch (InvalidOperationException ex) { Report(ex.Message); return; }
            await AddSourceAsync(source.IndexUrl, ct);
        }
    }

    private async Task AddSourceAsync(string? address, CancellationToken ct)
    {
        if (_confirmRisk is null) return;

        if (string.IsNullOrWhiteSpace(address))
        {
            Report("Type the address of the source you want to add first.");
            return;
        }

        IsLoading = true;
        Report("Checking that source...");

        try
        {
            var config = await _configService.LoadAsync();
            var registry = (await _registryClient.FetchRegistryAsync(new Uri(config.PluginRegistryUrl), ct)).Value;
            var installed = await _receiptStore.InstalledPluginIdsAsync();

            var preview = await _sourceAdder.PreviewAsync(
                address, registry.Plugins, config.UserPluginSources, installed,
                config.KnownPluginAddresses, ct);

            if (!preview.CanAdd)
            {
                Report($"That source wasn't added. {preview.Refusal}");
                return;
            }

            if (!_confirmRisk(preview))
            {
                // Nothing was written, nothing was cached — cancelling leaves no trace of a source
                // the user decided against.
                Report("Cancelled. Nothing was added.");
                return;
            }

            // Re-checked INSIDE the lock. CanAdd ran before the notice, and the user may have spent
            // any amount of time reading it — another copy of the manager, or another window, can
            // have claimed the same developer id in between. Appending regardless would write a
            // duplicate the loader then refuses, and announce it as added.
            var committed = false;
            await _configService.UpdateAsync(c =>
            {
                var stillFree = CatalogSourceResolver.CanAdd(
                    registry.Plugins, c.UserPluginSources, installed, preview.PluginId,
                    preview.IndexUrl, c.KnownPluginAddresses) is null;
                if (!stillFree) return;

                c.UserPluginSources.Add(UserSourceAdder.Accept(preview, DateTimeOffset.UtcNow));
                c.KnownPluginGameIds.Remove(UserPluginSource.AcceptanceKey(preview.PluginId, preview.IndexUrl));

                // Remembered so this source can be removed and added back later: the installed-mods
                // reservation has to know this address is the one that created them.
                c.KnownPluginAddresses[preview.PluginId] = preview.IndexUrl;
                committed = true;
            });

            if (!committed)
            {
                Report($"That source wasn't added: something else started using the developer id " +
                       $"\"{preview.PluginId}\" while you were reading.");
                return;
            }

            SourcesChanged?.Invoke();

            // Reload FIRST, then speak. LoadPlugins writes its own status line, so reporting before
            // it would leave the user hearing "Loaded 3 developers" in place of the one thing they
            // pressed a button to find out.
            await LoadPluginsCommand.ExecuteAsync(null);
            Report($"Added {preview.DisplayName}.");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to add a source");
            Report("Couldn't add that source. " + CatalogRefusedException.SpeakableReason(ex));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Removes a source. Mods already installed from it stay installed and can still be uninstalled
    /// — removal stops updates and new installs, it does not touch anything on disk.
    /// </summary>
    private async Task RemoveSourceAsync(SourceListItem? source)
    {
        // Every exit from here says something. Silence after pressing a button is the worst outcome
        // on a screen reader: it is indistinguishable from success, and it is exactly what happened —
        // declining the confirmation returned without a word, so a removal that never ran looked
        // like one that had.
        if (source is null)
        {
            Report("Select the source you want to remove first.");
            return;
        }

        if (_confirmRemove is null)
        {
            Report("Couldn't remove that source.");
            return;
        }

        if (!_confirmRemove(source.DisplayName))
        {
            Report($"{source.DisplayName} was kept. Nothing was removed.");
            return;
        }

        try
        {
            await _configService.UpdateAsync(c =>
                c.UserPluginSources.RemoveAll(s =>
                    string.Equals(SafeId.Canonical(s.PluginId), SafeId.Canonical(source.PluginId),
                        StringComparison.OrdinalIgnoreCase)));

            SourcesChanged?.Invoke();
            await LoadPluginsCommand.ExecuteAsync(null);
            Report($"Removed {source.DisplayName}. Mods you already installed from it are still installed.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to remove source {PluginId}", source.PluginId);
            Report("Couldn't remove that source.");
        }
    }

    /// <summary>
    /// Shown AND spoken — these are all results of something the user just did.
    ///
    /// <para>The announcement is cleared first. Observable properties suppress a change notification
    /// when the value is equal, so pressing a button twice and getting the same answer would raise
    /// nothing the second time and the control would seem broken. Clearing re-arms it; the live
    /// region coalesces to the latest value, so the blank is never spoken.</para>
    /// </summary>
    private void Report(string message)
    {
        StatusMessage = message;
        StatusAnnouncement = null;
        StatusAnnouncement = message;
    }

    /// <summary>
    /// Triggered by Enter on the developers list. Opens a Developer Details view scoped to
    /// the selected plugin, listing only that developer's mods.
    /// </summary>
    [RelayCommand]
    private void OpenDeveloperDetails(PluginItemViewModel? plugin)
    {
        if (plugin == null) return;
        _navigateToDeveloperDetails?.Invoke(plugin.Entry);
    }

    [RelayCommand]
    private async Task LoadPluginsAsync(CancellationToken ct)
    {
        IsLoading = true;
        StatusMessage = "Loading developers...";
        StatusAnnouncement = null;

        try
        {
            var config = await _configService.LoadAsync();
            RebuildSources(config);
            var registryFetch = await _registryClient.FetchRegistryAsync(new Uri(config.PluginRegistryUrl), ct);
            var registry = registryFetch.Value;

            // Every registry-listed plugin is active. We don't expose a per-plugin enable/
            // disable because (a) every plugin already had to clear registry-side review and
            // (b) a "disabled but visible" state is just confusing UX. Sort by name.
            Plugins.Clear();
            foreach (var entry in registry.Plugins.OrderBy(p => p.Name))
            {
                // A failed link is the result of a button the user just pressed, so it speaks.
                Plugins.Add(new PluginItemViewModel(entry, _logger, msg =>
                {
                    StatusMessage = msg;
                    StatusAnnouncement = msg;
                }));
            }

            // Rebuilt from the ACCEPTED list, not the raw config, so a source the loader refused
            // never appears here as though it were working.
            var accepted = UserPluginSourceValidation.Accept(config.UserPluginSources);

            var summary = $"Loaded {Plugins.Count} developer{(Plugins.Count == 1 ? "" : "s")}.";
            if (accepted.Rejected.Count > 0)
            {
                summary = string.Join(" ",
                    accepted.Rejected.Select(r => $"The source {r.Describe} wasn't loaded because {r.Reason}.")) +
                    " " + summary;
                StatusAnnouncement = summary;
            }
            StatusMessage = registryFetch.FromCache
                ? $"Offline — showing the saved catalog from {CatalogStatus.FormatCachedAt(registryFetch.CachedAtUtc)}. {summary}"
                : summary;
            // Being offline matters; the count on its own does not.
            StatusAnnouncement = registryFetch.FromCache ? StatusMessage : null;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Loading cancelled.";  // they cancelled it; shown, not announced
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load developers");
            StatusMessage = "Couldn't load the developer list. " +
                            CatalogRefusedException.SpeakableReason(ex);
            StatusAnnouncement = StatusMessage;
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public partial class PluginItemViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly Action<string>? _reportStatus;

    public PluginEntry Entry { get; }

    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Author => Entry.Author;
    public Uri? Website => Entry.Website;
    public Dictionary<string, Uri> Links => Entry.Links;
    public bool HasLinks => Website != null || Links.Count > 0;

    public PluginItemViewModel(PluginEntry entry, ILogger logger, Action<string>? reportStatus = null)
    {
        Entry = entry;
        _logger = logger;
        _reportStatus = reportStatus;
    }

    [RelayCommand]
    private void OpenLink(Uri? uri)
    {
        // Registry entries are signed, but the launch path follows the same app-wide rule as
        // every other author-supplied link: https only, through the shared opener.
        if (uri == null) return;
        if (!ExternalLink.TryOpen(uri.AbsoluteUri, _logger))
            _reportStatus?.Invoke("Couldn't open that link in your browser — it may not be a safe https address, or no browser responded.");
    }

    public override string ToString() => Name;
}
