using System.IO;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// Where a project publishes to on GitHub, resolved from the repository rather than assumed.
/// </summary>
public sealed record GitPublishTarget(
    string WorktreeRoot,
    string Remote,
    string Branch,
    string Owner,
    string Repo,
    string IndexPathInRepo)
{
    /// <summary>The address a manager would read, on the branch being pushed.</summary>
    public string BranchRawUrl =>
        $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/{IndexPathInRepo}";

    /// <summary>The same file pinned to one commit. Immutable, so a CDN can't serve a stale copy of it.</summary>
    public string RawUrlAt(string commit) =>
        $"https://raw.githubusercontent.com/{Owner}/{Repo}/{commit}/{IndexPathInRepo}";

    public string Describe => $"{Owner}/{Repo}, branch {Branch}";
}

public enum GitPublishOutcome
{
    /// <summary>Committed and pushed, and the pushed commit was read back and matched.</summary>
    Published,

    /// <summary>Pushed and verified, but the branch's public address is still serving the old copy.</summary>
    PublishedPendingCdn,

    /// <summary>Stopped before anything was committed. The repository is untouched.</summary>
    Refused,

    /// <summary>Committed locally, and the push did not land. Managers cannot see it.</summary>
    CommittedNotPushed,

    /// <summary>The push landed, but exact post-publish verification failed.</summary>
    PublishedVerificationFailed
}

public sealed record GitPublishResult(GitPublishOutcome Outcome, string Title, string Message)
{
    public string? Commit { get; init; }
    public GitPublishTarget? Target { get; init; }

    /// <summary>
    /// The bytes actually committed, which are the candidate normalized to LF line endings. The
    /// caller must record THESE as published rather than what it handed in — recording the
    /// pre-normalization bytes would make the very next project-open decide the folder differs from
    /// what is live and offer to replace it.
    /// </summary>
    public byte[]? PublishedBytes { get; init; }
}

/// <summary>An index read from one exact fetched remote commit, never from a mutable CDN URL.</summary>
public sealed record GitRemoteIndexSnapshot(
    bool Succeeded,
    bool BranchExists,
    string? Commit,
    byte[]? IndexBytes,
    string? Error);

/// <summary>
/// Publishes an index by committing and pushing it, for authors who host their catalog on GitHub
/// rather than on a server.
///
/// <para><b>Nothing here is best-effort.</b> The equivalent local commit that used to follow a
/// server publish could fail silently because it changed nothing that mattered; here the commit and
/// the push ARE the publication, so every step either succeeds or refuses out loud.</para>
///
/// <para>The concurrency guarantee is git's own: a non-fast-forward push is rejected and nothing
/// lands. No lock is invented, because a rejected push already means exactly "somebody else changed
/// this branch first".</para>
/// </summary>
public sealed class GitHubIndexPublisher(GitService git, ILogger logger)
{
    /// <summary>
    /// Works out exactly where this project would publish, or explains why it cannot.
    ///
    /// <para>Everything ambiguous refuses rather than letting git pick. A detached HEAD has no
    /// branch to push, several remotes with no upstream give no answer as to which one, and letting
    /// plain <c>git push</c> guess is how an index reaches a ref nobody reads.</para>
    /// </summary>
    public async Task<(GitPublishTarget? Target, string? Error)> ResolveTargetAsync(
        string projectPath, CancellationToken ct = default)
    {
        if (!await git.IsRepoAsync(projectPath, ct))
            return (null, "This project folder isn't a git repository, so there's nothing to push to.");

        var root = await RunAsync(projectPath, ct, "rev-parse", "--show-toplevel");
        if (root is null) return (null, "Couldn't work out the repository root for this folder.");

        var branch = await RunAsync(projectPath, ct, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (string.IsNullOrWhiteSpace(branch))
        {
            return (null, "This repository has no branch checked out (detached HEAD), so there is no " +
                          "branch to publish to. Check out the branch your index lives on and try again.");
        }

        var remote = await ResolveRemoteAsync(projectPath, branch, ct);
        if (remote.Error is not null) return (null, remote.Error);

        var remoteUrl = await RunAsync(projectPath, ct, "remote", "get-url", remote.Name!);
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return (null, $"Couldn't read the address of remote '{remote.Name}'.");

        if (ParseGitHubSlug(remoteUrl) is not { } slug)
        {
            return (null, $"Remote '{remote.Name}' points at {remoteUrl}, which isn't a GitHub " +
                          "repository. GitHub publishing needs a github.com remote.");
        }

        var worktreeRoot = Path.GetFullPath(root);
        var indexFull = Path.GetFullPath(Path.Combine(projectPath, "index.json"));
        var relative = Path.GetRelativePath(worktreeRoot, indexFull).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal))
            return (null, "index.json sits outside the repository, so it can't be committed to it.");

        return (new GitPublishTarget(worktreeRoot, remote.Name!, branch!, slug.Owner, slug.Repo, relative), null);
    }

    /// <summary>
    /// Whether the branch already exists on the remote, or null when the remote could not be asked.
    ///
    /// <para>Used to word the confirmation honestly: creating a branch publishes every commit
    /// already in the folder, because git pushes a commit together with its ancestry. An author
    /// deserves to be told that before it happens rather than after.</para>
    /// </summary>
    public async Task<bool?> RemoteBranchExistsAsync(GitPublishTarget target, CancellationToken ct = default)
    {
        var lsRemote = await RunResultAsync(target.WorktreeRoot, ct,
            "ls-remote", target.Remote, $"refs/heads/{target.Branch}");
        return lsRemote.Success ? !string.IsNullOrWhiteSpace(lsRemote.Stdout) : null;
    }

    /// <summary>
    /// Fetches the configured branch and reads <c>index.json</c> from the resulting immutable commit.
    /// A branch-based raw.githubusercontent.com address is deliberately not involved: that address
    /// may continue serving the preceding commit for its cache lifetime immediately after a push.
    /// </summary>
    public async Task<GitRemoteIndexSnapshot> ReadRemoteIndexAsync(
        GitPublishTarget target,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var lsRemote = await RunResultAsync(
            target.WorktreeRoot,
            ct,
            "ls-remote",
            target.Remote,
            $"refs/heads/{target.Branch}");
        if (!lsRemote.Success)
        {
            return new GitRemoteIndexSnapshot(
                false,
                false,
                null,
                null,
                $"Asking {target.Describe} for its branch failed:\n\n{lsRemote.Combined}");
        }

        if (string.IsNullOrWhiteSpace(lsRemote.Stdout))
            return new GitRemoteIndexSnapshot(true, false, null, null, null);

        var fetch = await RunResultAsync(
            target.WorktreeRoot,
            ct,
            "fetch",
            target.Remote,
            target.Branch);
        if (!fetch.Success)
        {
            return new GitRemoteIndexSnapshot(
                false,
                true,
                null,
                null,
                $"Fetching {target.Describe} failed:\n\n{fetch.Combined}");
        }

        var commit = await RunAsync(target.WorktreeRoot, ct, "rev-parse", "--verify", "FETCH_HEAD");
        if (commit is null)
            return new GitRemoteIndexSnapshot(false, true, null, null, "Git did not report the fetched commit id.");

        var tree = await RunResultAsync(
            target.WorktreeRoot,
            ct,
            "ls-tree",
            "--name-only",
            commit,
            "--",
            target.IndexPathInRepo);
        if (!tree.Success)
        {
            return new GitRemoteIndexSnapshot(
                false,
                true,
                commit,
                null,
                $"The fetched commit could not be inspected:\n\n{tree.Combined}");
        }

        var pathExists = tree.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim().Replace('\\', '/'))
            .Any(path => string.Equals(path, target.IndexPathInRepo, StringComparison.Ordinal));
        if (!pathExists)
            return new GitRemoteIndexSnapshot(true, true, commit, null, null);

        var (exit, bytes, stderr) = await ProcessRunner.RunBinaryAsync(
            "git",
            new[] { "show", $"{commit}:{target.IndexPathInRepo}" },
            target.WorktreeRoot,
            ct: ct);
        return exit == 0
            ? new GitRemoteIndexSnapshot(true, true, commit, bytes, null)
            : new GitRemoteIndexSnapshot(
                false,
                true,
                commit,
                null,
                $"The fetched index could not be read:\n\n{stderr}");
    }

    /// <summary>
    /// The whole transaction: refuse on anything unexpected, commit only the index, take the final
    /// authorization with the commit made but nothing pushed, then push one explicit refspec.
    /// </summary>
    /// <param name="authorizeBeforePush">
    /// Run with the commit already made and the push not yet attempted — the last moment where
    /// stopping still costs nothing remote. Returning a message here aborts and leaves the local
    /// commit, which is reported rather than silently reset.
    /// </param>
    public Task<GitPublishResult> PublishAsync(
        GitPublishTarget target,
        byte[] rawCandidate,
        string commitMessage,
        Func<Task<string?>> authorizeBeforePush,
        CancellationToken ct = default) =>
        PublishAsync(
            target,
            rawCandidate,
            commitMessage,
            authorizeBeforePush,
            transitionValidator: null,
            ct);

    public async Task<GitPublishResult> PublishAsync(
        GitPublishTarget target,
        byte[] rawCandidate,
        string commitMessage,
        Func<Task<string?>> authorizeBeforePush,
        Func<byte[]?, byte[], string?>? transitionValidator,
        CancellationToken ct = default)
    {
        // Normalized FIRST, so what is written, what is staged and what is compared are one thing.
        //
        // The index is written with Environment.NewLine, which is CRLF on Windows, and Git for
        // Windows sets core.autocrlf=true by default — so git converts it to LF while staging and
        // the stored blob legitimately differs from the file. The byte check below is right to
        // refuse that; the answer is to stop handing git something it is going to rewrite, not to
        // stop checking. JSON carries no meaning in its line endings, and LF is what a file served
        // out of a repository should be anyway.
        var candidate = NormalizeToLf(rawCandidate);

        // ---- the tree must be exactly as expected, and a failed check is not a clean tree ----

        var status = await git.StatusPorcelainAsync(target.WorktreeRoot, ct);
        if (!status.Success)
        {
            return Refused("Couldn't read the repository state",
                $"`git status` failed, so there is no way to tell what would be committed:\n\n{status.Combined}");
        }

        if (DescribeUnexpectedChanges(status.Stdout, target.IndexPathInRepo) is { } unexpected)
        {
            return Refused("The repository has other changes",
                $"Publishing commits {target.IndexPathInRepo} and nothing else, so it stops when " +
                $"anything else is in the way:\n\n{unexpected}\n\n" +
                "Commit, stash or revert those first. Nothing was committed or pushed.");
        }

        // ---- does that branch exist on the remote at all? ----

        // ls-remote answers this without needing the ref to exist, which `fetch` does not: fetching
        // a branch that was never pushed fails with "couldn't find remote ref", and reading that as
        // "the remote is unreachable" turns the ordinary FIRST publish into a hard error. A brand
        // new index repository is empty by definition.
        var remoteSnapshot = await ReadRemoteIndexAsync(target, ct);
        if (!remoteSnapshot.Succeeded)
        {
            return Refused("Couldn't reach the remote",
                $"There is no exact remote catalog baseline to publish against:\n\n" +
                $"{remoteSnapshot.Error}\n\nNothing was committed or pushed.");
        }

        var remoteBranchExists = remoteSnapshot.BranchExists;

        // An unborn HEAD — a repository with no commits yet — is a normal state for a new index
        // repo, and the index commit simply becomes the first one.
        var localHead = await RunAsync(target.WorktreeRoot, ct, "rev-parse", "--verify", "HEAD");

        if (remoteBranchExists)
        {
            // ---- it exists, so this publish must build on exactly what is there ----

            var remoteHead = remoteSnapshot.Commit;
            if (remoteHead is null)
                return Refused("Couldn't compare with the remote", "Nothing was committed or pushed.");

            if (localHead is null)
            {
                return Refused("This copy has no history",
                    $"{target.Describe} already has commits, but this folder has none — so this is not " +
                    "a copy of that branch. Clone the repository and work in that folder. Nothing was " +
                    "committed or pushed.");
            }

            if (!string.Equals(localHead, remoteHead, StringComparison.Ordinal))
            {
                var ahead = await RunAsync(target.WorktreeRoot, ct, "rev-list", "--count", $"{remoteHead}..{localHead}");
                var behind = await RunAsync(target.WorktreeRoot, ct, "rev-list", "--count", $"{localHead}..{remoteHead}");

                if (ahead is not "0")
                {
                    return Refused("This branch has commits that aren't pushed",
                        $"{target.Describe} has {ahead} local commit(s) the remote doesn't have. Publishing " +
                        "would push those too, and they aren't part of this index change. Push or undo them " +
                        "first. Nothing was committed or pushed.");
                }

                if (behind is not "0")
                {
                    return Refused("Someone else changed this branch",
                        $"{target.Describe} has moved on by {behind} commit(s) since this copy last " +
                        "updated. Pull first so this publish builds on what's there. Nothing was " +
                        "committed or pushed.");
                }
            }
        }
        else
        {
            // ---- it does not exist: this push creates the branch ----

            // The "don't sweep up unpushed commits" rule cannot apply here and must not be faked:
            // git pushes a commit together with its whole ancestry, so creating a branch publishes
            // every commit already in this folder. That is almost always exactly what the author
            // wants for a new index repository, and it is stated rather than silently done.
            logger.Information(
                "Remote branch {Branch} does not exist on {Remote}; this publish will create it",
                target.Branch, target.Remote);
        }

        if (transitionValidator is not null &&
            transitionValidator(remoteSnapshot.IndexBytes, candidate) is { } transitionError)
        {
            return Refused(
                "The catalog would lose published releases",
                $"{transitionError}\n\nNothing was committed or pushed.");
        }

        // ---- write, stage only the index, and prove what git actually stored ----

        var indexOnDisk = Path.Combine(target.WorktreeRoot, target.IndexPathInRepo.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllBytesAsync(indexOnDisk, candidate, ct);

        var add = await git.AddAsync(target.WorktreeRoot, target.IndexPathInRepo, ct);
        if (!add.Success)
            return Refused("Couldn't stage the index", $"{add.Combined}\n\nNothing was committed or pushed.");

        var staged = await ReadStagedBlobAsync(target, ct);
        if (staged is null)
            return Refused("Couldn't read back what was staged", "Nothing was committed or pushed.");

        if (!staged.SequenceEqual(candidate))
        {
            return Refused("Git stored something different from what was written",
                "The staged copy of index.json doesn't match the bytes this publish prepared, even " +
                "after normalizing line endings. That means a clean filter or Git LFS is rewriting " +
                "it — and whatever managers would then read is not what you approved. Check this " +
                "repository's .gitattributes. Nothing was committed.");
        }

        // ---- commit, then the last free check, then push ----

        var commit = await git.CommitAsync(target.WorktreeRoot, commitMessage, ct);
        if (!commit.Success)
            return Refused("Couldn't commit the index", $"{commit.Combined}\n\nNothing was pushed.");

        var commitSha = await RunAsync(target.WorktreeRoot, ct, "rev-parse", "--verify", "HEAD");
        if (commitSha is null)
            return new GitPublishResult(GitPublishOutcome.CommittedNotPushed, "Committed, but its id couldn't be read",
                "The commit was made but git wouldn't report its id, so it wasn't pushed. Managers " +
                "cannot see it.") { Target = target };

        if (await authorizeBeforePush() is { } refusal)
        {
            return new GitPublishResult(GitPublishOutcome.CommittedNotPushed, "Stopped before pushing", refusal)
            {
                Commit = commitSha,
                Target = target
            };
        }

        // Explicit refspec, never force: git is told exactly which commit goes to which branch, so
        // no push configuration can send it somewhere else, and a non-fast-forward is rejected.
        var push = await RunResultAsync(target.WorktreeRoot, ct,
            "push", target.Remote, $"{commitSha}:refs/heads/{target.Branch}");

        if (!push.Success)
        {
            var text = push.Combined;
            var concurrent = text.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("fetch first", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("rejected", StringComparison.OrdinalIgnoreCase);

            return new GitPublishResult(GitPublishOutcome.CommittedNotPushed,
                concurrent ? "Someone else pushed first" : "The push was refused",
                (concurrent
                    ? $"{target.Describe} changed while this was being prepared, so nothing was pushed."
                    : "The remote refused the push. That is not a conflict — it is usually sign-in, " +
                      "two-factor, or a protected branch.") +
                $"\n\n{text}\n\n" +
                "The change IS committed locally and managers cannot see it. Nothing was lost: fix " +
                "the cause and publish again.")
            {
                Commit = commitSha,
                Target = target
            };
        }

        return await VerifyPushedAsync(
            target,
            commitSha,
            candidate,
            remoteSnapshot.IndexBytes,
            transitionValidator,
            ct);
    }

    /// <summary>
    /// Proves the push landed, then how visible it is.
    ///
    /// <para>The branch's raw address is a CDN and can serve the previous copy for a few minutes, so
    /// it is never the thing that decides success — the remote ref is. Telling an author their
    /// publish failed because a cache had not caught up would send them to publish again over a
    /// perfectly good one.</para>
    /// </summary>
    private async Task<GitPublishResult> VerifyPushedAsync(
        GitPublishTarget target,
        string commitSha,
        byte[] candidate,
        byte[]? previousIndex,
        Func<byte[]?, byte[], string?>? transitionValidator,
        CancellationToken ct)
    {
        var remoteRef = await RunResultAsync(target.WorktreeRoot, ct,
            "ls-remote", target.Remote, $"refs/heads/{target.Branch}");
        var remoteSha = remoteRef.Success
            ? remoteRef.Stdout.Split('\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            : null;

        if (remoteSha is null)
        {
            return new GitPublishResult(
                GitPublishOutcome.PublishedVerificationFailed,
                "Published, but the remote ref could not be verified",
                $"The push command succeeded, but reading back {target.Describe} failed:\n\n" +
                $"{remoteRef.Combined}")
            {
                Commit = commitSha,
                Target = target
            };
        }

        var (blobExit, committedBytes, blobError) = await ProcessRunner.RunBinaryAsync(
            "git",
            new[] { "show", $"{commitSha}:{target.IndexPathInRepo}" },
            target.WorktreeRoot,
            ct: ct);
        if (blobExit != 0 || !committedBytes.AsSpan().SequenceEqual(candidate))
        {
            return new GitPublishResult(
                GitPublishOutcome.PublishedVerificationFailed,
                "Published, but the committed index did not verify",
                blobExit != 0
                    ? $"The pushed commit's index could not be read back:\n\n{blobError}"
                    : "The pushed commit's index bytes do not match the approved candidate.")
            {
                Commit = commitSha,
                Target = target,
                PublishedBytes = blobExit == 0 ? committedBytes : null
            };
        }

        if (transitionValidator is not null &&
            transitionValidator(previousIndex, committedBytes) is { } transitionError)
        {
            return new GitPublishResult(
                GitPublishOutcome.PublishedVerificationFailed,
                "Published, but release preservation did not verify",
                transitionError)
            {
                Commit = commitSha,
                Target = target,
                PublishedBytes = committedBytes
            };
        }

        if (remoteSha is not null && !string.Equals(remoteSha, commitSha, StringComparison.Ordinal))
        {
            return new GitPublishResult(GitPublishOutcome.Published, "Published, and already superseded",
                $"This commit reached {target.Describe}, and the branch has since moved on to another " +
                "commit. What managers read is whatever is there now.")
            {
                Commit = commitSha,
                Target = target,
                PublishedBytes = committedBytes
            };
        }

        logger.Information("Pushed index commit {Commit} to {Target}", commitSha, target.Describe);

        return new GitPublishResult(GitPublishOutcome.Published, "Published",
            $"index.json is committed and pushed to {target.Describe}.")
        {
            Commit = commitSha,
            Target = target,
            PublishedBytes = committedBytes
        };
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Names anything in the worktree that publishing is not allowed to sweep up — which is any
    /// path other than the index.
    ///
    /// <para>The index itself is deliberately NOT an offender in any state. It is about to be
    /// overwritten with the candidate and staged again, so whatever it held first cannot reach the
    /// commit. An earlier version refused when it was already staged, on the reasoning that
    /// somebody else may have staged it — and that turned every refusal AFTER staging into a
    /// permanent one, because this publisher stages it itself. A failed publish then poisoned every
    /// later attempt and the only way out was to know to run <c>git reset</c> by hand.</para>
    ///
    /// <para>What refuses is a STAGED path other than the index, because that is precisely what can
    /// end up in the commit: <c>git commit</c> takes everything staged. An untracked file or an
    /// unstaged edit to a tracked file cannot reach it, so refusing on those was a wall with
    /// nothing behind it — and an ordinary repository has untracked files in it all the time.</para>
    /// </summary>
    private static string? DescribeUnexpectedChanges(string porcelain, string indexPathInRepo)
    {
        var offenders = new List<string>();

        foreach (var line in porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;

            // Porcelain v1 is "XY path": X is the staging area, Y is the worktree. '?' is untracked
            // (never committed) and ' ' is unchanged there, so only the rest can ride along.
            var stagedState = line[0];
            if (stagedState is ' ' or '?') continue;

            var path = line[3..].Trim().Replace('\\', '/').Trim('"');

            // Renames read as "orig -> new"; both halves are staged, so name the whole thing.
            if (!string.Equals(path, indexPathInRepo, StringComparison.OrdinalIgnoreCase))
                offenders.Add(path);
        }

        return offenders.Count == 0 ? null : string.Join("\n", offenders.Distinct());
    }

    private async Task<byte[]?> ReadStagedBlobAsync(GitPublishTarget target, CancellationToken ct)
    {
        try
        {
            var (exit, bytes, _) = await ProcessRunner.RunBinaryAsync(
                "git", new[] { "show", $":{target.IndexPathInRepo}" }, target.WorktreeRoot, ct: ct);
            return exit == 0 ? bytes : null;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Couldn't read the staged index blob");
            return null;
        }
    }

    /// <summary>
    /// Which remote to push to. The branch's own upstream if it has one; otherwise the only remote
    /// there is. More than one and no upstream is genuinely ambiguous and refuses.
    /// </summary>
    private async Task<(string? Name, string? Error)> ResolveRemoteAsync(
        string projectPath, string? branch, CancellationToken ct)
    {
        var upstream = await RunAsync(projectPath, ct, "config", "--get", $"branch.{branch}.remote");
        if (!string.IsNullOrWhiteSpace(upstream)) return (upstream, null);

        var remotesText = await RunAsync(projectPath, ct, "remote");
        var remotes = (remotesText ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .ToList();

        return remotes.Count switch
        {
            0 => (null, "This repository has no remote, so there is nowhere to push it."),
            1 => (remotes[0], null),
            _ => (null, $"Branch '{branch}' has no upstream and this repository has several remotes " +
                        $"({string.Join(", ", remotes)}). Set the branch's upstream so there is no doubt " +
                        "which one to publish to.")
        };
    }

    /// <summary>
    /// CRLF to LF, so what git stores is what was written.
    ///
    /// <para>Safe on raw UTF-8: CR and LF are single bytes that cannot occur inside a multi-byte
    /// sequence, so this cannot corrupt any character. A lone CR (no LF after it) is left alone —
    /// it isn't a line ending git would touch, and rewriting it would be this method inventing a
    /// change of its own.</para>
    /// </summary>
    public static byte[] NormalizeToLf(byte[] bytes)
    {
        const byte cr = 0x0D, lf = 0x0A;
        if (Array.IndexOf(bytes, cr) < 0) return bytes;

        var output = new byte[bytes.Length];
        var written = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == cr && i + 1 < bytes.Length && bytes[i + 1] == lf) continue;
            output[written++] = bytes[i];
        }

        return output[..written];
    }

    /// <summary>owner/repo out of an https or ssh GitHub remote, or null when it isn't GitHub.</summary>
    public static (string Owner, string Repo)? ParseGitHubSlug(string remoteUrl)
    {
        var url = remoteUrl.Trim();
        string path;

        if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = url["git@github.com:".Length..];
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                 parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            path = parsed.AbsolutePath.TrimStart('/');
        }
        else
        {
            return null;
        }

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    private static GitPublishResult Refused(string title, string message) =>
        new(GitPublishOutcome.Refused, title, message);

    private static async Task<string?> RunAsync(string folder, CancellationToken ct, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, folder, ct);
        return result.Success ? result.Stdout.Trim() : null;
    }

    private static Task<ProcessResult> RunResultAsync(string folder, CancellationToken ct, params string[] args) =>
        ProcessRunner.RunAsync("git", args, folder, ct);
}
