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
    private readonly IConfigService _configService;
    private readonly ILogger _logger;
    private readonly Action<PluginEntry>? _navigateToDeveloperDetails;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    public PluginsViewModel(
        IPluginRegistryClient registryClient,
        IConfigService configService,
        ILogger logger,
        Action<PluginEntry>? navigateToDeveloperDetails = null)
    {
        _registryClient = registryClient;
        _configService = configService;
        _logger = logger;
        _navigateToDeveloperDetails = navigateToDeveloperDetails;
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

        try
        {
            var config = await _configService.LoadAsync();
            var registry = await _registryClient.FetchRegistryAsync(new Uri(config.PluginRegistryUrl), ct);

            // Every registry-listed plugin is active. We don't expose a per-plugin enable/
            // disable because (a) every plugin already had to clear registry-side review and
            // (b) a "disabled but visible" state is just confusing UX. Sort by name.
            Plugins.Clear();
            foreach (var entry in registry.Plugins.OrderBy(p => p.Name))
            {
                Plugins.Add(new PluginItemViewModel(entry, _logger));
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
    private readonly ILogger _logger;

    public PluginEntry Entry { get; }

    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Author => Entry.Author;
    public string Description => Entry.Description;
    public Uri? Website => Entry.Website;
    public Dictionary<string, Uri> Links => Entry.Links;
    public bool HasLinks => Website != null || Links.Count > 0;

    public PluginItemViewModel(PluginEntry entry, ILogger logger)
    {
        Entry = entry;
        _logger = logger;
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
