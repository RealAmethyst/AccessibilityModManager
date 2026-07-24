using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Config persistence with crash resilience: saves are atomic (temp file + rename) and every
/// successful save also refreshes a <c>.bak</c> copy. A corrupt main file is preserved as
/// <c>.corrupt</c>, the backup is tried next, and only then do defaults apply — with
/// <see cref="LastLoadProblem"/> set so the app can tell the user instead of silently losing
/// their game locations, emulator records, and filters.
/// </summary>
public sealed class ConfigService : IConfigService
{
    private static readonly string DefaultConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager");

    private readonly string _configDirectory;
    private string ConfigFilePath => Path.Combine(_configDirectory, "config.json");
    private string BackupFilePath => ConfigFilePath + ".bak";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;

    public ConfigService(ILogger logger, string? configDirectoryOverride = null)
    {
        _logger = logger;
        _configDirectory = configDirectoryOverride ?? DefaultConfigDirectory;
    }

    // Sticky: once a recovery happened, the problem stays reported until the app acknowledges it
    // after telling the user. Clearing it on every load would let a later clean load (there are
    // many per session, from any view model) race the startup check and swallow the warning.
    public string? LastLoadProblem { get; private set; }

    public void AcknowledgeLoadProblem() => LastLoadProblem = null;

    public async Task<AppConfig> LoadAsync()
    {
        if (!File.Exists(ConfigFilePath))
        {
            // First run (or the user deliberately deleted it) — only worth reporting if a backup
            // existed but was itself unreadable (see TryLoadBackupOrDefaults).
            _logger.Information("No config file found at {Path}, using defaults", ConfigFilePath);
            return TryLoadBackupOrDefaults(mainWasMissing: true);
        }

        try
        {
            var json = await File.ReadAllTextAsync(ConfigFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                ?? throw new JsonException("Config deserialized to null");
            _logger.Information("Config loaded from {Path}", ConfigFilePath);
            return config;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load config from {Path}", ConfigFilePath);
            try
            {
                File.Copy(ConfigFilePath, ConfigFilePath + ".corrupt", overwrite: true);
                _logger.Error("Preserved unreadable config as {Path}", ConfigFilePath + ".corrupt");
            }
            catch (Exception copyEx)
            {
                _logger.Warning(copyEx, "Couldn't preserve corrupt config copy");
            }
            return TryLoadBackupOrDefaults(mainWasMissing: false);
        }
    }

    private AppConfig TryLoadBackupOrDefaults(bool mainWasMissing)
    {
        var backupExisted = File.Exists(BackupFilePath);
        if (backupExisted)
        {
            try
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(BackupFilePath), JsonOptions);
                if (config != null)
                {
                    if (!mainWasMissing)
                    {
                        LastLoadProblem =
                            "The settings file was unreadable, so the last good backup was restored. " +
                            "Recent changes (since that backup) may be missing.";
                    }
                    _logger.Warning("Config restored from backup {Path}", BackupFilePath);
                    return config;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Config backup at {Path} is also unreadable", BackupFilePath);
            }
        }

        // A missing main file is normal on first run — but a backup that existed and couldn't be
        // used means settings really were lost, and that must never be silent.
        if (!mainWasMissing || backupExisted)
        {
            LastLoadProblem =
                "The settings file was unreadable and no usable backup existed, so settings were reset to defaults. " +
                "Manually located games and emulator records may need to be set up again. " +
                "Any unreadable file was kept next to the settings with a '.corrupt' name.";
        }
        return new AppConfig();
    }

    public async Task SaveAsync(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(_configDirectory);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            await AtomicJson.WriteAtomicAsync(ConfigFilePath, Encoding.UTF8.GetBytes(json));

            // The freshly-written file is the new last-known-good — refresh the backup from it,
            // atomically, so an interrupted refresh can never leave a truncated backup that would
            // fail exactly when it's needed.
            try { await AtomicJson.WriteAtomicAsync(BackupFilePath, Encoding.UTF8.GetBytes(json)); }
            catch (Exception bakEx) { _logger.Warning(bakEx, "Couldn't refresh config backup"); }

            _logger.Information("Config saved to {Path}", ConfigFilePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save config to {Path}", ConfigFilePath);
            throw;
        }
    }
}
