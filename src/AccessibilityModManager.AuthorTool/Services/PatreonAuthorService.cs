using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Patreon;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// AuthorTool-side façade over <see cref="PatreonClient"/> using the AUTHOR client_id +
/// scopes. Sign-in token is stored in a separate DPAPI blob (<c>patreon-author.dat</c>) so
/// it can't get confused with a user-side manager session if both apps run on the same
/// machine. Methods expose the author's own campaign + tier list so the per-release
/// editor can render checkboxes.
/// </summary>
public sealed class PatreonAuthorService
{
    private static readonly string TokenFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager-Author",
        "patreon-author.dat");

    private static readonly byte[] Entropy = "AMM-Author:Patreon:v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly PatreonClient _client;
    private readonly ILogger _logger;

    private PatreonAccount? _account;
    private PatreonOwnCampaign? _ownCampaign;

    public PatreonAuthorService(HttpClient http, ILogger logger)
    {
        _client = new PatreonClient(http, PatreonAppRegistry.Author, logger);
        _logger = logger;
    }

    public bool IsSignedIn => _account != null;
    public PatreonAccount? CurrentAccount => _account;
    public PatreonOwnCampaign? OwnCampaign => _ownCampaign;

    public event Action? StateChanged;

    public async Task LoadAsync()
    {
        _account = LoadFromDisk();
        if (_account != null && _ownCampaign == null)
        {
            // Best-effort fetch on launch so the campaign + tiers are ready when the
            // author opens the release dialog.
            try { _ownCampaign = await _client.FetchOwnCampaignAsync(_account, CancellationToken.None); }
            catch (Exception ex) { _logger.Warning(ex, "Couldn't refresh author's Patreon campaign on launch"); }
        }
    }

    public async Task SignInAsync(CancellationToken ct)
    {
        var account = await _client.SignInAsync(ct);
        _account = account;
        SaveToDisk(account);
        try { _ownCampaign = await _client.FetchOwnCampaignAsync(account, ct); }
        catch (Exception ex) { _logger.Warning(ex, "Sign-in succeeded but couldn't fetch own campaign"); }
        StateChanged?.Invoke();
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        if (_account != null)
            await _client.RevokeAsync(_account, ct);
        try { if (File.Exists(TokenFile)) File.Delete(TokenFile); } catch { }
        _account = null;
        _ownCampaign = null;
        StateChanged?.Invoke();
    }

    public async Task<PatreonOwnCampaign?> RefreshOwnCampaignAsync(CancellationToken ct)
    {
        if (_account == null) return null;
        _ownCampaign = await _client.FetchOwnCampaignAsync(_account, ct);
        return _ownCampaign;
    }

    /// <summary>
    /// Validate that the user's pasted Patreon post URL belongs to the signed-in author's
    /// own campaign and list every downloadable attachment on it. Used by the release
    /// dialog to let the author pick which attachment corresponds to <em>this</em> release
    /// (Q1=A / Q2=A — one post per game, dropdown after Validate). The second tuple element
    /// is the raw response JSON, dumped to a debug file when no attachments parse out so we
    /// can see the actual schema Patreon returns.
    /// </summary>
    public async Task<(IReadOnlyList<PatreonPostAttachment> Attachments, string? DebugFilePath)>
        ValidatePostUrlAsync(string postUrl, CancellationToken ct)
    {
        if (_account == null) return (Array.Empty<PatreonPostAttachment>(), null);
        var postId = ExtractPostId(postUrl);
        if (postId == null) return (Array.Empty<PatreonPostAttachment>(), null);

        var (attachments, raw) = await _client.FetchPostAttachmentsWithRawAsync(_account, postId, ct);
        if (attachments.Count == 0)
        {
            var path = WriteDebugDump(postId, raw);
            return (attachments, path);
        }
        return (attachments, null);
    }

    private string? WriteDebugDump(string postId, string rawJson)
    {
        try
        {
            var debugDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AccessibilityModManager-Author",
                "debug");
            Directory.CreateDirectory(debugDir);
            var path = Path.Combine(debugDir, $"patreon-post-{postId}.json");
            File.WriteAllText(path, rawJson);
            _logger.Information("Patreon post probe returned 0 attachments — raw response saved to {Path}", path);
            return path;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't write Patreon post debug dump");
            return null;
        }
    }

    /// <summary>
    /// Pulls the numeric id out of a Patreon post URL like
    /// <c>https://www.patreon.com/posts/blah-blah-12345</c>. Returns null if the URL
    /// doesn't look like a Patreon post link.
    /// </summary>
    public static string? ExtractPostId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!uri.Host.EndsWith("patreon.com", StringComparison.OrdinalIgnoreCase)) return null;
        // Patreon post URLs look like /posts/{slug-with-dashes}-{numeric-id}. The id is the
        // trailing numeric run after the last dash; everything else is the slug.
        var lastSegment = uri.Segments.LastOrDefault()?.TrimEnd('/');
        if (string.IsNullOrEmpty(lastSegment)) return null;
        var dashIdx = lastSegment.LastIndexOf('-');
        var idCandidate = dashIdx >= 0 ? lastSegment[(dashIdx + 1)..] : lastSegment;
        return idCandidate.All(char.IsDigit) ? idCandidate : null;
    }

    private PatreonAccount? LoadFromDisk()
    {
        if (!File.Exists(TokenFile)) return null;
        try
        {
            var bytes = File.ReadAllBytes(TokenFile);
            var json = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<PatreonAccount>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't decrypt patreon-author.dat — treating as signed out");
            try { File.Delete(TokenFile); } catch { }
            return null;
        }
    }

    private void SaveToDisk(PatreonAccount account)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(account, JsonOptions);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(TokenFile, encrypted);
    }
}
