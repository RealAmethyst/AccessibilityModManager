using System.Diagnostics;
using System.Text;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// The publish transaction, against a real git remote.
///
/// <para>A bare repository in a temp folder stands in for GitHub. The transaction never talks to
/// github.com — it fetches, compares object ids, commits and pushes an explicit refspec — so a
/// local remote exercises every step that can go wrong, including the ones that matter most: that
/// nothing unrelated is swept into the commit, and that a refusal really does leave the remote
/// untouched.</para>
/// </summary>
public sealed class GitHubIndexPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amm-gitpub-" + Guid.NewGuid().ToString("N"));

    private readonly string _bare;
    private readonly string _work;

    public GitHubIndexPublisherTests()
    {
        Directory.CreateDirectory(_root);
        _bare = Path.Combine(_root, "origin.git");
        _work = Path.Combine(_root, "work");

        Git(_root, "init", "--bare", "-b", "main", _bare);
        Git(_root, "clone", _bare, _work);

        // Deterministic identity and no signing, so the tests don't depend on the machine's config.
        Git(_work, "config", "user.email", "test@example.invalid");
        Git(_work, "config", "user.name", "Test");
        Git(_work, "config", "commit.gpgsign", "false");
        // Pinned so the staged-blob comparison tests the publisher rather than the machine's
        // autocrlf setting. A rewriting filter is exactly what that check exists to catch, and it
        // must not fire or not fire by accident.
        Git(_work, "config", "core.autocrlf", "false");

        File.WriteAllText(Path.Combine(_work, "README.md"), "seed\n");
        Git(_work, "add", "README.md");
        Git(_work, "commit", "-m", "seed");
        Git(_work, "push", "origin", "main");
    }

    public void Dispose()
    {
        try { DeleteHard(_root); } catch { /* best effort */ }
    }

    private GitPublishTarget Target() =>
        new(_work, "origin", "main", "someone", "their-mod", "index.json");

    private static GitHubIndexPublisher Publisher() =>
        new(new GitService(TestLogger.Create()), TestLogger.Create());

    private static byte[] Index(string pluginId) =>
        Encoding.UTF8.GetBytes($"{{\"pluginId\":\"{pluginId}\",\"repoVersion\":\"1\"}}\n");

    [Fact]
    public async Task APublishCommitsTheIndexAndPushesIt()
    {
        var candidate = Index("theirs");

        var result = await Publisher().PublishAsync(
            Target(), candidate, "publish index", () => Task.FromResult<string?>(null));

        Assert.Equal(GitPublishOutcome.Published, result.Outcome);
        Assert.NotNull(result.Commit);

        // The remote really moved to this commit, and really holds these bytes.
        Assert.Equal(result.Commit, RemoteHead());
        Assert.Equal(candidate, RemoteBlob("index.json"));
    }

    [Fact]
    public async Task OnlyTheIndexIsCommitted()
    {
        // A file that was already committed and is now untouched must stay untouched, and the
        // commit must name exactly one path.
        var result = await Publisher().PublishAsync(
            Target(), Index("theirs"), "publish index", () => Task.FromResult<string?>(null));

        Assert.Equal(GitPublishOutcome.Published, result.Outcome);

        var changed = Git(_work, "show", "--name-only", "--pretty=format:", "HEAD").Trim();
        Assert.Equal("index.json", changed);
    }

    [Fact]
    public async Task UnrelatedStagedChangesRefuseBeforeAnythingIsCommitted()
    {
        await File.WriteAllTextAsync(Path.Combine(_work, "README.md"), "edited by hand\n");
        Git(_work, "add", "README.md");
        var before = LocalHead();

        var result = await Publisher().PublishAsync(
            Target(), Index("theirs"), "publish index", () => Task.FromResult<string?>(null));

        Assert.Equal(GitPublishOutcome.Refused, result.Outcome);
        Assert.Contains("README.md", result.Message);
        Assert.Equal(before, LocalHead());   // nothing committed
    }

    /// <summary>
    /// The things an ordinary working folder is full of, none of which can reach the commit: an
    /// untracked scratch file, and an edit to a tracked file that was never staged. Refusing on
    /// these made publishing impossible in any repository somebody actually works in.
    /// </summary>
    [Fact]
    public async Task UntrackedAndUnstagedChangesDoNotBlockPublishing()
    {
        await File.WriteAllTextAsync(Path.Combine(_work, "scratch.txt"), "notes to self\n");
        await File.WriteAllTextAsync(Path.Combine(_work, "README.md"), "edited, not staged\n");

        var result = await Publisher().PublishAsync(
            Target(), Index("theirs"), "publish index", () => Task.FromResult<string?>(null));

        Assert.Equal(GitPublishOutcome.Published, result.Outcome);

        // And neither of them was swept into it.
        var changed = Git(_work, "show", "--name-only", "--pretty=format:", "HEAD").Trim();
        Assert.Equal("index.json", changed);
    }

    /// <summary>
    /// An index left staged by an earlier attempt must not block the next one.
    ///
    /// <para>This publisher stages the index itself, so any refusal after that point left it staged
    /// — and refusing on "already staged" then made every later attempt fail too. Amethyst hit
    /// exactly that: one refusal and publishing was dead until she knew to run `git reset` by
    /// hand.</para>
    /// </summary>
    [Fact]
    public async Task AnIndexLeftStagedByAnEarlierAttemptIsSimplyReplaced()
    {
        await File.WriteAllTextAsync(Path.Combine(_work, "index.json"), "{\"stale\":true}\n");
        Git(_work, "add", "index.json");

        var candidate = Index("theirs");
        var result = await Publisher().PublishAsync(
            Target(), candidate, "publish index", () => Task.FromResult<string?>(null));

        Assert.Equal(GitPublishOutcome.Published, result.Outcome);
        // What landed is the candidate, not the bytes that were sitting staged.
        Assert.Equal(candidate, RemoteBlob("index.json"));
    }

    [Fact]
    public async Task AnUnrelatedStagedFileStillRefuses()
    {
        // git commit takes everything staged, so this one would ride along inside what looks like
        // an index publish.
        await File.WriteAllTextAsync(Path.Combine(_work, "secrets.txt"), "oops\n");
        Git(_work, "add", "secrets.txt");

        var result = await Publisher().PublishAsync(
            Target(), Index("theirs"), "publish index", () => Task.FromResult<string?>(null));

        Assert.Equal(GitPublishOutcome.Refused, result.Outcome);
        Assert.Contains("secrets.txt", result.Message);
    }

    [Fact]
    public async Task LocalCommitsThatWereNeverPushedRefuse()
    {
        // Publishing would carry this unrelated commit along with the index.
        await File.WriteAllTextAsync(Path.Combine(_work, "notes.txt"), "wip\n");
        Git(_work, "add", "notes.txt");
        Git(_work, "commit", "-m", "unrelated work");

        var result = await Publisher().PublishAsync(
            Target(), Index("theirs"), "publish index", () => Task.FromResult<string?>(null));

        Assert.Equal(GitPublishOutcome.Refused, result.Outcome);
        Assert.Contains("local commit", result.Message);
        Assert.Contains("aren't pushed", result.Title);
    }

    [Fact]
    public async Task ABranchSomebodyElseMovedRefuses()
    {
        // A second clone pushes first — the ordinary "someone else published" case.
        var other = Path.Combine(_root, "other");
        Git(_root, "clone", _bare, other);
        Git(other, "config", "user.email", "other@example.invalid");
        Git(other, "config", "user.name", "Other");
        Git(other, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(other, "index.json"), "{\"from\":\"them\"}\n");
        Git(other, "add", "index.json");
        Git(other, "commit", "-m", "their publish");
        Git(other, "push", "origin", "main");

        var result = await Publisher().PublishAsync(
            Target(), Index("theirs"), "publish index", () => Task.FromResult<string?>(null));

        Assert.Equal(GitPublishOutcome.Refused, result.Outcome);
        Assert.Contains("moved on", result.Message);
    }

    [Fact]
    public async Task RefusingAtTheLastCheckLeavesTheCommitAndNeverPushes()
    {
        // The registry check runs with the commit made and nothing pushed. Saying no there must not
        // reach the remote, and must not pretend the local commit does not exist.
        var remoteBefore = RemoteHead();

        var result = await Publisher().PublishAsync(
            Target(), Index("theirs"), "publish index",
            () => Task.FromResult<string?>("The registry stopped naming this plugin."));

        Assert.Equal(GitPublishOutcome.CommittedNotPushed, result.Outcome);
        Assert.Contains("registry", result.Message);
        Assert.Equal(remoteBefore, RemoteHead());       // the remote is untouched
        Assert.NotEqual(remoteBefore, LocalHead());     // and the commit is real
    }

    /// <summary>
    /// The first publish into a brand new repository, which is how this is actually met: the
    /// remote has no branches at all. Fetching one that was never pushed fails with "couldn't find
    /// remote ref", and reading that as an unreachable remote turned the ordinary first publish
    /// into a hard error.
    /// </summary>
    [Fact]
    public async Task PublishingIntoAnEmptyRepositoryCreatesTheBranch()
    {
        var emptyBare = Path.Combine(_root, "empty.git");
        var fresh = Path.Combine(_root, "fresh");
        Git(_root, "init", "--bare", "-b", "main", emptyBare);
        Git(_root, "init", "-b", "main", fresh);
        Git(fresh, "remote", "add", "origin", emptyBare);
        Git(fresh, "config", "user.email", "test@example.invalid");
        Git(fresh, "config", "user.name", "Test");
        Git(fresh, "config", "commit.gpgsign", "false");
        Git(fresh, "config", "core.autocrlf", "false");

        var target = new GitPublishTarget(fresh, "origin", "main", "someone", "new-mod", "index.json");
        var candidate = Index("brand-new");

        Assert.False(await Publisher().RemoteBranchExistsAsync(target));

        var result = await Publisher().PublishAsync(
            target, candidate, "first publish", () => Task.FromResult<string?>(null));

        Assert.Equal(GitPublishOutcome.Published, result.Outcome);
        Assert.Equal(result.Commit, Git(emptyBare, "rev-parse", "refs/heads/main").Trim());
        Assert.Equal(candidate, Encoding.UTF8.GetBytes(Git(emptyBare, "show", "refs/heads/main:index.json")));
    }

    /// <summary>
    /// A folder with no commits at all — a clone of an empty repository. The index commit simply
    /// becomes the first one, rather than the publish failing because HEAD cannot be resolved.
    /// </summary>
    [Fact]
    public async Task AnUnbornBranchCanStillPublish()
    {
        var emptyBare = Path.Combine(_root, "unborn.git");
        var clone = Path.Combine(_root, "unborn-clone");
        Git(_root, "init", "--bare", "-b", "main", emptyBare);
        Git(_root, "clone", emptyBare, clone);
        Git(clone, "config", "user.email", "test@example.invalid");
        Git(clone, "config", "user.name", "Test");
        Git(clone, "config", "commit.gpgsign", "false");
        Git(clone, "config", "core.autocrlf", "false");

        var target = new GitPublishTarget(clone, "origin", "main", "someone", "new-mod", "index.json");

        var result = await Publisher().PublishAsync(
            target, Index("unborn"), "first publish", () => Task.FromResult<string?>(null));

        Assert.Equal(GitPublishOutcome.Published, result.Outcome);
        // One commit, and it is the root — nothing was invented to hang it off.
        Assert.Equal("1", Git(clone, "rev-list", "--count", "HEAD").Trim());
    }

    /// <summary>
    /// The failure Amethyst hit. Git for Windows defaults to <c>core.autocrlf=true</c>, and the
    /// index is written with <c>Environment.NewLine</c> — CRLF — so git rewrote it to LF while
    /// staging and the byte check correctly refused. Refusing was right; handing git something it
    /// was always going to rewrite was not.
    /// </summary>
    [Fact]
    public async Task CrlfContentPublishesOnAnAutocrlfRepository()
    {
        Git(_work, "config", "core.autocrlf", "true");

        var crlf = Encoding.UTF8.GetBytes("{\r\n  \"pluginId\": \"theirs\"\r\n}\r\n");

        var result = await Publisher().PublishAsync(
            Target(), crlf, "publish index", () => Task.FromResult<string?>(null));

        Assert.Equal(GitPublishOutcome.Published, result.Outcome);

        // What landed is the LF form, and that is what the caller is told to record — recording the
        // CRLF bytes would make the next project-open think the folder differs from what is live.
        var expected = Encoding.UTF8.GetBytes("{\n  \"pluginId\": \"theirs\"\n}\n");
        Assert.Equal(expected, result.PublishedBytes);
        Assert.Equal(expected, RemoteBlob("index.json"));
    }

    [Fact]
    public void NormalizingLineEndingsOnlyTouchesCrlf()
    {
        var lf = Encoding.UTF8.GetBytes("a\nb\n");
        Assert.Same(lf, GitHubIndexPublisher.NormalizeToLf(lf));   // untouched, not even copied

        Assert.Equal(lf, GitHubIndexPublisher.NormalizeToLf(Encoding.UTF8.GetBytes("a\r\nb\r\n")));

        // A lone CR is not a line ending git would rewrite, so it is left exactly as it is.
        var loneCr = Encoding.UTF8.GetBytes("a\rb");
        Assert.Equal(loneCr, GitHubIndexPublisher.NormalizeToLf(loneCr));

        // Multi-byte UTF-8 survives: CR and LF can't occur inside a continuation sequence.
        var accented = Encoding.UTF8.GetBytes("Pokémon\r\nDigimon\r\n");
        Assert.Equal(Encoding.UTF8.GetBytes("Pokémon\nDigimon\n"),
            GitHubIndexPublisher.NormalizeToLf(accented));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
    [InlineData("https://github.com/owner/repo", "owner", "repo")]
    [InlineData("git@github.com:owner/repo.git", "owner", "repo")]
    public void GitHubRemotesAreRecognised(string url, string owner, string repo)
    {
        var slug = GitHubIndexPublisher.ParseGitHubSlug(url);

        Assert.NotNull(slug);
        Assert.Equal(owner, slug!.Value.Owner);
        Assert.Equal(repo, slug.Value.Repo);
    }

    [Theory]
    [InlineData("https://gitlab.com/owner/repo.git")]
    [InlineData("https://example.com/owner/repo.git")]
    [InlineData("https://github.com/owner")]
    public void NonGitHubOrMalformedRemotesAreNot(string url) =>
        Assert.Null(GitHubIndexPublisher.ParseGitHubSlug(url));

    // ------------------------------------------------------------------ helpers

    private string LocalHead() => Git(_work, "rev-parse", "HEAD").Trim();

    private string RemoteHead() =>
        Git(_bare, "rev-parse", "refs/heads/main").Trim();

    private byte[] RemoteBlob(string path)
    {
        var text = Git(_bare, "show", $"refs/heads/main:{path}");
        // git's own output through this helper is text; the publisher does its byte-exact check
        // with the binary runner. Comparing UTF-8 here is enough to prove the right content landed.
        return Encoding.UTF8.GetBytes(text);
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed:\n{stdout}\n{stderr}");

        return stdout;
    }

    /// <summary>Git marks objects read-only, so a plain recursive delete fails on Windows.</summary>
    private static void DeleteHard(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
