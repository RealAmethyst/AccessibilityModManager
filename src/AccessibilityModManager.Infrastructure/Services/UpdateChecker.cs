using System.Runtime.InteropServices;
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
    Uri ReleasePageUrl,
    string RuntimeIdentifier);

/// <summary>Discover an exact platform asset from the official release and verify its SHA-256 before use.</summary>
public sealed class UpdateChecker
{
    private const string Repository = "RealAmethyst/AccessibilityModManager";
    private static readonly Uri ReleasesApiUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private readonly HttpClient httpClient;
    private readonly ILogger logger;
    private readonly string runtimeIdentifier;
    public string? LastError { get; private set; }

    public UpdateChecker(HttpClient httpClient, ILogger logger, string? runtimeIdentifier = null)
    {
        this.httpClient = httpClient;
        this.logger = logger;
        this.runtimeIdentifier = runtimeIdentifier ?? CurrentRuntime;
    }

    public static string CurrentRuntime => RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "unsupported"
        : "unsupported";

    public static string AssetName(Version version, string runtime) => runtime switch
    {
        "win-x64" => $"AccessibilityModManager-{version}-Setup.exe",
        "linux-x64" => $"AccessibilityModManager-{version}-linux-x64.tar.gz",
        _ => throw new PlatformNotSupportedException("No manager update package is available for this platform.")
    };

    public async Task<UpdateInfo?> CheckForUpdateAsync(Version currentVersion, CancellationToken ct = default)
    {
        LastError = null;
        try
        {
            _ = AssetName(currentVersion, runtimeIdentifier);
            using var req = Request(ReleasesApiUrl);
            using var resp = await httpClient.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
                root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version.Build < 0)
                throw new InvalidDataException("The release tag is not a stable manager version.");
            if (Comparable(version) <= Comparable(currentVersion)) return null;

            var name = AssetName(version, runtimeIdentifier);
            var assets = root.GetProperty("assets").EnumerateArray().ToArray();
            var installers = assets.Where(a => a.GetProperty("name").GetString() == name).ToArray();
            var checksums = assets.Where(a => a.GetProperty("name").GetString() == name + ".sha256").ToArray();
            // A release can contain one platform only. Never fall back to another platform or product.
            if (installers.Length == 0 && checksums.Length == 0) return null;
            if (installers.Length != 1 || checksums.Length != 1)
                throw new InvalidDataException("The release must contain exactly one platform installer and its matching checksum.");
            var url = AssetUri(installers[0], tag, name);
            var hashUrl = AssetUri(checksums[0], tag, name + ".sha256");
            using var hashReq = Request(hashUrl);
            using var hashResp = await httpClient.SendAsync(hashReq, ct);
            hashResp.EnsureSuccessStatusCode();
            var hashText = (await hashResp.Content.ReadAsStringAsync(ct)).Trim().TrimStart('\ufeff');
            var tokens = hashText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length is < 1 or > 2 || tokens[0].Length != 64 || !tokens[0].All(Uri.IsHexDigit) ||
                tokens.Length == 2 && tokens[1].TrimStart('*') != name)
                throw new InvalidDataException("The release checksum is malformed or names a different installer.");
            var releasePage = new Uri($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}");
            var size = installers[0].TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            return new UpdateInfo(version, tag,
                root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                root.TryGetProperty("body", out var b) ? b.GetString() : null,
                url, tokens[0].ToLowerInvariant(), size > 0 ? size : null, releasePage, runtimeIdentifier);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            logger.Warning(ex, "Manager update check failed");
            return null;
        }
    }

    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (info.RuntimeIdentifier != runtimeIdentifier)
            throw new InvalidOperationException("The update is for a different operating system or architecture.");
        var name = AssetName(info.Version, runtimeIdentifier);
        RequireAssetUri(info.InstallerUrl, info.TagName, name);
        var directory = Directory.CreateTempSubdirectory("amm-update-");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory.FullName,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var target = Path.Combine(directory.FullName, name);
        try
        {
            using var resp = await httpClient.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = info.ContentLength ?? resp.Content.Headers.ContentLength;
            await using (var input = await resp.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long count = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    count += read;
                    if (count > 2L * 1024 * 1024 * 1024 || total is > 0 && count > total)
                        throw new InvalidDataException("The update download exceeds its expected size.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (total is > 0) progress?.Report((double)count / total.Value);
                }
                if (total is > 0 && count != total)
                    throw new InvalidDataException("The update download is incomplete.");
            }
            await using var verify = File.OpenRead(target);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, ct));
            if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update failed its SHA-256 check. It has not been installed.");
            return target;
        }
        catch { directory.Delete(true); throw; }
    }

    private static Version Comparable(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static HttpRequestMessage Request(Uri url)
    {
        UrlValidator.RequireHttps(url, "manager update");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("AccessibilityModManager-UpdateChecker");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    private static Uri AssetUri(JsonElement asset, string tag, string name)
    {
        var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
        RequireAssetUri(uri, tag, name);
        return uri;
    }

    private static void RequireAssetUri(Uri uri, string tag, string name)
    {
        var expected = new Uri($"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}");
        if (uri != expected)
            throw new InvalidDataException("The update asset does not belong to the expected official release and platform.");
    }
}
