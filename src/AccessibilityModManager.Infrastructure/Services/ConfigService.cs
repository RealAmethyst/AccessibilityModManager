using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed class ConfigService : IConfigService
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager");

    private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;

    public ConfigService(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<AppConfig> LoadAsync()
    {
        if (!File.Exists(ConfigFilePath))
        {
            _logger.Information("No config file found at {Path}, using defaults", ConfigFilePath);
            return new AppConfig();
        }

        try
        {
            var json = await File.ReadAllTextAsync(ConfigFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            _logger.Information("Config loaded from {Path}", ConfigFilePath);
            return config ?? new AppConfig();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load config from {Path}, using defaults", ConfigFilePath);
            return new AppConfig();
        }
    }

    public async Task SaveAsync(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            await File.WriteAllTextAsync(ConfigFilePath, json);
            _logger.Information("Config saved to {Path}", ConfigFilePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save config to {Path}", ConfigFilePath);
            throw;
        }
    }
}
