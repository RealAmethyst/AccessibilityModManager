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

    /// <summary>
    /// Blanks the status line before an action produces a new one.
    ///
    /// <para>The status line is announced by binding to its text, and an observable property
    /// suppresses an assignment equal to what is already there. So pressing Save twice would set
    /// "Settings saved." to the same value and say nothing the second time — the user gets silence
    /// in response to a button they just pressed. Clearing first guarantees a real change.</para>
    /// </summary>
    private void ClearStatusBeforeNewResult() => StatusMessage = null;

    [RelayCommand]
    private async Task SignInOrOutOfPatreonAsync()
    {
        if (PatreonBusy) return;
        PatreonBusy = true;
        ClearStatusBeforeNewResult();
        // Captured up front so the failure message names the action the user actually asked for.
        var wasSignedIn = _patreon.IsSignedIn;
        try
        {
            if (wasSignedIn)
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
            // The exception text goes to the log, never to the status line: these messages are
            // spoken now, and raw exception text is CLR type names, file paths and byte offsets.
            _logger.Error(ex, "Patreon sign-in/out failed");
            StatusMessage = wasSignedIn
                ? "Couldn't sign out of Patreon. Check the log for details."
                : "Couldn't sign in to Patreon. Check the log for details.";
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
        ClearStatusBeforeNewResult();
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
            StatusMessage = "Couldn't load your settings. Check the log for details.";
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        ClearStatusBeforeNewResult();
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
            StatusMessage = "Couldn't save your settings. Check the log for details.";
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        ClearStatusBeforeNewResult();
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
            StatusMessage = $"Couldn't open the logs folder. It's at {logDir}.";
        }
    }
}
