using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>What reading a published asset told us. The three cases must not be conflated.</summary>
public enum PublishedAssetStatus
{
    /// <summary>It's there, and it was read to the end.</summary>
    Found,

    /// <summary>The server says there's nothing at that address (404/410).</summary>
    Absent,

    /// <summary>
    /// We couldn't tell — a network blip, a proxy error, an authentication demand, anything but a
    /// clean "not there". Never treated as absence: that conflation is how an overwrite gets waved
    /// through, and how a release that is merely gated reads as a broken address.
    /// </summary>
    Unreadable
}

/// <summary>
/// The outcome of one read. <paramref name="Sha256"/> is set only when hashing was asked for AND
/// the asset was found; <paramref name="Detail"/> always carries something worth logging or
/// showing, because the two failure shapes need different words in front of the author.
/// </summary>
public sealed record PublishedAssetResult(
    PublishedAssetStatus Status,
    string? Sha256,
    string Detail);

/// <summary>
/// Reads what a public download address actually serves. One implementation, shared by the release
/// dialog (which proves an upload landed) and the index editor (which proves the catalog's own
/// addresses answer once it is live), because these two used to disagree about what a failure was:
/// the dialog distinguished three outcomes and the editor collapsed everything into a bool.
/// </summary>
public sealed class PublishedAssetProbe
{
    // One handler for the process. The per-request timeout is set per call rather than on the
    // client, because the two callers want very different bounds: a quick availability poll and a
    // full streamed hash of a mod package are not the same request.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Reads <paramref name="url"/>, optionally hashing the body.
    ///
    /// <para>Availability and content are separated on purpose. Asking only "does it answer" is
    /// cheap enough to run over a whole catalog; hashing streams the package to compare what is
    /// served against what the index promises. Both bypass caches — a stale intermediary answering
    /// for the origin is precisely one of the faults being looked for.</para>
    /// </summary>
    public async Task<PublishedAssetResult> ReadAsync(
        Uri url, bool hash, TimeSpan timeout, CancellationToken ct)
    {
        using var perRequest = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perRequest.CancelAfter(timeout);

        try
        {
            var separator = string.IsNullOrEmpty(url.Query) ? "?" : "&";
            var busted = new Uri(url.AbsoluteUri + separator + "_=" + Guid.NewGuid().ToString("n"));

            using var response = await Http.GetAsync(
                busted, HttpCompletionOption.ResponseHeadersRead, perRequest.Token);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return new PublishedAssetResult(PublishedAssetStatus.Absent, null, $"HTTP {(int)response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                return new PublishedAssetResult(
                    PublishedAssetStatus.Unreadable, null, $"HTTP {(int)response.StatusCode}");
            }

            if (!hash)
                return new PublishedAssetResult(PublishedAssetStatus.Found, null, $"HTTP {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(perRequest.Token);
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, perRequest.Token));
            return new PublishedAssetResult(PublishedAssetStatus.Found, digest, $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up; that is not a verdict about the address.
            throw;
        }
        catch (OperationCanceledException)
        {
            return new PublishedAssetResult(
                PublishedAssetStatus.Unreadable, null, $"no answer within {timeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            return new PublishedAssetResult(PublishedAssetStatus.Unreadable, null, ex.Message);
        }
    }
}
