using System.IO;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// Uploads a wrapped ZIP + generated <c>gate.json</c> to the author's Patreon-gate download
/// server over SFTP. Authentication uses the same SSH private key the author already
/// configured for PuTTY / FileZilla (see <see cref="ServerUploadConfig.PrivateKeyPath"/>),
/// so there's no second secret to manage. All operations are non-destructive: if a release
/// already exists at the same path, the new file overwrites it (which is the intended
/// behaviour when an author re-publishes a release with the same version after a fix).
/// </summary>
public sealed class ServerUploadService
{
    private static readonly JsonSerializerOptions GateJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;

    public ServerUploadService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Quick reachability probe used by the "Test connection" button. Tries to authenticate
    /// and list <see cref="ServerUploadConfig.RemoteBasePath"/>. Returns null on success or
    /// a human-readable error message on failure. Only checks fields needed for SFTP — the
    /// public URL is upload-time only and isn't required to verify connectivity.
    /// </summary>
    public async Task<string?> TestConnectionAsync(ServerUploadConfig cfg, CancellationToken ct)
    {
        var validationError = ValidateForConnection(cfg);
        if (validationError != null) return validationError;

        return await Task.Run(() =>
        {
            try
            {
                using var sftp = OpenSftp(cfg);
                if (!sftp.Exists(cfg.RemoteBasePath))
                    return $"Remote base path '{cfg.RemoteBasePath}' doesn't exist on the server. " +
                           "Create it on the VPS first (see PATREON_VPS_SETUP.md section 6.7).";
                var entries = sftp.ListDirectory(cfg.RemoteBasePath).Count();
                _logger.Information("SFTP connection test ok — listed {Count} entries under {Path}",
                    entries, cfg.RemoteBasePath);
                return null;
            }
            catch (SshAuthenticationException ex)
            {
                _logger.Warning(ex, "SFTP authentication failed");
                return $"Authentication failed: {ex.Message}. Verify the user, key path, and that " +
                       $"the matching public key is in /home/{cfg.User}/.ssh/authorized_keys on the VPS.";
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "SFTP connection test failed");
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        }, ct);
    }

    /// <summary>
    /// Upload a wrapped ZIP plus a <c>gate.json</c> derived from the release's Patreon block.
    /// Lays them out at <c>{RemoteBasePath}/{gameId}/{version}/</c>. Throws on failure with
    /// a clear message — caller surfaces it via the existing <c>_showInfoDialog</c> path.
    /// </summary>
    public async Task UploadReleaseAsync(
        ServerUploadConfig cfg,
        string gameId,
        string version,
        string localZipPath,
        PatreonGate gate,
        CancellationToken ct)
    {
        var validationError = ValidateForUpload(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        if (!File.Exists(localZipPath))
            throw new FileNotFoundException("Wrapped ZIP not found.", localZipPath);

        var fileName = Path.GetFileName(localZipPath);
        var remoteFolder = JoinPosix(cfg.RemoteBasePath, gameId, version);
        var remoteZip = JoinPosix(remoteFolder, fileName);
        var remoteGate = JoinPosix(remoteFolder, "gate.json");

        await Task.Run(() =>
        {
            using var sftp = OpenSftp(cfg);

            // Make sure each path component exists. SFTP MakeDirectory only creates one
            // level at a time, so walk down from RemoteBasePath.
            EnsureRemoteDirectory(sftp, cfg.RemoteBasePath, gameId, version);

            // Upload the ZIP. SSH.NET's UploadFile streams from disk, so memory use is bounded
            // by the SFTP buffer (~32 KB). Large wrapped ZIPs (hundreds of MB) are fine.
            _logger.Information("Uploading {Local} to {Remote}", localZipPath, remoteZip);
            using (var src = File.OpenRead(localZipPath))
            {
                sftp.UploadFile(src, remoteZip, canOverride: true);
            }

            // Build gate.json from the release's Patreon block. Camel-cased to match the
            // schema the .NET download server reads.
            var gateJson = BuildGateJson(gate);
            using (var src = new MemoryStream(Encoding.UTF8.GetBytes(gateJson)))
            {
                sftp.UploadFile(src, remoteGate, canOverride: true);
            }

            _logger.Information("Upload complete: {Folder}", remoteFolder);
        }, ct);
    }

    /// <summary>
    /// Build the public download URL the manager hits for a given release. Used to write
    /// the <c>serverUrl</c> field into the index entry's Patreon block at save time.
    /// </summary>
    public static string BuildPublicUrl(
        ServerUploadConfig cfg, string gameId, string version, string fileName)
    {
        var trimmedBase = cfg.PublicBaseUrl.TrimEnd('/');
        return $"{trimmedBase}/{Uri.EscapeDataString(gameId)}/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(fileName)}";
    }

    private static SftpClient OpenSftp(ServerUploadConfig cfg)
    {
        // Read the key from disk every time — paths can change between calls and the user
        // could swap keys. SSH.NET's PrivateKeyFile accepts an optional passphrase.
        var key = string.IsNullOrEmpty(cfg.KeyPassphrase)
            ? new PrivateKeyFile(cfg.PrivateKeyPath)
            : new PrivateKeyFile(cfg.PrivateKeyPath, cfg.KeyPassphrase);
        var sftp = new SftpClient(cfg.Host, cfg.Port == 0 ? 22 : cfg.Port, cfg.User, key);
        sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        sftp.OperationTimeout = TimeSpan.FromMinutes(15); // tolerant of slow uploads of big ZIPs
        sftp.Connect();
        return sftp;
    }

    private static void EnsureRemoteDirectory(SftpClient sftp, params string[] parts)
    {
        var current = "";
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            current = string.IsNullOrEmpty(current) ? part : JoinPosix(current, part);
            if (!sftp.Exists(current))
            {
                sftp.CreateDirectory(current);
            }
        }
    }

    private static string JoinPosix(params string[] parts)
    {
        // Always forward slashes — SFTP paths are POSIX even when the client runs on Windows.
        return string.Join("/", parts.Select(p => p.Trim('/')))
            .Replace("//", "/")
            .TrimEnd('/')
            .Insert(0, parts[0].StartsWith('/') ? "/" : "");
    }

    private static string BuildGateJson(PatreonGate gate)
    {
        var dto = new GateDto(gate.CampaignId, gate.TierIds);
        return JsonSerializer.Serialize(dto, GateJsonOptions);
    }

    /// <summary>
    /// Connection-only validation. Skips the public URL — it's only consumed at upload
    /// time when we write the URL into the index, so requiring it for "Test connection"
    /// would block the user from verifying SFTP works before they've finalised the URL.
    /// </summary>
    private static string? ValidateForConnection(ServerUploadConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Host)) return "Server host is required.";
        if (string.IsNullOrWhiteSpace(cfg.User)) return "Server user is required.";
        if (string.IsNullOrWhiteSpace(cfg.PrivateKeyPath))
            return "SSH private key path is required.";
        if (!File.Exists(cfg.PrivateKeyPath))
            return $"SSH private key file not found at '{cfg.PrivateKeyPath}'.";
        if (string.IsNullOrWhiteSpace(cfg.RemoteBasePath))
            return "Remote releases path is required.";
        if (!cfg.RemoteBasePath.StartsWith('/'))
            return "Remote releases path must be an absolute POSIX path (start with '/').";
        return null;
    }

    /// <summary>
    /// Full validation for actually uploading a release. Connection fields plus the
    /// public URL the manager needs to download from.
    /// </summary>
    private static string? ValidateForUpload(ServerUploadConfig cfg)
    {
        var connectionError = ValidateForConnection(cfg);
        if (connectionError != null) return connectionError;
        if (string.IsNullOrWhiteSpace(cfg.PublicBaseUrl))
            return "Public download base URL is required.";
        if (!cfg.PublicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "Public download base URL must use https://.";
        return null;
    }

    private sealed record GateDto(string CampaignId, List<string> TierIds);
}
