using System.Diagnostics;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly ILogger _logger;

    [ObservableProperty]
    private string _pluginRegistryUrl = string.Empty;

    [ObservableProperty]
    private string _defaultChannel = "stable";

    [ObservableProperty]
    private string? _statusMessage;

    public SettingsViewModel(IConfigService configService, ILogger logger)
    {
        _configService = configService;
        _logger = logger;
    }

    [RelayCommand]
    private async Task LoadSettingsAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();
            PluginRegistryUrl = config.PluginRegistryUrl;
            DefaultChannel = config.DefaultChannel;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load settings");
            StatusMessage = $"Failed to load settings: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();
            config.PluginRegistryUrl = PluginRegistryUrl;
            config.DefaultChannel = DefaultChannel;
            await _configService.SaveAsync(config);
            StatusMessage = "Settings saved.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save settings");
            StatusMessage = $"Failed to save settings: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        var logDir = LoggingSetup.GetLogDirectory();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = logDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to open log directory");
            StatusMessage = $"Could not open logs folder: {ex.Message}";
        }
    }
}
