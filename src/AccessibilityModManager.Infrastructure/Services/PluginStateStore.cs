using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed class PluginStateStore : IPluginStateStore
{
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager",
        "plugins");

    private static readonly string StateFilePath = Path.Combine(StateDirectory, "plugin-states.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;

    public PluginStateStore(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<List<PluginState>> LoadAllAsync()
    {
        if (!File.Exists(StateFilePath))
        {
            _logger.Information("No plugin state file found, returning empty list");
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(StateFilePath);
            var states = JsonSerializer.Deserialize<List<PluginState>>(json, JsonOptions);
            _logger.Information("Loaded {Count} plugin states", states?.Count ?? 0);
            return states ?? [];
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load plugin states, returning empty list");
            return [];
        }
    }

    public async Task SaveAsync(PluginState state)
    {
        var states = await LoadAllAsync();
        var existing = states.FindIndex(s => s.PluginId == state.PluginId);
        if (existing >= 0)
            states[existing] = state;
        else
            states.Add(state);

        try
        {
            Directory.CreateDirectory(StateDirectory);
            var json = JsonSerializer.Serialize(states, JsonOptions);
            await File.WriteAllTextAsync(StateFilePath, json);
            _logger.Information("Saved state for plugin {PluginId}", state.PluginId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save plugin state for {PluginId}", state.PluginId);
            throw;
        }
    }
}
