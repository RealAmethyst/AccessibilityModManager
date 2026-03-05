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

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    public PluginsViewModel(
        IPluginRegistryClient registryClient,
        IPluginStateStore stateStore,
        IConfigService configService,
        ILogger logger)
    {
        _registryClient = registryClient;
        _stateStore = stateStore;
        _configService = configService;
        _logger = logger;
    }

    [RelayCommand]
    private async Task LoadPluginsAsync(CancellationToken ct)
    {
        IsLoading = true;
        StatusMessage = "Loading plugins...";

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

                Plugins.Add(new PluginItemViewModel(entry, isEnabled, _stateStore, _logger));
            }

            StatusMessage = $"Loaded {Plugins.Count} plugins.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Plugin loading cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load plugins");
            StatusMessage = $"Failed to load plugins: {ex.Message}";
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

    public PluginItemViewModel(PluginEntry entry, bool isEnabled, IPluginStateStore stateStore, ILogger logger)
    {
        Entry = entry;
        _isEnabled = isEnabled;
        _stateStore = stateStore;
        _logger = logger;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (IsBuiltIn) return;
        _ = SaveStateAsync(value);
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
}
