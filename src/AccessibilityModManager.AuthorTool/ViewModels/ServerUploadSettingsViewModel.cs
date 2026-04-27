using AccessibilityModManager.AuthorTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.AuthorTool.ViewModels;

/// <summary>
/// Modal-dialog VM for the "Server upload settings" form. Lets the author point the
/// AuthorTool at their Patreon-gate download server (host, user, key path, paths) and
/// run a quick connection test before saving. The settings persist to the existing
/// AuthorTool config file (no new storage), and getting them right is what flips the
/// auto-upload-on-Save flow on for Patreon-gated releases.
/// </summary>
public sealed partial class ServerUploadSettingsViewModel : ObservableObject
{
    private readonly AuthorConfigService _configService;
    private readonly ServerUploadService _uploadService;
    private readonly ILogger _logger;

    [ObservableProperty]
    private string _host = "";

    [ObservableProperty]
    private string _user = "";

    [ObservableProperty]
    private string _privateKeyPath = "";

    [ObservableProperty]
    private string _keyPassphrase = "";

    [ObservableProperty]
    private string _remoteBasePath = "/var/www/mod-server/releases";

    [ObservableProperty]
    private string _publicBaseUrl = "";

    [ObservableProperty]
    private int _port = 22;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    public Action? CloseDialog { get; set; }

    public ServerUploadSettingsViewModel(
        AuthorConfigService configService,
        ServerUploadService uploadService,
        ILogger logger)
    {
        _configService = configService;
        _uploadService = uploadService;
        _logger = logger;

        var existing = _configService.GetServerUploadConfig();
        if (existing != null)
        {
            _host = existing.Host;
            _user = existing.User;
            _privateKeyPath = existing.PrivateKeyPath;
            _keyPassphrase = existing.KeyPassphrase;
            _remoteBasePath = existing.RemoteBasePath;
            _publicBaseUrl = existing.PublicBaseUrl;
            _port = existing.Port == 0 ? 22 : existing.Port;
        }
        else
        {
            // Reasonable defaults so the first-time setup just needs the host/user/key.
            _publicBaseUrl = "";
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        StatusMessage = "Connecting...";
        try
        {
            var error = await _uploadService.TestConnectionAsync(BuildConfig(), CancellationToken.None);
            StatusMessage = error == null
                ? "Connection OK. Server is reachable and the remote path exists."
                : $"Failed: {error}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Server upload settings test threw");
            StatusMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        // Empty host = wipe the config (disables auto-upload). Otherwise persist as-is.
        if (string.IsNullOrWhiteSpace(Host))
        {
            _configService.SaveServerUploadConfig(null);
        }
        else
        {
            _configService.SaveServerUploadConfig(BuildConfig());
        }
        CloseDialog?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseDialog?.Invoke();

    private ServerUploadConfig BuildConfig() => new()
    {
        Host = Host?.Trim() ?? "",
        User = User?.Trim() ?? "",
        PrivateKeyPath = PrivateKeyPath?.Trim() ?? "",
        KeyPassphrase = KeyPassphrase ?? "",
        RemoteBasePath = RemoteBasePath?.Trim() ?? "",
        PublicBaseUrl = PublicBaseUrl?.Trim() ?? "",
        Port = Port == 0 ? 22 : Port
    };
}
