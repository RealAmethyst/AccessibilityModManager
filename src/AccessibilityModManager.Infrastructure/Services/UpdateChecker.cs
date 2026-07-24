using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string ReleaseName,
    string? ReleaseNotes,
    Uri InstallerUrl,
    string Sha256,
    long? ContentLength,
    Uri ReleasePageUrl);

/// <summary>
/// Checks the GitHub Releases API for a newer manager build, downloads the installer with
/// SHA256 verification, and hands it off to the OS to run. The running app exits so Inno's
/// upgrade flow can replace files. Public-key trust comes from HTTPS to api.github.com.
/// </summary>
public sealed class UpdateChecker
{
    private static readonly Uri ReleasesApiUrl =
        new("https://api.github.com/repos/RealAmethyst/AccessibilityModManager/releases/latest");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public UpdateChecker(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(Version currentVersion, CancellationToken ct = default)
    {
        try
        {
            UrlValidator.RequireHttps(ReleasesApiUrl, "GitHub releases API");

            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            // GitHub API requires a User-Agent header.
            req.Headers.UserAgent.ParseAdd("AccessibilityModManager-UpdateChecker");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await _httpClient.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var releaseName = root.TryGetProperty("name", out var n) ? n.GetString() ?? tagName : tagName;
            var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;

            if (!TryParseVersion(tagName, out var releaseVersion))
            {
                _logger.Warning("Could not parse release tag {Tag} as a version", tagName);
                return null;
            }

            if (releaseVersion <= currentVersion)
            {
                _logger.Information("Manager is up to date (running {Current}, latest {Latest})",
                    currentVersion, releaseVersion);
                return null;
            }

            // Find the .exe asset and the .sha256 sibling.
            string? exeUrl = null;
            string? sha256Url = null;
            long? exeSize = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    var url = asset.GetProperty("browser_download_url").GetString();
                    var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

                    // Match the installer naming convention so we don't pick up other assets
                    // shipped in the same release (e.g. the author tool exe).
                    if (name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exeUrl = url;
                        exeSize = size;
                    }
                    else if (name.EndsWith("-Setup.exe.sha256", StringComparison.OrdinalIgnoreCase))
                    {
                        sha256Url = url;
                    }
                }
            }

            if (string.IsNullOrEmpty(exeUrl) || string.IsNullOrEmpty(sha256Url))
            {
                _logger.Warning("Latest release {Tag} is missing an .exe asset or .sha256 sibling", tagName);
                return null;
            }

            // These come from the GitHub API JSON; enforce https before we ever fetch/execute them,
            // so the invariant is local and testable rather than relying on GitHub always returning https.
            UrlValidator.RequireHttps(exeUrl, "manager installer asset");
            UrlValidator.RequireHttps(sha256Url, "manager installer sha256 asset");

            // Fetch the SHA256 hash text up front — small, lets us bail early if it's malformed.
            using var hashReq = new HttpRequestMessage(HttpMethod.Get, sha256Url);
            hashReq.Headers.UserAgent.ParseAdd("AccessibilityModManager-UpdateChecker");
            using var hashResp = await _httpClient.SendAsync(hashReq, ct);
            hashResp.EnsureSuccessStatusCode();
            var sha256Text = (await hashResp.Content.ReadAsStringAsync(ct)).Trim();
            // Some publishers write "<hash>  filename"; take the first whitespace-delimited token.
            var firstToken = sha256Text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "";
            if (firstToken.Length != 64 || !firstToken.All(IsHexDigit))
            {
                _logger.Warning("Release SHA256 file content does not parse as a 64-char hex hash: {Content}", sha256Text);
                return null;
            }

            var info = new UpdateInfo(
                Version: releaseVersion,
                TagName: tagName,
                ReleaseName: releaseName,
                ReleaseNotes: body,
                InstallerUrl: new Uri(exeUrl, UriKind.Absolute),
                Sha256: firstToken.ToLowerInvariant(),
                ContentLength: exeSize > 0 ? exeSize : null,
                ReleasePageUrl: !string.IsNullOrEmpty(htmlUrl) ? new Uri(htmlUrl) : ReleasesApiUrl);

            _logger.Information("Update available: {Current} -> {Latest} ({Url})",
                currentVersion, info.Version, info.InstallerUrl);
            return info;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Update check failed");
            return null;
        }
    }

    public async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        UrlValidator.RequireHttps(info.InstallerUrl, "manager installer");

        var tempDir = Path.Combine(Path.GetTempPath(), "AccessibilityModManager-Update");
        Directory.CreateDirectory(tempDir);
        var fileName = Path.GetFileName(info.InstallerUrl.LocalPath);
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            fileName = $"AccessibilityModManager-{info.Version}-Setup.exe";
        var targetPath = Path.Combine(tempDir, fileName);

        if (File.Exists(targetPath)) File.Delete(targetPath);

        using (var resp = await _httpClient.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = info.ContentLength ?? resp.Content.Headers.ContentLength;

            await using var http = await resp.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(targetPath);
            var buffer = new byte[81920];
            long readSoFar = 0;
            int read;
            while ((read = await http.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                readSoFar += read;
                if (total is > 0)
                    progress?.Report(Math.Min(1.0, (double)readSoFar / total.Value));
            }
        }

        // SHA256 verify before we trust the file. Mismatch = abort and surface to caller.
        await using (var verify = File.OpenRead(targetPath))
        {
            var hash = await SHA256.HashDataAsync(verify, ct);
            var actualHex = Convert.ToHexString(hash).ToLowerInvariant();
            if (!string.Equals(actualHex, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(targetPath);
                throw new InvalidOperationException(
                    $"Downloaded installer hash mismatch. Expected {info.Sha256}, got {actualHex}. " +
                    "The download was deleted as a precaution.");
            }
        }

        _logger.Information("Update installer ready at {Path}", targetPath);
        return targetPath;
    }

    private static bool TryParseVersion(string tagName, out Version version)
    {
        // Accept "1.2.3", "v1.2.3", "v1.2.3-beta1" — strip leading 'v', drop pre-release suffix
        // for the comparison since System.Version doesn't model it.
        var s = tagName.TrimStart('v', 'V');
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out version!);
    }

    private static bool IsHexDigit(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
