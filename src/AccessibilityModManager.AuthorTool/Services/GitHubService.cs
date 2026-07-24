using System.Text.Json;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

public sealed record GitHubRepo(string NameWithOwner, string Description, string Url);
public sealed record GitHubRelease(string TagName, string Name, bool IsDraft, bool IsPrerelease);
public sealed record GitHubIssue(int Number, string Title, string State, string Url, string Author, string Body);

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

    public async Task<List<GitHubIssue>> ListIssuesAsync(string repo, int limit = 30, string state = "open", CancellationToken ct = default)
    {
        _logger.Information("gh issue list --repo {Repo} --state {State}", repo, state);
        var result = await ProcessRunner.RunAsync("gh",
            new[]
            {
                "issue", "list",
                "--repo", repo,
                "--state", state,
                "--limit", limit.ToString(),
                "--json", "number,title,state,url,author,body"
            },
            ct: ct);

        if (!result.Success)
            throw new InvalidOperationException($"gh issue list failed: {result.Combined}");

        var issues = JsonSerializer.Deserialize<List<JsonElement>>(result.Stdout, JsonOptions) ?? [];
        return issues.Select(i => new GitHubIssue(
            i.GetProperty("number").GetInt32(),
            i.TryGetProperty("title", out var t) && t.ValueKind != JsonValueKind.Null ? t.GetString() ?? "" : "",
            i.TryGetProperty("state", out var s) && s.ValueKind != JsonValueKind.Null ? s.GetString() ?? "" : "",
            i.TryGetProperty("url", out var u) && u.ValueKind != JsonValueKind.Null ? u.GetString() ?? "" : "",
            i.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object &&
                a.TryGetProperty("login", out var login) && login.ValueKind != JsonValueKind.Null
                    ? login.GetString() ?? "" : "",
            i.TryGetProperty("body", out var b) && b.ValueKind != JsonValueKind.Null ? b.GetString() ?? "" : ""))
            .ToList();
    }

    public async Task<ProcessResult> AddIssueCommentAsync(string repo, int issueNumber, string body, CancellationToken ct = default)
    {
        _logger.Information("gh issue comment {Number} on {Repo}", issueNumber, repo);
        return await ProcessRunner.RunAsync("gh",
            new[]
            {
                "issue", "comment", issueNumber.ToString(),
                "--repo", repo,
                "--body", body
            },
            ct: ct);
    }

    public async Task<ProcessResult> CloseIssueAsync(string repo, int issueNumber, string? reason = null, CancellationToken ct = default)
    {
        _logger.Information("gh issue close {Number} on {Repo} (reason={Reason})", issueNumber, repo, reason ?? "default");
        var args = new List<string> { "issue", "close", issueNumber.ToString(), "--repo", repo };
        if (!string.IsNullOrEmpty(reason))
        {
            args.Add("--reason");
            args.Add(reason);
        }
        return await ProcessRunner.RunAsync("gh", args, ct: ct);
    }

    public async Task<int?> CreatePullRequestAsync(string repo, string headBranch, string baseBranch, string title, string body, CancellationToken ct = default)
    {
        _logger.Information("gh pr create on {Repo} ({Head} -> {Base})", repo, headBranch, baseBranch);
        var result = await ProcessRunner.RunAsync("gh",
            new[]
            {
                "pr", "create",
                "--repo", repo,
                "--head", headBranch,
                "--base", baseBranch,
                "--title", title,
                "--body", body
            },
            ct: ct);

        if (!result.Success)
            throw new InvalidOperationException($"gh pr create failed: {result.Combined}");

        // gh prints the PR URL. Parse the trailing number out of it.
        var url = result.Stdout.Trim();
        var lastSlash = url.LastIndexOf('/');
        if (lastSlash < 0 || !int.TryParse(url[(lastSlash + 1)..], out var prNumber))
            return null;
        return prNumber;
    }

    public async Task<ProcessResult> MergePullRequestAsync(string repo, int prNumber, string strategy = "squash", bool deleteBranch = true, CancellationToken ct = default)
    {
        _logger.Information("gh pr merge {Number} on {Repo} ({Strategy})", prNumber, repo, strategy);
        var args = new List<string>
        {
            "pr", "merge", prNumber.ToString(),
            "--repo", repo,
            $"--{strategy}"
        };
        if (deleteBranch) args.Add("--delete-branch");
        return await ProcessRunner.RunAsync("gh", args, ct: ct);
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
