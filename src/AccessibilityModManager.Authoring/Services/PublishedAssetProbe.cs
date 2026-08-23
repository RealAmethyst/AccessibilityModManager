using System.Net;
using System.Security.Cryptography;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

public enum PublishedAssetStatus
{
    Found,
    Absent,
    Unreadable
}

public sealed record PublishedAssetState(PublishedAssetStatus Status, string? Sha256);

public interface IPublishedAssetProbe
{
    Task<PublishedAssetState> ProbeAsync(Uri url, CancellationToken ct = default);
}

public sealed class PublishedAssetProbe : IPublishedAssetProbe
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private readonly ILogger _logger;

    public PublishedAssetProbe(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PublishedAssetState> ProbeAsync(Uri url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Published asset probes require an https:// URL.");

        try
        {
            var separator = string.IsNullOrEmpty(url.Query) ? "?" : "&";
            var cacheBusted = new Uri(
                url.AbsoluteUri + separator + "_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var response = await Http.GetAsync(
                cacheBusted,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return new PublishedAssetState(PublishedAssetStatus.Absent, null);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning("Reading {Url} returned {Status}", url, response.StatusCode);
                return new PublishedAssetState(PublishedAssetStatus.Unreadable, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            return new PublishedAssetState(PublishedAssetStatus.Found, sha);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't read the published asset at {Url}", url);
            return new PublishedAssetState(PublishedAssetStatus.Unreadable, null);
        }
    }
}
