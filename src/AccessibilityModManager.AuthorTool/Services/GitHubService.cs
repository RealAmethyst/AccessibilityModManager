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

    public async Task<List<GitHubRepo>> ListReposAsync(int limit = 100, CancellationToken ct = default)
    {
        _logger.Information("gh repo list --limit {Limit} --json", limit);
        var result = await ProcessRunner.RunAsync("gh",
            new[]
            {
                "repo", "list",
                "--limit", limit.ToString(),
                "--json", "nameWithOwner,description,url"
            },
            ct: ct);

        if (!result.Success)
            throw new InvalidOperationException($"gh repo list failed: {result.Combined}");

        var repos = JsonSerializer.Deserialize<List<JsonElement>>(result.Stdout, JsonOptions) ?? [];
        return repos.Select(r => new GitHubRepo(
            r.GetProperty("nameWithOwner").GetString() ?? "",
            r.TryGetProperty("description", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetString() ?? "" : "",
            r.TryGetProperty("url", out var u) && u.ValueKind != JsonValueKind.Null ? u.GetString() ?? "" : ""))
            .Where(r => !string.IsNullOrEmpty(r.NameWithOwner))
            .ToList();
    }

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
