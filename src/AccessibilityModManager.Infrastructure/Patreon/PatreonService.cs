using System.Net.Http;
using System.Net.Http.Headers;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Patreon;

/// <summary>
/// Higher-level façade over <see cref="PatreonClient"/> for the manager. Tracks "is the user
/// signed in?", refreshes tokens transparently, exposes entitlement queries, and routes the
/// gated-release download path through the Patreon attachment fetch. The rest of the manager
/// only talks to this — it doesn't construct PatreonClient directly.
/// </summary>
public sealed class PatreonService
{
    private readonly PatreonClient _client;
    private readonly IPatreonAccountStore _store;
    private readonly IPatreonEntitlementCache _cache;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    private PatreonAccount? _currentAccount;
    private HashSet<string> _ownedCampaignIds = new(StringComparer.Ordinal);

    public PatreonService(
        PatreonClient client,
        IPatreonAccountStore store,
        IPatreonEntitlementCache cache,
        HttpClient http,
        ILogger logger)
    {
        _client = client;
        _store = store;
        _cache = cache;
        _http = http;
        _logger = logger;
    }

    /// <summary>True when the user has a stored token (regardless of expiry).</summary>
    public bool IsSignedIn => _currentAccount != null;

    public PatreonAccount? CurrentAccount => _currentAccount;
    public IReadOnlyList<PatreonMembership> CachedMemberships => _cache.Memberships;

    /// <summary>
    /// True when the signed-in user is the owner of <paramref name="campaignId"/>. Used by
    /// the install flow to skip Patreon download (which returns no URL for creators viewing
    /// their own paid posts) and offer a local-file path instead.
    /// </summary>
    public bool IsCampaignOwner(string campaignId) =>
        !string.IsNullOrEmpty(campaignId) && _ownedCampaignIds.Contains(campaignId);

    /// <summary>Raised when sign-in state changes so the UI can refresh.</summary>
    public event Action? SignInStateChanged;

    /// <summary>
    /// Loads any stored token into memory and refreshes both the owned-campaign list (for
    /// creator detection) AND the membership cache (for patron entitlement). Called once
    /// on app startup. Without the membership refresh here, gated releases would stay
    /// hidden after every restart until the user manually clicked Refresh — the cache is
    /// empty by default and <see cref="IsEntitled"/> returns false on an empty cache.
    /// Fires <see cref="SignInStateChanged"/> when memberships finish loading so views
    /// that already rendered with no entitlements (because they raced startup) re-render.
    /// </summary>
    public async Task LoadAsync()
    {
        _currentAccount = await _store.LoadAsync();
        if (_currentAccount == null) return;

        _logger.Information("Loaded Patreon session for user {UserId}", _currentAccount.UserId);

        try { await RefreshOwnedCampaignsAsync(CancellationToken.None); }
        catch (Exception ex) { _logger.Warning(ex, "Couldn't load owned-campaign list on startup"); }

        try
        {
            // Don't call RefreshEntitlementsAsync here — that would also fire
            // SignInStateChanged twice (once from this method, once from SignOutAsync if the
            // token is rejected). Inline the happy path so the failure case still signs out
            // cleanly via SignOutAsync but the success case fires SignInStateChanged exactly
            // once at the end.
            await EnsureFreshTokenAsync(CancellationToken.None);
            var (updated, memberships) = await _client.FetchIdentityAndMembershipsAsync(
                _currentAccount, CancellationToken.None);
            _currentAccount = updated;
            await _store.SaveAsync(updated);
            _cache.Set(memberships);
        }
        catch (PatreonUnauthorizedException)
        {
            _logger.Warning("Stored Patreon token rejected on startup; signing out");
            await SignOutAsync(revokeOnPatreon: false, CancellationToken.None);
            return;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't refresh entitlements on startup — gated releases " +
                                "may stay hidden until the user retries");
        }

        SignInStateChanged?.Invoke();
    }

    /// <summary>
    /// Asks Patreon for the list of campaigns the signed-in user owns, then caches the ids.
    /// Quiet failure — if the user authorized only the older patron-side scope set, this
    /// returns 403 / empty and the user just won't be flagged as a creator. They can sign
    /// out + back in with the new scope to fix that.
    /// </summary>
    private async Task RefreshOwnedCampaignsAsync(CancellationToken ct)
    {
        if (_currentAccount == null)
        {
            _ownedCampaignIds.Clear();
            return;
        }
        try
        {
            var campaign = await _client.FetchOwnCampaignAsync(_currentAccount, ct);
            var fresh = new HashSet<string>(StringComparer.Ordinal);
            if (campaign != null) fresh.Add(campaign.CampaignId);
            _ownedCampaignIds = fresh;
            if (campaign != null)
                _logger.Information("Detected creator session — owns campaign {CampaignId}", campaign.CampaignId);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Couldn't fetch owned campaigns (likely missing scope)");
            _ownedCampaignIds.Clear();
        }
    }

    public async Task SignInAsync(CancellationToken ct)
    {
        var account = await _client.SignInAsync(ct);
        _currentAccount = account;
        await _store.SaveAsync(account);
        _cache.Invalidate();
        await RefreshOwnedCampaignsAsync(ct);
        SignInStateChanged?.Invoke();
    }

    public async Task SignOutAsync(bool revokeOnPatreon, CancellationToken ct)
    {
        if (_currentAccount != null && revokeOnPatreon)
        {
            await _client.RevokeAsync(_currentAccount, ct);
        }
        await _store.ClearAsync();
        _currentAccount = null;
        _cache.Invalidate();
        _ownedCampaignIds.Clear();
        SignInStateChanged?.Invoke();
    }

    /// <summary>
    /// Refresh the entitlement set from Patreon. Q4=A says recheck on every install attempt;
    /// the cache is just a within-session optimization.
    /// </summary>
    public async Task<bool> RefreshEntitlementsAsync(CancellationToken ct)
    {
        if (_currentAccount == null) return false;
        try
        {
            await EnsureFreshTokenAsync(ct);
            var (updated, memberships) = await _client.FetchIdentityAndMembershipsAsync(_currentAccount!, ct);
            _currentAccount = updated;
            await _store.SaveAsync(updated);
            _cache.Set(memberships);
            await RefreshOwnedCampaignsAsync(ct);
            return true;
        }
        catch (PatreonUnauthorizedException)
        {
            _logger.Warning("Patreon token rejected after refresh; signing out");
            await SignOutAsync(revokeOnPatreon: false, ct);
            return false;
        }
    }

    /// <summary>
    /// True when the user is currently entitled to at least one tier in <paramref name="gate"/>'s
    /// allowlist. False when not signed in, not a patron of that campaign, or only entitled
    /// to non-matching tiers.
    /// </summary>
    public bool IsEntitled(PatreonGate gate)
    {
        var membership = _cache.Memberships.FirstOrDefault(m => m.CampaignId == gate.CampaignId);
        if (membership == null) return false;
        return membership.CurrentlyEntitledTierIds.Any(t => gate.TierIds.Contains(t));
    }

    /// <summary>
    /// Look up the attachment metadata for a gated release without downloading it. Returns
    /// the chosen attachment (filename + tier ids + maybe a URL) or null if the API didn't
    /// list any attachments. Used by the install flow to decide upfront whether the API can
    /// give us a download URL — when <see cref="PatreonPostAttachment.DownloadUrl"/> is null
    /// even for an entitled patron, we degrade to a manual "open post in browser, pick the
    /// file" flow because Patreon's current API doesn't return signed URLs to anyone for
    /// post attachments.
    /// </summary>
    public async Task<PatreonPostAttachment?> TryResolveAttachmentAsync(
        PatreonGate gate, CancellationToken ct)
    {
        if (_currentAccount == null) return null;
        if (string.IsNullOrEmpty(gate.PostId)) return null;
        await EnsureFreshTokenAsync(ct);
        var attachments = await _client.FetchPostAttachmentsAsync(_currentAccount, gate.PostId, ct);
        if (attachments.Count == 0) return null;
        if (!string.IsNullOrEmpty(gate.AttachmentFileName))
        {
            return attachments.FirstOrDefault(a =>
                string.Equals(a.FileName, gate.AttachmentFileName, StringComparison.OrdinalIgnoreCase));
        }
        return attachments[0];
    }

    /// <summary>
    /// Download a gated release from the author's own download server, passing the user's
    /// Patreon access token in <c>Authorization: Bearer ...</c>. The server validates the
    /// token against Patreon's API and only serves the file if the user is currently
    /// entitled to one of the gate's tiers — entitlement enforcement happens server-side
    /// because the manager can't be trusted to enforce it on its own. Same SHA256
    /// verification still happens after download. Streams to disk to avoid loading the
    /// whole ZIP into memory.
    /// </summary>
    public async Task DownloadFromServerAsync(
        string serverUrl,
        string destPath,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct)
    {
        if (_currentAccount == null)
            throw new InvalidOperationException(
                "Not signed in to Patreon — can't download gated release from author's server.");

        // Never send the patron's Patreon bearer token to a non-https endpoint. The server URL
        // comes from the (unsigned) plugin index, so an http:// value would leak the token.
        UrlValidator.RequireHttps(serverUrl, "Patreon author download server");

        await EnsureFreshTokenAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Get, serverUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _currentAccount.AccessToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Author's download server returned {(int)resp.StatusCode} ({resp.StatusCode}). " +
                (string.IsNullOrEmpty(body) ? "No response body." : $"Response: {body[..Math.Min(body.Length, 400)]}"));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var total = resp.Content.Headers.ContentLength;
        long downloaded = 0;

        await using var dest = File.Create(destPath);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, n), ct);
            downloaded += n;
            if (progress != null && total is > 0)
            {
                progress.Report(new ProgressInfo
                {
                    Percentage = (double)downloaded / total.Value * 100,
                    StatusText = $"Downloading from {new Uri(serverUrl).Host}... {downloaded / 1024:N0} / {total.Value / 1024:N0} KB",
                    StepDescription = "Downloading package"
                });
            }
        }
    }

    /// <summary>
    /// Fetch the Patreon-hosted attachment for a gated release and stream it to disk. When
    /// the gate names a specific <see cref="PatreonGate.AttachmentFileName"/>, that file is
    /// picked out of the post's attachment list (supports "one post per game, many files");
    /// otherwise the first attachment is used (back-compat with one-post-per-release).
    /// </summary>
    public async Task DownloadGatedReleaseAsync(
        PatreonGate gate, string destPath,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (_currentAccount == null)
            throw new InvalidOperationException("Not signed in to Patreon — can't download gated release.");
        if (string.IsNullOrEmpty(gate.PostId))
            throw new InvalidOperationException(
                "This release has no Patreon post id, so the Patreon-API download path doesn't apply. " +
                "It should be served from the author's download server instead.");

        await EnsureFreshTokenAsync(ct);

        var attachments = await _client.FetchPostAttachmentsAsync(_currentAccount, gate.PostId, ct);
        if (attachments.Count == 0)
            throw new InvalidOperationException(
                $"Couldn't find any attachments on Patreon post {gate.PostId} — was the file removed or the post unpublished?");

        PatreonPostAttachment chosen;
        if (!string.IsNullOrEmpty(gate.AttachmentFileName))
        {
            chosen = attachments.FirstOrDefault(a =>
                string.Equals(a.FileName, gate.AttachmentFileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Patreon post {gate.PostId} has no attachment named '{gate.AttachmentFileName}'. " +
                    "The author may have removed or renamed it.");
        }
        else
        {
            chosen = attachments[0];
        }

        if (chosen.DownloadUrl is null)
            throw new InvalidOperationException(
                $"Patreon returned attachment metadata for '{chosen.FileName}' on post {gate.PostId} but no download URL. " +
                "This usually means the signed-in account isn't entitled to the post's tier (or sign-in expired). " +
                "Sign in again and re-check your Patreon membership.");

        await _client.DownloadAttachmentAsync(_currentAccount, chosen.DownloadUrl, destPath, progress, ct);
    }

    private async Task EnsureFreshTokenAsync(CancellationToken ct)
    {
        if (_currentAccount == null) return;
        // Refresh proactively when within 5 minutes of expiry so we don't get caught
        // mid-call with a stale token.
        if (_currentAccount.ExpiresAt - DateTime.UtcNow > TimeSpan.FromMinutes(5)) return;

        try
        {
            var refreshed = await _client.RefreshAsync(_currentAccount, ct);
            _currentAccount = refreshed;
            await _store.SaveAsync(refreshed);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Patreon token refresh failed");
            throw;
        }
    }
}
