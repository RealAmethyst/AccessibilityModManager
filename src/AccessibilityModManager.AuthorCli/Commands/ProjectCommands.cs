using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class ProjectCommands
{
    public static void AddTo(RootCommand root, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(services);

        var outcomeWriter = services.GetRequiredService<OutcomeWriter>();
        var authorConfig = services.GetRequiredService<AuthorConfigService>();
        var projectContext = services.GetRequiredService<AuthorProjectContext>();
        var indexFiles = services.GetRequiredService<IndexFileService>();
        var catalogWorkflow = services.GetRequiredService<CatalogWorkflow>();
        var gitService = services.GetRequiredService<GitService>();
        var gitHubService = services.GetRequiredService<IGitHubService>();

        var project = new Command("project", "Manage local author projects.");

        var init = new Command("init", "Create a starter index.json in the target folder.");
        var pluginIdArgument = new Argument<string>("plugin-id")
        {
            Description = "Plugin id for the new project."
        };
        init.Arguments.Add(pluginIdArgument);
        init.SetAction(async (parseResult, cancellationToken) =>
        {
            var rawProjectPath = CatalogCommandSupport.GetProjectOption(parseResult);
            if (string.IsNullOrWhiteSpace(rawProjectPath))
            {
                throw CatalogCommandSupport.Usage(
                    "project init requires --project <path> to choose the target folder.");
            }

            string projectPath;
            try
            {
                projectPath = Path.GetFullPath(rawProjectPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw CatalogCommandSupport.Validation(ex.Message);
            }

            PluginRepoIndex candidate;
            try
            {
                candidate = catalogWorkflow.CreateProject(parseResult.GetValue(pluginIdArgument)!);
            }
            catch (InvalidOperationException ex)
            {
                throw CatalogCommandSupport.Validation(ex.Message);
            }

            if (CatalogCommandSupport.GetDryRun(parseResult))
            {
                if (indexFiles.Exists(projectPath))
                {
                    throw CatalogCommandSupport.Conflict(
                        $"An index.json already exists at '{projectPath}'.");
                }

                CatalogCommandSupport.ValidateIndexCandidate(candidate);

                var preview = CatalogCommandSupport.CreateProjectSummaryResult(
                    "projectInitialized",
                    $"Dry run: would create a new project for '{candidate.PluginId}' at '{projectPath}'.",
                    projectPath,
                    candidate,
                    dryRun: true);

                return CatalogCommandSupport.Complete(outcomeWriter, parseResult, preview);
            }

            await using var lease = await projectContext.AcquireWriteLeaseAsync(projectPath, cancellationToken);
            if (indexFiles.Exists(projectPath))
            {
                throw CatalogCommandSupport.Conflict(
                    $"An index.json already exists at '{projectPath}'.");
            }

            var durable = CatalogCommandSupport.StampGeneratedAt(candidate);
            CatalogCommandSupport.ValidateIndexCandidate(durable);
            indexFiles.Save(projectPath, durable);

            authorConfig.RecordRecent(
                projectPath,
                displayName: CatalogCommandSupport.DefaultProjectDisplayName(projectPath));

            var result = CatalogCommandSupport.CreateProjectSummaryResult(
                "projectInitialized",
                $"Created a new project for '{durable.PluginId}' at '{projectPath}'.",
                projectPath,
                durable,
                dryRun: false);

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var recent = new Command("recent", "List recently opened author projects.");
        recent.SetAction(parseResult =>
        {
            var projects = authorConfig.Load().RecentProjects
                .OrderByDescending(project => project.LastOpenedAt)
                .Select(project => new
                {
                    path = project.Path,
                    displayName = project.DisplayName ?? CatalogCommandSupport.DefaultProjectDisplayName(project.Path),
                    gitHubRepo = project.GitHubRepo,
                    lastOpenedAt = project.LastOpenedAt,
                    exists = Directory.Exists(project.Path),
                    lastPublishedIndexSha256 = project.LastPublishedIndexSha256
                })
                .ToArray();

            var result = CatalogCommandSupport.Success(
                "recentProjectsListed",
                new { projects },
                $"Found {projects.Length} recent project(s).");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var open = new Command("open", "Resolve a project and record it as recent.");
        open.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projectContext, parseResult, cancellationToken);
            var existing = authorConfig.GetRecent(resolved.ProjectPath);
            var dryRun = CatalogCommandSupport.GetDryRun(parseResult);

            if (!dryRun)
            {
                authorConfig.RecordRecent(
                    resolved.ProjectPath,
                    displayName: existing?.DisplayName ?? CatalogCommandSupport.DefaultProjectDisplayName(resolved.ProjectPath),
                    gitHubRepo: existing?.GitHubRepo);
            }

            var refreshed = dryRun ? existing : authorConfig.GetRecent(resolved.ProjectPath);
            var result = CatalogCommandSupport.Success(
                "projectOpened",
                new
                {
                    projectPath = resolved.ProjectPath,
                    pluginId = resolved.Index.PluginId,
                    repoVersion = resolved.Index.RepoVersion,
                    generatedAt = resolved.Index.GeneratedAt,
                    gameCount = resolved.Index.Games.Count,
                    releaseBucketCount = resolved.Index.ReleasesByGameId.Count,
                    gitHubRepo = refreshed?.GitHubRepo,
                    lastOpenedAt = refreshed?.LastOpenedAt,
                    dryRun
                },
                dryRun
                    ? $"Dry run: would open '{resolved.Index.PluginId}' at '{resolved.ProjectPath}'."
                    : $"Opened '{resolved.Index.PluginId}' at '{resolved.ProjectPath}'.");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var clone = new Command("clone", "Clone a GitHub repo into a local project folder.");
        var repoArgument = new Argument<string>("repo")
        {
            Description = "GitHub repo as owner/name or GitHub HTTPS URL."
        };
        clone.Arguments.Add(repoArgument);
        clone.SetAction(async (parseResult, cancellationToken) =>
        {
            var repo = CatalogCommandSupport.NormalizeGitHubRepo(parseResult.GetValue(repoArgument)!);
            var rawTargetPath = CatalogCommandSupport.GetProjectOption(parseResult);

            string targetPath;
            try
            {
                targetPath = Path.GetFullPath(
                    string.IsNullOrWhiteSpace(rawTargetPath)
                        ? Path.Combine(AuthorConfigService.GetReposDirectory(), repo.Replace('/', '-'))
                        : rawTargetPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw CatalogCommandSupport.Validation(ex.Message);
            }

            if (CatalogCommandSupport.GetDryRun(parseResult))
            {
                var preview = CatalogCommandSupport.Success(
                    "projectCloned",
                    new
                    {
                        projectPath = targetPath,
                        gitHubRepo = repo,
                        hasIndex = (bool?)null,
                        updatedExisting = (bool?)null,
                        dryRun = true
                    },
                    $"Dry run: would clone or update '{repo}' in '{targetPath}' and record it as a recent project.");

                return CatalogCommandSupport.Complete(outcomeWriter, parseResult, preview);
            }

            CatalogCommandSupport.EnsureGitAvailable(await gitService.IsAvailableAsync(cancellationToken), "Git");

            var updatedExisting = false;
            if (Directory.Exists(targetPath))
            {
                if (await gitService.IsRepoAsync(targetPath, cancellationToken))
                {
                    var pullResult = await gitService.PullAsync(targetPath, cancellationToken);
                    if (!pullResult.Success)
                    {
                        throw CatalogCommandSupport.Validation(
                            $"git pull failed for '{repo}': {pullResult.Combined}");
                    }

                    updatedExisting = true;
                }
                else if (Directory.EnumerateFileSystemEntries(targetPath).Any())
                {
                    throw CatalogCommandSupport.Conflict(
                        $"Target folder '{targetPath}' already exists and is not a Git repository.");
                }
                else
                {
                    var cloneResult = await gitService.CloneAsync(
                        $"https://github.com/{repo}.git",
                        targetPath,
                        cancellationToken);

                    if (!cloneResult.Success)
                    {
                        throw CatalogCommandSupport.Validation(
                            $"git clone failed for '{repo}': {cloneResult.Combined}");
                    }
                }
            }
            else
            {
                var cloneResult = await gitService.CloneAsync(
                    $"https://github.com/{repo}.git",
                    targetPath,
                    cancellationToken);

                if (!cloneResult.Success)
                {
                    throw CatalogCommandSupport.Validation(
                        $"git clone failed for '{repo}': {cloneResult.Combined}");
                }
            }

            var hasIndex = indexFiles.Exists(targetPath);
            authorConfig.RecordRecent(targetPath, displayName: repo, gitHubRepo: repo);

            var result = CatalogCommandSupport.Success(
                "projectCloned",
                new
                {
                    projectPath = targetPath,
                    gitHubRepo = repo,
                    hasIndex,
                    updatedExisting,
                    dryRun = false
                },
                hasIndex
                    ? updatedExisting
                        ? $"Updated '{repo}' in '{targetPath}' and recorded it as a recent project."
                        : $"Cloned '{repo}' to '{targetPath}' and recorded it as a recent project."
                    : updatedExisting
                        ? $"Updated '{repo}' in '{targetPath}' and recorded it as a recent project. No index.json exists there yet; run project init in that folder if this repo should host a catalog."
                        : $"Cloned '{repo}' to '{targetPath}' and recorded it as a recent project. No index.json exists there yet; run project init in that folder if this repo should host a catalog.");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var pull = new Command("pull", "Run git pull --ff-only in the resolved project folder.");
        pull.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projectContext, parseResult, cancellationToken);

            if (CatalogCommandSupport.GetDryRun(parseResult))
            {
                var preview = CatalogCommandSupport.Success(
                    "projectPulled",
                    new
                    {
                        projectPath = resolved.ProjectPath,
                        pluginId = resolved.Index.PluginId,
                        currentBranch = (string?)null,
                        remoteUrl = (string?)null,
                        output = (string?)null,
                        dryRun = true
                    },
                    $"Dry run: would pull the latest commits into '{resolved.ProjectPath}'.");

                return CatalogCommandSupport.Complete(outcomeWriter, parseResult, preview);
            }

            CatalogCommandSupport.EnsureGitAvailable(await gitService.IsAvailableAsync(cancellationToken), "Git");
            await using var lease = await projectContext.AcquireWriteLeaseAsync(resolved.ProjectPath, cancellationToken);
            if (!await gitService.IsRepoAsync(resolved.ProjectPath, cancellationToken))
            {
                throw CatalogCommandSupport.Validation(
                    $"'{resolved.ProjectPath}' is not a Git repository.");
            }

            var pullResult = await gitService.PullAsync(resolved.ProjectPath, cancellationToken);
            if (!pullResult.Success)
            {
                throw CatalogCommandSupport.Validation(
                    $"git pull failed in '{resolved.ProjectPath}': {pullResult.Combined}");
            }

            var currentBranch = await gitService.GetCurrentBranchAsync(resolved.ProjectPath, cancellationToken);
            var remoteUrl = await gitService.GetRemoteUrlAsync(resolved.ProjectPath, ct: cancellationToken);
            var result = CatalogCommandSupport.Success(
                "projectPulled",
                new
                {
                    projectPath = resolved.ProjectPath,
                    pluginId = resolved.Index.PluginId,
                    currentBranch,
                    remoteUrl,
                    output = string.IsNullOrWhiteSpace(pullResult.Combined) ? null : pullResult.Combined,
                    dryRun = false
                },
                $"Pulled the latest commits into '{resolved.ProjectPath}'.");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var repos = new Command("repos", "List GitHub repos the current gh account can push to.");
        repos.SetAction(async (parseResult, cancellationToken) =>
        {
            CatalogCommandSupport.EnsureGitAvailable(
                await gitHubService.IsAvailableAsync(cancellationToken),
                "GitHub CLI ('gh')");
            CatalogCommandSupport.EnsureGitHubAuthenticated(
                await gitHubService.IsAuthenticatedAsync(cancellationToken));

            var availableRepos = await gitHubService.ListReposAsync(ct: cancellationToken);
            var result = CatalogCommandSupport.Success(
                "projectReposListed",
                new
                {
                    repos = availableRepos.Select(repo => new
                    {
                        nameWithOwner = repo.NameWithOwner,
                        description = repo.Description,
                        url = repo.Url
                    }).ToArray()
                },
                $"Found {availableRepos.Count} GitHub repo(s).");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        var status = new Command("status", "Show the resolved project and local repo status.");
        status.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projectContext, parseResult, cancellationToken);
            var recentProject = authorConfig.GetRecent(resolved.ProjectPath);

            var gitAvailable = await gitService.IsAvailableAsync(cancellationToken);
            var isRepository = gitAvailable && await gitService.IsRepoAsync(resolved.ProjectPath, cancellationToken);

            string? currentBranch = null;
            string? remoteUrl = null;
            bool? hasUncommittedChanges = null;
            string[]? statusPorcelain = null;
            string? statusError = null;

            if (isRepository)
            {
                currentBranch = await gitService.GetCurrentBranchAsync(resolved.ProjectPath, cancellationToken);
                remoteUrl = await gitService.GetRemoteUrlAsync(resolved.ProjectPath, ct: cancellationToken);

                var statusResult = await gitService.StatusPorcelainAsync(resolved.ProjectPath, cancellationToken);
                if (statusResult.Success)
                {
                    hasUncommittedChanges = !string.IsNullOrWhiteSpace(statusResult.Stdout);
                    statusPorcelain = string.IsNullOrWhiteSpace(statusResult.Stdout)
                        ? Array.Empty<string>()
                        : statusResult.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                }
                else
                {
                    statusError = statusResult.Combined;
                }
            }

            var result = CatalogCommandSupport.Success(
                "projectStatus",
                new
                {
                    projectPath = resolved.ProjectPath,
                    pluginId = resolved.Index.PluginId,
                    repoVersion = resolved.Index.RepoVersion,
                    generatedAt = resolved.Index.GeneratedAt,
                    gameCount = resolved.Index.Games.Count,
                    releaseBucketCount = resolved.Index.ReleasesByGameId.Count,
                    exists = Directory.Exists(resolved.ProjectPath),
                    gitHubRepo = recentProject?.GitHubRepo,
                    lastOpenedAt = recentProject?.LastOpenedAt,
                    git = new
                    {
                        available = gitAvailable,
                        isRepository,
                        currentBranch,
                        remoteUrl,
                        hasUncommittedChanges,
                        statusPorcelain,
                        statusError
                    }
                },
                $"Resolved '{resolved.Index.PluginId}' at '{resolved.ProjectPath}'.");

            return CatalogCommandSupport.Complete(outcomeWriter, parseResult, result);
        });

        project.Subcommands.Add(init);
        project.Subcommands.Add(recent);
        project.Subcommands.Add(open);
        project.Subcommands.Add(clone);
        project.Subcommands.Add(pull);
        project.Subcommands.Add(repos);
        project.Subcommands.Add(status);

        root.Subcommands.Add(project);
    }
}
