using System.Collections.ObjectModel;
using System.Diagnostics;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.App.ViewModels;

public partial class PluginsViewModel : ObservableObject
{
    private readonly IPluginRegistryClient _registryClient;
    private readonly IPluginStateStore _stateStore;
    private readonly IConfigService _configService;
    private readonly ILogger _logger;
    private readonly Action<PluginEntry>? _navigateToDeveloperDetails;

    /// <summary>
    /// Raised when the user toggles a plugin's enabled state. MainViewModel listens so it can
    /// invalidate the Games tab cache and force a re-detection on next visit.
    /// </summary>
    public event Action? PluginEnabledChanged;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    public PluginsViewModel(
        IPluginRegistryClient registryClient,
        IPluginStateStore stateStore,
        IConfigService configService,
        ILogger logger,
        Action<PluginEntry>? navigateToDeveloperDetails = null)
    {
        _registryClient = registryClient;
        _stateStore = stateStore;
        _configService = configService;
        _logger = logger;
        _navigateToDeveloperDetails = navigateToDeveloperDetails;
    }

    internal void NotifyPluginEnabledChanged() => PluginEnabledChanged?.Invoke();

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

        try
        {
            var config = await _configService.LoadAsync();
            var registry = await _registryClient.FetchRegistryAsync(new Uri(config.PluginRegistryUrl), ct);
            var states = await _stateStore.LoadAllAsync();
            var stateMap = states.ToDictionary(s => s.PluginId);

            Plugins.Clear();
            foreach (var entry in registry.Plugins.OrderByDescending(p => p.IsBuiltIn).ThenBy(p => p.Name))
            {
                stateMap.TryGetValue(entry.Id, out var state);
                var isEnabled = entry.IsBuiltIn || state?.IsEnabled != false;

                Plugins.Add(new PluginItemViewModel(entry, isEnabled, _stateStore, _logger, this));
            }

            StatusMessage = $"Loaded {Plugins.Count} developer{(Plugins.Count == 1 ? "" : "s")}.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Loading cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load developers");
            StatusMessage = $"Failed to load developers: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public partial class PluginItemViewModel : ObservableObject
{
    private readonly IPluginStateStore _stateStore;
    private readonly ILogger _logger;
    private readonly PluginsViewModel? _parent;

    public PluginEntry Entry { get; }

    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Author => Entry.Author;
    public string Description => Entry.Description;
    public bool IsBuiltIn => Entry.IsBuiltIn;
    public Uri? Website => Entry.Website;
    public Dictionary<string, Uri> Links => Entry.Links;
    public bool HasLinks => Website != null || Links.Count > 0;

    [ObservableProperty]
    private bool _isEnabled;

    public PluginItemViewModel(PluginEntry entry, bool isEnabled, IPluginStateStore stateStore, ILogger logger, PluginsViewModel? parent = null)
    {
        Entry = entry;
        _isEnabled = isEnabled;
        _stateStore = stateStore;
        _logger = logger;
        _parent = parent;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (IsBuiltIn) return;
        _ = SaveStateAsync(value);
        _parent?.NotifyPluginEnabledChanged();
    }

    private async Task SaveStateAsync(bool enabled)
    {
        try
        {
            await _stateStore.SaveAsync(new PluginState
            {
                PluginId = Id,
                IsEnabled = enabled
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save plugin state for {PluginId}", Id);
        }
    }

    [RelayCommand]
    private void OpenLink(Uri? uri)
    {
        if (uri == null) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to open link {Uri}", uri);
        }
    }

    public override string ToString() => Name;
}
