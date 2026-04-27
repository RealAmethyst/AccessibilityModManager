using System.Diagnostics;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Logging;
using AccessibilityModManager.Infrastructure.Patreon;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly PatreonService _patreon;
    private readonly ILogger _logger;

    [ObservableProperty]
    private string _defaultChannel = "stable";

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedInToPatreon))]
    [NotifyPropertyChangedFor(nameof(PatreonStatusText))]
    [NotifyPropertyChangedFor(nameof(SignInButtonText))]
    private bool _patreonStateChanged;

    [ObservableProperty]
    private bool _patreonBusy;

    public bool IsSignedInToPatreon => _patreon.IsSignedIn;

    public string SignInButtonText => IsSignedInToPatreon ? "Sign out of Patreon" : "Sign in to Patreon";

    public string PatreonStatusText
    {
        get
        {
            if (!_patreon.IsSignedIn)
                return "Not signed in. Sign in to see Patreon-gated mod releases.";
            var who = _patreon.CurrentAccount?.FullName ?? _patreon.CurrentAccount?.Email ?? "your Patreon account";
            var lastCheck = _patreon.CurrentAccount?.LastEntitlementCheck;
            var lastCheckText = lastCheck.HasValue
                ? $"last checked {(DateTime.UtcNow - lastCheck.Value).TotalMinutes:F0} minutes ago"
                : "not yet checked";
            return $"Signed in as {who}, {lastCheckText}.";
        }
    }

    public SettingsViewModel(IConfigService configService, PatreonService patreon, ILogger logger)
    {
        _configService = configService;
        _patreon = patreon;
        _logger = logger;

        // Mirror sign-in state changes from the service into our [ObservableProperty] so
        // CommunityToolkit's NotifyPropertyChangedFor wakes the bound view.
        _patreon.SignInStateChanged += OnPatreonStateChanged;
    }

    private void OnPatreonStateChanged() => PatreonStateChanged = !PatreonStateChanged;

    [RelayCommand]
    private async Task SignInOrOutOfPatreonAsync()
    {
        if (PatreonBusy) return;
        PatreonBusy = true;
        try
        {
            if (_patreon.IsSignedIn)
            {
                // Q6=A+B: also revoke on Patreon's side, best-effort.
                await _patreon.SignOutAsync(revokeOnPatreon: true, CancellationToken.None);
                StatusMessage = "Signed out of Patreon.";
            }
            else
            {
                await _patreon.SignInAsync(CancellationToken.None);
                await _patreon.RefreshEntitlementsAsync(CancellationToken.None);
                StatusMessage = "Signed in to Patreon.";
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Patreon sign-in/out failed");
            StatusMessage = $"Patreon sign-in failed: {ex.Message}";
        }
        finally
        {
            PatreonBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshPatreonStatusAsync()
    {
        if (PatreonBusy || !_patreon.IsSignedIn) return;
        PatreonBusy = true;
        try
        {
            var ok = await _patreon.RefreshEntitlementsAsync(CancellationToken.None);
            StatusMessage = ok
                ? $"Patreon status refreshed. {_patreon.CachedMemberships.Count} active memberships."
                : "Patreon refresh failed — you may need to sign in again.";
            PatreonStateChanged = !PatreonStateChanged;
        }
        finally
        {
            PatreonBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadSettingsAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();
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
