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

    /// <summary>
    /// Bumped by every sign-out. Network calls capture it before awaiting and refuse to commit
    /// their result if it moved while they were in flight. Startup's session load is
    /// fire-and-forget, so without this a response that landed just after the user signed out
    /// would put the account back in memory AND write the token back to disk — undoing the
    /// sign-out, and surviving a restart.
    /// </summary>
    private int _sessionGeneration;

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
        // Captured before the first await, so a sign-out at any point during startup is seen —
        // including one landing between loading the token and using it.
        var generation = _sessionGeneration;

        // Into a local first: assigning straight to the field would re-populate it (and so report
        // the user as signed in) if a sign-out happened while the store was being read.
        var loaded = await _store.LoadAsync();
        if (loaded == null || generation != _sessionGeneration) return;
        _currentAccount = loaded;

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
            if (generation != _sessionGeneration || _currentAccount == null) return;

            var (updated, memberships) = await FetchIdentityWithOneRetryAsync(
                generation, CancellationToken.None);
            if (!await TryCommitAccountAsync(generation, updated)) return;
            _cache.Set(memberships);

            // Ownership is fetched again HERE, after the identity call has settled on a usable
            // token. The earlier attempt above can 401 on a stale token and quietly clear the
            // owned-campaign set, and a creator with no ownership recorded sees their own gated
            // releases treated as a patron's — hidden — until they refresh by hand.
            await RefreshOwnedCampaignsAsync(CancellationToken.None);
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
            // Deliberately keeps whatever was known before: a probe that couldn't complete says
            // nothing about whether this user owns a campaign, and wiping on failure meant one
            // flaky call made a creator's own gated releases vanish from their view.
            _logger.Debug(ex, "Couldn't fetch owned campaigns (likely missing scope or a transient error)");
        }
    }

    public async Task SignInAsync(CancellationToken ct)
    {
        var account = await _client.SignInAsync(ct);
        _sessionGeneration++;
        _currentAccount = account;
        await _store.SaveAsync(account);
        _cache.Invalidate();
        await RefreshOwnedCampaignsAsync(ct);
        SignInStateChanged?.Invoke();
    }

    public async Task SignOutAsync(bool revokeOnPatreon, CancellationToken ct)
    {
        // Invalidate BEFORE any await: anything already in flight must not be able to commit.
        _sessionGeneration++;

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
            var generation = _sessionGeneration;
            var (updated, memberships) = await FetchIdentityWithOneRetryAsync(generation, ct);
            if (!await TryCommitAccountAsync(generation, updated)) return false;
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

        // A download can be in flight across a sign-out too, and its token refresh must not
        // resurrect the session any more than startup's can.
        await EnsureFreshTokenAsync(_sessionGeneration, ct);

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

    // The old "auto-download straight from Patreon's CDN" path (TryResolveAttachmentAsync +
    // DownloadGatedReleaseAsync) was removed in the round-2 audit: Amethyst doesn't distribute
    // files through Patreon's site any more, and the path attached the user's bearer token to
    // API-supplied URLs without a host check. Gated installs now use the creator file picker,
    // the author's download server, or the manual browser-download flow.

    /// <summary>
    /// Fetches identity + memberships, and if Patreon rejects a token that looked perfectly
    /// current, spends the refresh token once and tries again before giving up.
    /// <para>
    /// <see cref="EnsureFreshTokenAsync"/> only refreshes within five minutes of the recorded
    /// expiry, so a token invalidated early — a clock that drifted, a session revoked from
    /// Patreon's side, an expiry we recorded wrong — went straight to a forced sign-out and a
    /// fresh trip through the browser, when a refresh would very likely have fixed it silently
    /// (audit finding 36). If the refresh ALSO fails, the 401 propagates and the caller signs out
    /// exactly as before: this recovers sessions, it never keeps a dead one alive.
    /// </para>
    /// </summary>
    private async Task<(PatreonAccount Account, IReadOnlyList<PatreonMembership> Memberships)>
        FetchIdentityWithOneRetryAsync(int generation, CancellationToken ct)
    {
        await EnsureFreshTokenAsync(generation, ct);
        try
        {
            return await _client.FetchIdentityAndMembershipsAsync(_currentAccount!, ct);
        }
        catch (PatreonUnauthorizedException)
        {
            _logger.Information(
                "Patreon rejected a token that wasn't due to expire — refreshing once before signing out");

            PatreonAccount refreshed;
            try
            {
                refreshed = await _client.RefreshAsync(_currentAccount!, ct);
            }
            catch (HttpRequestException ex) when (IsAuthorizationFailure(ex))
            {
                // The token endpoint answered and said no (a dead or revoked refresh token), which
                // is an authorization failure however it's dressed up — RefreshAsync surfaces it
                // as an HttpRequestException via EnsureSuccessStatusCode. Rethrowing it AS an
                // authorization failure is what makes the caller sign out; leaving it as a generic
                // HTTP error made startup keep a permanently dead session and told the user they
                // were signed in. A refresh that fails with no status at all is a network problem,
                // and is left alone so a flaky connection never costs someone their session.
                throw new PatreonUnauthorizedException();
            }

            if (!await TryCommitAccountAsync(generation, refreshed))
                throw new OperationCanceledException("Signed out while refreshing the Patreon session.");
            return await _client.FetchIdentityAndMembershipsAsync(_currentAccount!, ct);
        }
    }

    /// <summary>
    /// Assigns and persists a refreshed account, but only if the session it belongs to is still
    /// the current one. Every commit goes through here: checking at the CALLERS wasn't enough,
    /// because a refresh completing after a sign-out would already have written the token back to
    /// disk by the time the caller looked.
    /// </summary>
    private async Task<bool> TryCommitAccountAsync(int generation, PatreonAccount account)
    {
        if (generation != _sessionGeneration)
        {
            _logger.Information("Discarding a Patreon token that arrived after the session ended");
            return false;
        }
        _currentAccount = account;
        await _store.SaveAsync(account);
        return true;
    }

    private async Task EnsureFreshTokenAsync(int generation, CancellationToken ct)
    {
        if (_currentAccount == null) return;
        // Refresh proactively when within 5 minutes of expiry so we don't get caught
        // mid-call with a stale token.
        if (_currentAccount.ExpiresAt - DateTime.UtcNow > TimeSpan.FromMinutes(5)) return;

        try
        {
            var refreshed = await _client.RefreshAsync(_currentAccount, ct);
            await TryCommitAccountAsync(generation, refreshed);
        }
        catch (HttpRequestException ex) when (IsAuthorizationFailure(ex))
        {
            // Same mapping as the retry path below: the token endpoint answering "no" is an
            // authorization failure whatever HTTP shape it arrives in, and the caller must sign
            // out rather than keep a session that can never work again.
            _logger.Warning(ex, "Patreon refused to refresh the token");
            throw new PatreonUnauthorizedException();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Patreon token refresh failed");
            throw;
        }
    }

    /// <summary>
    /// Whether a token-endpoint failure means "this session is finished" rather than "try later".
    /// Only an outright rejection counts: OAuth returns 400 (invalid_grant) for a dead or revoked
    /// refresh token and 401 for bad client credentials. Rate limits and server errors are
    /// emphatically NOT authorization failures — treating them as such would sign people out
    /// every time Patreon had a bad afternoon.
    /// </summary>
    private static bool IsAuthorizationFailure(HttpRequestException ex) =>
        ex.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized;
}
