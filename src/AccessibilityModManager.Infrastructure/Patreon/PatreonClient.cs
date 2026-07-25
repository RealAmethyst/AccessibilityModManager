using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Patreon;

/// <summary>
/// Low-level Patreon API + OAuth (PKCE) client. One instance per OAuth app — the manager
/// and AuthorTool each construct their own with their respective <see cref="PatreonOAuthOptions"/>.
/// All API calls use the JSON:API v2 endpoints under <c>https://www.patreon.com/api/oauth2/v2</c>.
/// Higher-level façades (sign-in state, entitlement caching, install-flow integration) live
/// on top of this in <c>PatreonService</c> / <c>PatreonAuthorService</c>.
/// </summary>
public sealed class PatreonClient
{
    private const string ApiBase = "https://www.patreon.com/api/oauth2/v2";
    // V1 still serves authenticated requests with our v2-issued tokens. We use v1 only for
    // the post→attachments fetch — v2's post resource removed the include relationships
    // for file attachments (returns "ParameterInvalidOnType" for include=media or
    // attachments_media), but v1's `include=attachments` is alive and documented.
    private const string ApiBaseV1 = "https://www.patreon.com/api";
    private const string AuthorizeUrl = "https://www.patreon.com/oauth2/authorize";
    private const string TokenUrl = "https://www.patreon.com/api/oauth2/token";
    private const string RevokeUrl = "https://www.patreon.com/api/oauth2/token/revoke";

    private readonly HttpClient _http;
    private readonly PatreonOAuthOptions _options;
    private readonly ILogger _logger;

    public PatreonClient(HttpClient http, PatreonOAuthOptions options, ILogger logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public PatreonOAuthOptions Options => _options;

    // -------- OAuth flow --------

    /// <summary>
    /// Run the full OAuth2 PKCE flow: open the user's browser to Patreon's authorize page,
    /// spin up a one-shot loopback HTTP listener for the redirect, exchange the code for a
    /// token, and fetch the user's identity to populate the <see cref="PatreonAccount"/>.
    /// Cancellation token aborts the wait without leaking the listener.
    /// </summary>
    public async Task<PatreonAccount> SignInAsync(CancellationToken ct)
    {
        var (verifier, challenge) = GeneratePkcePair();
        var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{_options.LoopbackPort}/");
        listener.Start();

        try
        {
            // Open the browser to Patreon's consent screen.
            var authorizeUri =
                $"{AuthorizeUrl}?response_type=code" +
                $"&client_id={Uri.EscapeDataString(_options.ClientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}" +
                $"&scope={Uri.EscapeDataString(string.Join(" ", _options.Scopes))}" +
                $"&state={state}" +
                $"&code_challenge={challenge}" +
                $"&code_challenge_method=S256";

            _logger.Information("Opening Patreon authorize URL");
            Process.Start(new ProcessStartInfo { FileName = authorizeUri, UseShellExecute = true });

            // Wait for OUR redirect. Anything else arriving on this loopback port — a favicon
            // probe, a browser prefetch, another program, a stray '/?error=' — is answered and
            // ignored. Taking the first request that arrived meant one of those consumed the
            // listener, failed the sign-in for want of a code, and left the real callback
            // knocking on a closed port (audit finding 36). "Ours" means the configured callback
            // path AND the state we generated, checked before the request is accepted rather
            // than after, so a wrong-state request can neither end the wait nor be told it
            // signed in.
            var expectedPath = new Uri(_options.RedirectUri).AbsolutePath;

            // And it can't wait forever: the user may close the browser or simply walk away, and
            // an un-cancellable pending sign-in leaves the UI busy and the port held.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var cancelReg = timeout.Token.Register(() => { try { listener.Stop(); } catch { } });

            HttpListenerContext context;
            string? code, error;
            while (true)
            {
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (timeout.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        "Patreon sign-in timed out after 5 minutes with no answer from the browser. " +
                        "Start the sign-in again when you're ready.", ex);
                }

                code = context.Request.QueryString["code"];
                error = context.Request.QueryString["error"];
                var returnedState = context.Request.QueryString["state"];
                var path = context.Request.Url?.AbsolutePath ?? "/";

                // Ordinal, and the value must be non-EMPTY, not merely present: "?code=" passed a
                // null check, ended the wait, and got shown the "Signed in." page moments before
                // being rejected for having no code. A malformed callback now keeps waiting like
                // any other stray request.
                var isOurCallback =
                    string.Equals(path, expectedPath, StringComparison.Ordinal) &&
                    (!string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(error)) &&
                    returnedState == state;

                if (isOurCallback) break;

                _logger.Debug("Ignoring a request to the OAuth callback port that isn't our redirect: {Path}",
                    context.Request.Url?.PathAndQuery);
                try
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Couldn't answer a stray callback-port request");
                }
            }

            // The loop above already established this is our callback with our state, so the page
            // the user is shown now matches what actually happened — no more telling a browser
            // "Signed in." moments before the sign-in is rejected.
            var responseHtml = error == null
                ? "<html><body><h1>Signed in.</h1><p>You can close this tab and return to the app.</p></body></html>"
                : $"<html><body><h1>Sign-in failed.</h1><p>{WebUtility.HtmlEncode(error)}</p></body></html>";
            var bytes = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.Close();

            if (error != null)
                throw new InvalidOperationException($"Patreon sign-in failed: {error}");
            if (string.IsNullOrEmpty(code))
                throw new InvalidOperationException("Patreon redirect did not include an authorization code.");

            // Exchange the code for tokens (PKCE: send the verifier as proof).
            var account = await ExchangeCodeForTokenAsync(code, verifier, ct);

            // Round-trip the identity endpoint so we know who signed in.
            return await EnrichAccountFromIdentityAsync(account, ct);
        }
        finally
        {
            try { listener.Stop(); listener.Close(); } catch { }
        }
    }

    /// <summary>
    /// Refresh the access token using the refresh token. Patreon's tokens expire after one
    /// month; this is called transparently by higher-level code when an access token is
    /// near-expiry or comes back as 401.
    /// </summary>
    public async Task<PatreonAccount> RefreshAsync(PatreonAccount old, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = old.RefreshToken,
            ["client_id"] = _options.ClientId
        });
        using var resp = await _http.PostAsync(TokenUrl, form, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseTokenResponse(old, json);
    }

    /// <summary>Best-effort token revocation on Patreon's side (Q6=B).</summary>
    public async Task RevokeAsync(PatreonAccount account, CancellationToken ct)
    {
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = account.AccessToken,
                ["client_id"] = _options.ClientId
            });
            using var resp = await _http.PostAsync(RevokeUrl, form, ct);
            // Don't throw on non-success — revocation is best-effort, the local wipe is
            // what actually signs the user out.
            if (!resp.IsSuccessStatusCode)
                _logger.Warning("Patreon token revoke returned {Status}", resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Patreon token revoke failed; continuing");
        }
    }

    // -------- API calls --------

    /// <summary>
    /// Fetch the user's identity + memberships in one call. Returns the parsed memberships
    /// and an updated <see cref="PatreonAccount"/> with <c>LastEntitlementCheck</c> bumped.
    /// </summary>
    public async Task<(PatreonAccount UpdatedAccount, IReadOnlyList<PatreonMembership> Memberships)>
        FetchIdentityAndMembershipsAsync(PatreonAccount account, CancellationToken ct)
    {
        var url = $"{ApiBase}/identity?include=memberships.currently_entitled_tiers,memberships.campaign" +
                  $"&fields[user]=full_name,email" +
                  $"&fields[member]=patron_status,currently_entitled_amount_cents" +
                  $"&fields[tier]=title,amount_cents" +
                  $"&fields[campaign]=creation_name,vanity";

        var json = await GetJsonAsync(account, url, ct);
        var memberships = ParseMemberships(json);

        var updated = new PatreonAccount
        {
            AccessToken = account.AccessToken,
            RefreshToken = account.RefreshToken,
            ExpiresAt = account.ExpiresAt,
            UserId = account.UserId,
            FullName = account.FullName,
            Email = account.Email,
            LastEntitlementCheck = DateTime.UtcNow
        };
        return (updated, memberships);
    }

    /// <summary>
    /// AuthorTool path: list the signed-in author's own campaigns + tiers so the AuthorTool
    /// can render tier checkboxes. An author has exactly one campaign in practice.
    /// </summary>
    public async Task<PatreonOwnCampaign?> FetchOwnCampaignAsync(PatreonAccount account, CancellationToken ct)
    {
        var url = $"{ApiBase}/campaigns?include=tiers" +
                  $"&fields[campaign]=creation_name,vanity" +
                  $"&fields[tier]=title,amount_cents,published";
        var json = await GetJsonAsync(account, url, ct);
        return ParseOwnCampaign(json);
    }

    /// <summary>
    /// AuthorTool path: fetch ALL attachments on a Patreon post, plus the raw response
    /// JSON so callers can show diagnostic info when parsing finds zero attachments — useful
    /// while we're still figuring out which include parameter Patreon's v2 API expects.
    /// </summary>
    public async Task<(IReadOnlyList<PatreonPostAttachment> Attachments, string RawJson)>
        FetchPostAttachmentsWithRawAsync(PatreonAccount account, string postId, CancellationToken ct)
    {
        // V1 endpoint for posts. V2's post resource doesn't accept any include value that
        // returns file attachments — both `attachments_media` and `media` come back as
        // ParameterInvalidOnType. V1's `include=attachments` still works and returns each
        // attachment as a JSON:API resource of type "attachment" with attributes
        // {name, size_bytes, url}. We don't request fields[attachment]=... — when the
        // attachment field set we asked for didn't match the real schema, included items
        // came back empty.
        var url = $"{ApiBaseV1}/posts/{Uri.EscapeDataString(postId)}?include=attachments";
        var json = await GetJsonAsync(account, url, ct);
        var parsed = ParsePostAttachments(json, postId);
        return (parsed, json);
    }

    // -------- Plumbing --------

    private async Task<PatreonAccount> ExchangeCodeForTokenAsync(string code, string verifier, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["client_id"] = _options.ClientId,
            ["code_verifier"] = verifier
        });
        using var resp = await _http.PostAsync(TokenUrl, form, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        // Use a placeholder PatreonAccount for the parser; the real fields get filled
        // in afterwards via the identity round-trip.
        var seed = new PatreonAccount
        {
            AccessToken = "", RefreshToken = "", ExpiresAt = DateTime.UtcNow,
            UserId = ""
        };
        return ParseTokenResponse(seed, json);
    }

    private async Task<PatreonAccount> EnrichAccountFromIdentityAsync(PatreonAccount account, CancellationToken ct)
    {
        var url = $"{ApiBase}/identity?fields[user]=full_name,email";
        var json = await GetJsonAsync(account, url, ct);
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        return new PatreonAccount
        {
            AccessToken = account.AccessToken,
            RefreshToken = account.RefreshToken,
            ExpiresAt = account.ExpiresAt,
            UserId = data.GetProperty("id").GetString() ?? "",
            FullName = data.TryGetProperty("attributes", out var attrs) && attrs.TryGetProperty("full_name", out var fn) ? fn.GetString() : null,
            Email = data.TryGetProperty("attributes", out var attrs2) && attrs2.TryGetProperty("email", out var em) ? em.GetString() : null,
            LastEntitlementCheck = null
        };
    }

    private async Task<string> GetJsonAsync(PatreonAccount account, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new PatreonUnauthorizedException();

        // Read the body even on failure — Patreon's error responses include a JSON:API
        // `errors` array with code_name / detail fields that diagnose the request quickly
        // (wrong scope, deprecated field, malformed include, etc). Without surfacing the
        // body, callers only see "400 Bad Request" with no clue why.
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = ExtractFirstErrorDetail(body) ?? body;
            if (detail.Length > 400) detail = detail[..400] + "…";
            _logger.Warning("Patreon API {Status} on {Url}: {Detail}", (int)resp.StatusCode, url, detail);
            throw new HttpRequestException($"Patreon API returned {(int)resp.StatusCode}: {detail}");
        }
        return body;
    }

    private static string? ExtractFirstErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("errors", out var errors) ||
                errors.ValueKind != JsonValueKind.Array ||
                errors.GetArrayLength() == 0) return null;
            var first = errors[0];
            var codeName = first.TryGetProperty("code_name", out var c) ? c.GetString() : null;
            var detail = first.TryGetProperty("detail", out var d) ? d.GetString() : null;
            return (codeName, detail) switch
            {
                (null, null) => null,
                (null, _) => detail,
                (_, null) => codeName,
                _ => $"{codeName}: {detail}"
            };
        }
        catch
        {
            return null;
        }
    }

    private static PatreonAccount ParseTokenResponse(PatreonAccount old, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Missing access_token");
        var refresh = root.GetProperty("refresh_token").GetString() ?? throw new InvalidOperationException("Missing refresh_token");
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        return new PatreonAccount
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
            UserId = old.UserId,
            FullName = old.FullName,
            Email = old.Email,
            LastEntitlementCheck = old.LastEntitlementCheck
        };
    }

    private static IReadOnlyList<PatreonMembership> ParseMemberships(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var memberships = new List<PatreonMembership>();

        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("relationships", out var rels) ||
            !rels.TryGetProperty("memberships", out var memberRel) ||
            !memberRel.TryGetProperty("data", out var memberRefs))
            return memberships;

        // Build lookup of included resources keyed by (type, id) so we can resolve
        // memberships → tiers and memberships → campaign.
        var included = new Dictionary<(string type, string id), JsonElement>();
        if (root.TryGetProperty("included", out var inc) && inc.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in inc.EnumerateArray())
            {
                included[(item.GetProperty("type").GetString()!, item.GetProperty("id").GetString()!)] = item;
            }
        }

        foreach (var mref in memberRefs.EnumerateArray())
        {
            var memberId = mref.GetProperty("id").GetString()!;
            if (!included.TryGetValue(("member", memberId), out var member)) continue;

            string? campaignId = null;
            string? campaignName = null;
            var tierIds = new List<string>();

            if (member.TryGetProperty("relationships", out var memberRels))
            {
                if (memberRels.TryGetProperty("campaign", out var campRel) &&
                    campRel.TryGetProperty("data", out var campData) &&
                    campData.ValueKind != JsonValueKind.Null)
                {
                    campaignId = campData.GetProperty("id").GetString();
                    if (campaignId != null && included.TryGetValue(("campaign", campaignId), out var camp) &&
                        camp.TryGetProperty("attributes", out var campAttrs))
                    {
                        if (campAttrs.TryGetProperty("creation_name", out var cn)) campaignName = cn.GetString();
                        if (string.IsNullOrEmpty(campaignName) && campAttrs.TryGetProperty("vanity", out var v)) campaignName = v.GetString();
                    }
                }

                if (memberRels.TryGetProperty("currently_entitled_tiers", out var tierRel) &&
                    tierRel.TryGetProperty("data", out var tierData) &&
                    tierData.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tierData.EnumerateArray())
                    {
                        var tid = t.GetProperty("id").GetString();
                        if (!string.IsNullOrEmpty(tid)) tierIds.Add(tid);
                    }
                }
            }

            if (string.IsNullOrEmpty(campaignId)) continue;

            memberships.Add(new PatreonMembership
            {
                CampaignId = campaignId!,
                CurrentlyEntitledTierIds = tierIds,
                CampaignDisplayName = campaignName
            });
        }
        return memberships;
    }

    private static PatreonOwnCampaign? ParseOwnCampaign(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        var first = data.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined) return null;

        var campaignId = first.GetProperty("id").GetString()!;
        string? campaignName = null;
        if (first.TryGetProperty("attributes", out var attrs))
        {
            if (attrs.TryGetProperty("creation_name", out var cn)) campaignName = cn.GetString();
            if (string.IsNullOrEmpty(campaignName) && attrs.TryGetProperty("vanity", out var v)) campaignName = v.GetString();
        }

        // Tier resources live in "included".
        var tiers = new List<PatreonTier>();
        if (root.TryGetProperty("included", out var inc))
        {
            foreach (var item in inc.EnumerateArray())
            {
                if (item.GetProperty("type").GetString() != "tier") continue;
                var id = item.GetProperty("id").GetString()!;
                string? title = null;
                int? amount = null;
                bool published = true;
                if (item.TryGetProperty("attributes", out var tAttrs))
                {
                    if (tAttrs.TryGetProperty("title", out var t)) title = t.GetString();
                    if (tAttrs.TryGetProperty("amount_cents", out var a) && a.ValueKind == JsonValueKind.Number) amount = a.GetInt32();
                    if (tAttrs.TryGetProperty("published", out var p) && p.ValueKind == JsonValueKind.False) published = false;
                }
                if (!published) continue; // hide unpublished/archived tiers
                tiers.Add(new PatreonTier(id, title ?? id, amount ?? 0));
            }
        }
        // Sort cheapest → most expensive so the checkbox order matches what the author sees on patreon.com.
        tiers = tiers.OrderBy(t => t.AmountCents).ToList();
        return new PatreonOwnCampaign(campaignId, campaignName ?? campaignId, tiers);
    }

    private static IReadOnlyList<PatreonPostAttachment> ParsePostAttachments(string json, string postId)
    {
        var result = new List<PatreonPostAttachment>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data)) return result;

        var requiredTierIds = new List<string>();
        if (data.TryGetProperty("relationships", out var rels) &&
            rels.TryGetProperty("tiers", out var tierRel) &&
            tierRel.TryGetProperty("data", out var tierData) &&
            tierData.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tierData.EnumerateArray())
            {
                var tid = t.GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(tid)) requiredTierIds.Add(tid);
            }
        }

        // Path 1: full attachment data via the JSON:API `included` array. This populates
        // when the user has view access to the post — i.e. patrons hitting the manager-side
        // download flow. Each item has a real download URL we can fetch.
        if (root.TryGetProperty("included", out var inc) && inc.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in inc.EnumerateArray())
            {
                var typeName = item.GetProperty("type").GetString();
                if (typeName != "attachment" && typeName != "media") continue;
                if (!item.TryGetProperty("attributes", out var mAttrs)) continue;

                var downloadUrl =
                    (mAttrs.TryGetProperty("url", out var u1) ? u1.GetString() : null)
                    ?? (mAttrs.TryGetProperty("download_url", out var u2) ? u2.GetString() : null);
                if (string.IsNullOrEmpty(downloadUrl)) continue;

                var fileName =
                    (mAttrs.TryGetProperty("name", out var n1) ? n1.GetString() : null)
                    ?? (mAttrs.TryGetProperty("file_name", out var n2) ? n2.GetString() : null);

                long? size = mAttrs.TryGetProperty("size_bytes", out var sz) && sz.ValueKind == JsonValueKind.Number
                    ? sz.GetInt64() : null;

                result.Add(new PatreonPostAttachment(postId, new Uri(downloadUrl), fileName, size, requiredTierIds));
            }
        }

        // Path 2: preview metadata in `data.attributes.attachments_preview_metadata`. This
        // is what creators see for their own tier-locked posts — Patreon won't grant the
        // creator view access via OAuth, so the included array is empty. Filename + size
        // are enough for the AuthorTool's "pick which attachment" dropdown; the manager
        // download path needs a URL and will refuse the install if it ends up here (which
        // it shouldn't, because patrons get the included path).
        if (result.Count == 0 && data.TryGetProperty("attributes", out var attrs) &&
            attrs.TryGetProperty("attachments_preview_metadata", out var preview) &&
            preview.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in preview.EnumerateArray())
            {
                var fileName = item.TryGetProperty("file_name", out var fn) ? fn.GetString() : null;
                if (string.IsNullOrEmpty(fileName)) continue;

                long? size = item.TryGetProperty("size_bytes", out var sz) && sz.ValueKind == JsonValueKind.Number
                    ? sz.GetInt64() : null;

                result.Add(new PatreonPostAttachment(postId, DownloadUrl: null, fileName, size, requiredTierIds));
            }
        }

        return result;
    }

    private static (string Verifier, string Challenge) GeneratePkcePair()
    {
        // RFC 7636: verifier is 43-128 unreserved characters; challenge = base64url(sha256(verifier)).
        var verifierBytes = RandomNumberGenerator.GetBytes(64);
        var verifier = Base64Url(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64Url(challengeBytes);
        return (verifier, challenge);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Thrown when Patreon returns 401 — caller should refresh the token (or treat as
/// signed-out if the refresh fails too).
/// </summary>
public sealed class PatreonUnauthorizedException : Exception
{
    public PatreonUnauthorizedException() : base("Patreon access token rejected (401).") { }
}

public sealed record PatreonTier(string Id, string Title, int AmountCents)
{
    public string DisplayLabel => $"{Title} (${AmountCents / 100m:0.##}/mo)";
}

public sealed record PatreonOwnCampaign(string CampaignId, string DisplayName, IReadOnlyList<PatreonTier> Tiers);

/// <summary>
/// One downloadable file on a Patreon post. <see cref="DownloadUrl"/> is nullable because
/// the AuthorTool's validate path receives only preview metadata (filename + size) on its
/// own posts — Patreon's API returns <c>current_user_can_view=false</c> for a creator's
/// own tier-locked posts, and without view access there's no signed download URL. The
/// patron-side manager flow gets a real URL because the patron is actually entitled.
/// </summary>
public sealed record PatreonPostAttachment(
    string PostId, Uri? DownloadUrl, string? FileName, long? SizeBytes, IReadOnlyList<string> RequiredTierIds);
