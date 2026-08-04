using System.Text.Json;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

public sealed record GitHubRepo(string NameWithOwner, string Description, string Url);
public sealed record GitHubRelease(string TagName, string Name, bool IsDraft, bool IsPrerelease);

/// <summary>
/// Wraps the <c>gh</c> CLI. Authentication is delegated entirely to <c>gh auth login</c> —
/// we do not handle tokens ourselves. If <c>gh</c> is missing or not authed, the relevant
/// methods surface a clear error.
/// </summary>
public sealed class GitHubService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;

    public GitHubService(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("gh", new[] { "--version" }, ct: ct);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("gh",
                new[] { "auth", "status" }, ct: ct);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Every repository the signed-in user can PUSH to — their own, ones they were added to as a
    /// collaborator, and ones they reach through an organisation.
    ///
    /// <para><b>Why not <c>gh repo list</c>.</b> That command only ever returns repositories the
    /// user OWNS, and it has no affiliation switch. Publishing a mod on someone else's behalf means
    /// working in THEIR repository with push access granted to you — exactly the case the picker
    /// could never show. The REST endpoint takes an affiliation filter, so it is used instead.</para>
    ///
    /// <para>Paged by hand rather than with <c>gh api --paginate</c>: that concatenates one JSON
    /// array per page into a single stream, which is not a JSON document and will not parse.</para>
    /// </summary>
    public async Task<List<GitHubRepo>> ListReposAsync(int limit = 100, CancellationToken ct = default)
    {
        const int perPage = 100;
        const int maxPages = 20;   // backstop; 2000 repositories is far past a real author account

        var repos = new List<GitHubRepo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= maxPages && repos.Count < limit; page++)
        {
            var endpoint = "user/repos" +
                           "?affiliation=owner,collaborator,organization_member" +
                           $"&sort=full_name&per_page={perPage}&page={page}";
            _logger.Information("gh api {Endpoint}", endpoint);

            var result = await ProcessRunner.RunAsync("gh", new[] { "api", endpoint }, ct: ct);
            if (!result.Success)
                throw new InvalidOperationException($"gh api user/repos failed: {result.Combined}");

            var pageItems = JsonSerializer.Deserialize<List<JsonElement>>(result.Stdout, JsonOptions) ?? [];
            if (pageItems.Count == 0) break;

            foreach (var r in pageItems)
            {
                if (repos.Count >= limit) break;
                if (!CanPush(r)) continue;

                // REST returns snake_case; the old GraphQL-backed call returned camelCase.
                var nameWithOwner = Text(r, "full_name");
                if (string.IsNullOrEmpty(nameWithOwner) || !seen.Add(nameWithOwner)) continue;

                repos.Add(new GitHubRepo(nameWithOwner, Text(r, "description"), Text(r, "html_url")));
            }

            if (pageItems.Count < perPage) break;   // that was the last page
        }

        return repos;
    }

    /// <summary>
    /// Whether the signed-in user can push. Read access alone is no use here — the tool's whole
    /// job is committing an index and publishing releases.
    ///
    /// <para>Only an explicit <c>false</c> excludes a repository. If the permissions block is
    /// missing we cannot tell, and hiding a repository the user may well be able to publish to is
    /// the worse mistake — that is the bug this method exists to fix.</para>
    /// </summary>
    private static bool CanPush(JsonElement repo) =>
        !repo.TryGetProperty("permissions", out var permissions) ||
        !permissions.TryGetProperty("push", out var push) ||
        push.ValueKind != JsonValueKind.False;

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public async Task<List<GitHubRelease>> ListReleasesAsync(string repo, int limit = 30, CancellationToken ct = default)
    {
        _logger.Information("gh release list --repo {Repo}", repo);
        var result = await ProcessRunner.RunAsync("gh",
            new[]
            {
                "release", "list",
                "--repo", repo,
                "--limit", limit.ToString(),
                "--json", "tagName,name,isDraft,isPrerelease"
            },
            ct: ct);

        if (!result.Success)
            throw new InvalidOperationException($"gh release list failed: {result.Combined}");

        var releases = JsonSerializer.Deserialize<List<JsonElement>>(result.Stdout, JsonOptions) ?? [];
        return releases.Select(r => new GitHubRelease(
            r.GetProperty("tagName").GetString() ?? "",
            r.TryGetProperty("name", out var n) && n.ValueKind != JsonValueKind.Null ? n.GetString() ?? "" : "",
            r.TryGetProperty("isDraft", out var d) && d.GetBoolean(),
            r.TryGetProperty("isPrerelease", out var p) && p.GetBoolean()))
            .Where(r => !string.IsNullOrEmpty(r.TagName))
            .ToList();
    }

    public async Task<ProcessResult> CreateReleaseAsync(
        string repo, string tagName, string title, string? notes,
        IEnumerable<string> assetPaths, CancellationToken ct = default)
    {
        _logger.Information("gh release create {Tag} in {Repo}", tagName, repo);

        var args = new List<string>
        {
            "release", "create", tagName,
            "--repo", repo,
            "--title", title,
            "--notes", notes ?? ""
        };
        foreach (var asset in assetPaths) args.Add(asset);

        return await ProcessRunner.RunAsync("gh", args, ct: ct);
    }

    public async Task<ProcessResult> EditReleaseNotesAsync(
        string repo, string tagName, string notes, CancellationToken ct = default)
    {
        _logger.Information("gh release edit {Tag} (notes update) in {Repo}", tagName, repo);
        var args = new List<string>
        {
            "release", "edit", tagName,
            "--repo", repo,
            "--notes", notes
        };
        return await ProcessRunner.RunAsync("gh", args, ct: ct);
    }

    public async Task<ProcessResult> UploadReleaseAssetAsync(
        string repo, string tagName, string assetPath, bool clobber, CancellationToken ct = default)
    {
        _logger.Information("gh release upload {Tag} {Asset} (clobber={Clobber})", tagName, assetPath, clobber);

        var args = new List<string>
        {
            "release", "upload", tagName, assetPath,
            "--repo", repo
        };
        if (clobber) args.Add("--clobber");

        return await ProcessRunner.RunAsync("gh", args, ct: ct);
    }

    /// <summary>
    /// Returns the public download URL for an asset, given the repo and tag.
    /// GitHub's release assets follow a stable URL pattern.
    /// </summary>
    public static Uri BuildAssetUrl(string repo, string tagName, string assetFilename)
    {
        // repo is "owner/name" (keep its slash). The tag and filename are single path segments that
        // can contain characters needing URL-encoding (#, ?, %, spaces) — escape them so the URL
        // written into the index actually points at the uploaded asset.
        var escapedTag = Uri.EscapeDataString(tagName);
        var escapedFile = Uri.EscapeDataString(assetFilename);
        return new Uri($"https://github.com/{repo}/releases/download/{escapedTag}/{escapedFile}");
    }
}
