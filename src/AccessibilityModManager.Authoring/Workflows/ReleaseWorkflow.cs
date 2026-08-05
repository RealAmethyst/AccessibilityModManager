using System.Security.Cryptography;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.Authoring.Workflows;

public sealed record ReleasePublishRequest(
    string ProjectPath,
    string PluginId,
    string GameId,
    string Version,
    string Channel,
    string SourceRepo,
    string LocalZipPath,
    string? AssetFileName,
    string? Notes,
    string? ChangelogUrl,
    PatreonGate? Patreon);

public sealed record ReleasePublishPreview(
    string Repository,
    string Tag,
    string AssetFileName,
    string Sha256,
    bool CreatesRelease,
    bool ReplacesAsset);

public sealed record ReleasePublishResult(
    ModRelease Release,
    string AssetUrl,
    string Sha256,
    IReadOnlyList<string> CompletedPhases);

public sealed record PackageStageRequest(
    string PluginId,
    string GameId,
    string Version,
    string LocalZipPath,
    string? AssetFileName);

public interface IReleaseWorkflow
{
    Task<WorkflowResult<PreparedRelease>> StagePackageAsync(PackageStageRequest request, CancellationToken ct);
    Task<WorkflowResult<ReleasePublishPreview>> PreviewAsync(ReleasePublishRequest request, CancellationToken ct);
    Task<WorkflowResult<PreparedRelease>> PrepareAsync(ReleasePublishRequest request, CancellationToken ct);
    Task<WorkflowResult<ReleasePublishResult>> PublishAsync(
        PreparedRelease prepared,
        ReleasePublishRequest request,
        bool confirmed,
        CancellationToken ct);
}

public sealed class PreparedRelease : IAsyncDisposable
{
    private readonly string _tempDirectory;
    private bool _disposed;

    internal PreparedRelease(
        string tempDirectory,
        string stagedPath,
        FileStream stream,
        string sha256,
        ReleasePublishPreview preview,
        ReleasePublishRequest? request,
        bool assetAlreadyMatches)
    {
        _tempDirectory = tempDirectory;
        StagedPath = stagedPath;
        Stream = stream;
        Sha256 = sha256;
        Preview = preview;
        Request = request;
        AssetAlreadyMatches = assetAlreadyMatches;
    }

    public ReleasePublishPreview Preview { get; }
    public string StagedPath { get; }
    public string Sha256 { get; }
    public FileStream Stream { get; }

    internal ReleasePublishRequest? Request { get; }
    internal bool AssetAlreadyMatches { get; }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        Stream.Dispose();
        TryDelete(_tempDirectory);
        return ValueTask.CompletedTask;
    }

    internal void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PreparedRelease));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class ReleaseWorkflow : IReleaseWorkflow
{
    private readonly IGitHubService _gitHub;
    private readonly IPublishedAssetProbe _assets;
    private readonly ILogger _logger;

    public ReleaseWorkflow(
        IGitHubService gitHub,
        IPublishedAssetProbe assets,
        ILogger logger)
    {
        _gitHub = gitHub ?? throw new ArgumentNullException(nameof(gitHub));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WorkflowResult<PreparedRelease>> StagePackageAsync(
        PackageStageRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PluginId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.GameId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Version);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.LocalZipPath);

            var localRequest = new ReleasePublishRequest(
                ProjectPath: Path.GetTempPath(),
                PluginId: request.PluginId.Trim(),
                GameId: request.GameId.Trim(),
                Version: request.Version.Trim(),
                Channel: "stable",
                SourceRepo: "local/stage",
                LocalZipPath: Path.GetFullPath(request.LocalZipPath),
                AssetFileName: NullIfBlank(request.AssetFileName),
                Notes: null,
                ChangelogUrl: null,
                Patreon: null);
            var staged = Stage(localRequest);
            try
            {
                ct.ThrowIfCancellationRequested();
                var report = PluginPackageValidation.Validate(
                    staged.Stream,
                    localRequest.PluginId,
                    localRequest.GameId,
                    localRequest.Version,
                    _logger);
                if (!report.IsValid)
                {
                    await staged.DisposeAsync();
                    return new WorkflowResult<PreparedRelease>(
                        "packageValidationFailed",
                        null,
                        new[] { "The manager would refuse this package." }.Concat(report.Errors).ToArray(),
                        WorkflowErrorKind.Validation,
                        new[] { "packageStaged" });
                }

                var prepared = new PreparedRelease(
                    Path.GetDirectoryName(staged.StagedPath)!,
                    staged.StagedPath,
                    staged.Stream,
                    staged.Sha256,
                    staged.Preview,
                    request: null,
                    assetAlreadyMatches: false);
                staged.Detach();
                return new WorkflowResult<PreparedRelease>(
                    "packageStaged",
                    prepared,
                    new[] { $"Prepared and validated {prepared.Preview.AssetFileName}; SHA256 {prepared.Sha256}." },
                    completedPhases: new[] { "packageStaged", "packageValidated" });
            }
            catch
            {
                await staged.DisposeAsync();
                throw;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Failure<PreparedRelease>(WorkflowErrorKind.Validation, "packageStagingFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<ReleasePublishPreview>> PreviewAsync(
        ReleasePublishRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var normalized = NormalizeRequest(request);
            if (normalized.Patreon is not null)
            {
                return Failure<ReleasePublishPreview>(
                    WorkflowErrorKind.Validation,
                    "releaseValidationFailed",
                    "A Patreon-gated package cannot be uploaded to a public GitHub release. Use the server or Patreon flow instead.");
            }

            if (!await _gitHub.IsAvailableAsync(ct))
                return Failure<ReleasePublishPreview>(WorkflowErrorKind.Authentication, "githubUnavailable", "GitHub CLI isn't installed or available on PATH.");
            if (!await _gitHub.IsAuthenticatedAsync(ct))
                return Failure<ReleasePublishPreview>(WorkflowErrorKind.Authentication, "githubAuthenticationRequired", "GitHub CLI isn't signed in. Run 'gh auth login' and try again.");

            var isPrivate = await _gitHub.IsRepoPrivateAsync(normalized.SourceRepo, ct);
            if (isPrivate is true)
            {
                return Failure<ReleasePublishPreview>(
                    WorkflowErrorKind.Validation,
                    "privateRepositoryRefused",
                    $"Repository '{normalized.SourceRepo}' is private. Its release assets cannot be downloaded anonymously by the mod manager.");
            }
            if (isPrivate is null)
            {
                return Failure<ReleasePublishPreview>(
                    WorkflowErrorKind.Conflict,
                    "repositoryVisibilityUnknown",
                    $"Couldn't verify whether '{normalized.SourceRepo}' is public. Nothing was uploaded.");
            }

            if (!File.Exists(normalized.LocalZipPath))
                throw new FileNotFoundException($"The wrapped ZIP isn't there: {normalized.LocalZipPath}", normalized.LocalZipPath);

            var fileName = PathSafety.EnsureLeafFileName(
                string.IsNullOrWhiteSpace(normalized.AssetFileName)
                    ? Path.GetFileName(normalized.LocalZipPath)
                    : normalized.AssetFileName,
                "Asset filename");
            await using var stream = new FileStream(
                normalized.LocalZipPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var report = PluginPackageValidation.Validate(
                stream,
                normalized.PluginId,
                normalized.GameId,
                normalized.Version,
                _logger);
            if (!report.IsValid)
            {
                return new WorkflowResult<ReleasePublishPreview>(
                    "packageValidationFailed",
                    null,
                    new[] { "The manager would refuse this package." }.Concat(report.Errors).ToArray(),
                    WorkflowErrorKind.Validation);
            }

            stream.Position = 0;
            var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            var tag = $"v{normalized.Version}";
            var releases = await _gitHub.ListReleasesAsync(normalized.SourceRepo, ct: ct);
            var hasTag = releases.Any(release => string.Equals(release.TagName, tag, StringComparison.Ordinal));
            var replacesAsset = false;
            if (hasTag)
            {
                var url = GitHubService.BuildAssetUrl(normalized.SourceRepo, tag, fileName);
                var state = await _assets.ProbeAsync(url, ct);
                if (state.Status == PublishedAssetStatus.Unreadable ||
                    state.Status == PublishedAssetStatus.Found && string.IsNullOrWhiteSpace(state.Sha256))
                {
                    return Failure<ReleasePublishPreview>(
                        WorkflowErrorKind.Conflict,
                        "publishedAssetUnreadable",
                        $"The release tag '{tag}' exists, but the current asset couldn't be read safely. Nothing was uploaded.");
                }

                replacesAsset = state.Status == PublishedAssetStatus.Found &&
                                !string.Equals(state.Sha256, sha, StringComparison.OrdinalIgnoreCase);
            }

            var preview = new ReleasePublishPreview(
                normalized.SourceRepo,
                tag,
                fileName,
                sha,
                CreatesRelease: !hasTag,
                ReplacesAsset: replacesAsset);
            return new WorkflowResult<ReleasePublishPreview>(
                "releasePreviewed",
                preview,
                new[]
                {
                    $"Release upload is valid and would target {preview.Repository} {preview.Tag} as {preview.AssetFileName}."
                });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Failure<ReleasePublishPreview>(WorkflowErrorKind.Validation, "releasePreviewFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<PreparedRelease>> PrepareAsync(
        ReleasePublishRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var normalized = NormalizeRequest(request);
            if (normalized.Patreon is not null)
            {
                return Failure<PreparedRelease>(
                    WorkflowErrorKind.Validation,
                    "releaseValidationFailed",
                    "A Patreon-gated package cannot be uploaded to a public GitHub release. Use the server or Patreon flow instead.");
            }

            if (!await _gitHub.IsAvailableAsync(ct))
            {
                return Failure<PreparedRelease>(
                    WorkflowErrorKind.Authentication,
                    "githubUnavailable",
                    "GitHub CLI isn't installed or available on PATH.");
            }

            if (!await _gitHub.IsAuthenticatedAsync(ct))
            {
                return Failure<PreparedRelease>(
                    WorkflowErrorKind.Authentication,
                    "githubAuthenticationRequired",
                    "GitHub CLI isn't signed in. Run 'gh auth login' and try again.");
            }

            var isPrivate = await _gitHub.IsRepoPrivateAsync(normalized.SourceRepo, ct);
            if (isPrivate is true)
            {
                return Failure<PreparedRelease>(
                    WorkflowErrorKind.Validation,
                    "privateRepositoryRefused",
                    $"Repository '{normalized.SourceRepo}' is private. Its release assets cannot be downloaded anonymously by the mod manager.");
            }

            if (isPrivate is null)
            {
                return Failure<PreparedRelease>(
                    WorkflowErrorKind.Conflict,
                    "repositoryVisibilityUnknown",
                    $"Couldn't verify whether '{normalized.SourceRepo}' is public. Nothing was staged or uploaded.");
            }

            var prepared = Stage(normalized);
            try
            {
                var report = PluginPackageValidation.Validate(
                    prepared.Stream,
                    normalized.PluginId,
                    normalized.GameId,
                    normalized.Version,
                    _logger);
                if (!report.IsValid)
                {
                    await prepared.DisposeAsync();
                    return new WorkflowResult<PreparedRelease>(
                        "packageValidationFailed",
                        null,
                        new[] { "The manager would refuse this package." }.Concat(report.Errors).ToArray(),
                        WorkflowErrorKind.Validation,
                        new[] { "packageStaged" });
                }

                var releases = await _gitHub.ListReleasesAsync(normalized.SourceRepo, ct: ct);
                var hasTag = releases.Any(release =>
                    string.Equals(release.TagName, prepared.Preview.Tag, StringComparison.Ordinal));
                var assetAlreadyMatches = false;
                var replacesAsset = false;

                if (hasTag)
                {
                    var assetUrl = GitHubService.BuildAssetUrl(
                        normalized.SourceRepo,
                        prepared.Preview.Tag,
                        prepared.Preview.AssetFileName);
                    var published = await _assets.ProbeAsync(assetUrl, ct);
                    if (published.Status == PublishedAssetStatus.Unreadable ||
                        published.Status == PublishedAssetStatus.Found && string.IsNullOrWhiteSpace(published.Sha256))
                    {
                        await prepared.DisposeAsync();
                        return new WorkflowResult<PreparedRelease>(
                            "publishedAssetUnreadable",
                            null,
                            new[]
                            {
                                $"The release tag '{prepared.Preview.Tag}' exists, but the current asset couldn't be read safely. Nothing was uploaded."
                            },
                            WorkflowErrorKind.Conflict,
                            new[] { "packageStaged", "packageValidated" });
                    }

                    if (published.Status == PublishedAssetStatus.Found)
                    {
                        assetAlreadyMatches = string.Equals(
                            published.Sha256,
                            prepared.Sha256,
                            StringComparison.OrdinalIgnoreCase);
                        replacesAsset = !assetAlreadyMatches;
                    }
                }

                var preview = prepared.Preview with
                {
                    CreatesRelease = !hasTag,
                    ReplacesAsset = replacesAsset
                };
                var ready = new PreparedRelease(
                    Path.GetDirectoryName(prepared.StagedPath)!,
                    prepared.StagedPath,
                    prepared.Stream,
                    prepared.Sha256,
                    preview,
                    normalized,
                    assetAlreadyMatches);
                prepared.Detach();

                return new WorkflowResult<PreparedRelease>(
                    "releasePrepared",
                    ready,
                    new[]
                    {
                        $"Prepared {preview.AssetFileName} for {preview.Repository} {preview.Tag}; SHA256 {preview.Sha256}."
                    },
                    completedPhases: new[] { "packageStaged", "packageValidated" });
            }
            catch
            {
                await prepared.DisposeAsync();
                throw;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _logger.Warning(ex, "Release preparation failed");
            return Failure<PreparedRelease>(WorkflowErrorKind.Validation, "releasePreparationFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<ReleasePublishResult>> PublishAsync(
        PreparedRelease prepared,
        ReleasePublishRequest request,
        bool confirmed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(request);
        prepared.ThrowIfDisposed();

        var normalized = NormalizeRequest(request);
        if (prepared.Request is null || !Equivalent(prepared.Request, normalized))
        {
            return Failure<ReleasePublishResult>(
                WorkflowErrorKind.Conflict,
                "preparedReleaseMismatch",
                "The release request changed after the package was staged. Prepare it again so the validated bytes and metadata remain bound together.",
                new[] { "packageStaged", "packageValidated" });
        }

        if (prepared.Preview.ReplacesAsset && !confirmed)
        {
            return Failure<ReleasePublishResult>(
                WorkflowErrorKind.Conflict,
                "confirmationRequired",
                $"Publishing would replace {prepared.Preview.AssetFileName} on {prepared.Preview.Repository} {prepared.Preview.Tag}. Confirm that exact replacement before uploading.",
                new[] { "packageStaged", "packageValidated" });
        }

        var phases = new List<string> { "packageStaged", "packageValidated" };
        var notes = string.IsNullOrWhiteSpace(normalized.Notes)
            ? $"Release {prepared.Preview.Tag} for the Accessibility Mod Manager."
            : normalized.Notes!;

        try
        {
            if (prepared.Preview.CreatesRelease)
            {
                var created = await _gitHub.CreateReleaseAsync(
                    prepared.Preview.Repository,
                    prepared.Preview.Tag,
                    prepared.Preview.Tag,
                    notes,
                    new[] { prepared.StagedPath },
                    ct);
                if (!created.Success)
                {
                    return Failure<ReleasePublishResult>(
                        WorkflowErrorKind.Conflict,
                        "githubReleaseCreateFailed",
                        $"GitHub release creation failed: {created.Combined}",
                        phases);
                }

                phases.Add("githubReleaseCreated");
            }
            else
            {
                if (prepared.AssetAlreadyMatches)
                {
                    phases.Add("githubAssetAlreadyMatched");
                }
                else
                {
                    var uploaded = await _gitHub.UploadReleaseAssetAsync(
                        prepared.Preview.Repository,
                        prepared.Preview.Tag,
                        prepared.StagedPath,
                        clobber: prepared.Preview.ReplacesAsset,
                        ct);
                    if (!uploaded.Success)
                    {
                        return Failure<ReleasePublishResult>(
                            WorkflowErrorKind.Conflict,
                            "githubAssetUploadFailed",
                            $"GitHub asset upload failed: {uploaded.Combined}",
                            phases);
                    }

                    phases.Add("githubAssetUploaded");
                }

                if (!string.IsNullOrWhiteSpace(normalized.Notes))
                {
                    var edited = await _gitHub.EditReleaseNotesAsync(
                        prepared.Preview.Repository,
                        prepared.Preview.Tag,
                        notes,
                        ct);
                    if (!edited.Success)
                    {
                        return Failure<ReleasePublishResult>(
                            WorkflowErrorKind.Conflict,
                            "githubNotesUpdateFailed",
                            $"The asset phase completed, but GitHub release notes couldn't be updated: {edited.Combined}",
                            phases);
                    }

                    phases.Add("githubNotesUpdated");
                }
            }

            var assetUrl = GitHubService.BuildAssetUrl(
                prepared.Preview.Repository,
                prepared.Preview.Tag,
                prepared.Preview.AssetFileName);
            var release = new ModRelease
            {
                GameId = normalized.GameId,
                PluginId = normalized.PluginId,
                Version = normalized.Version,
                Channel = normalized.Channel,
                PackageUrl = assetUrl,
                Sha256 = prepared.Sha256,
                ChangelogUrl = NullIfBlank(normalized.ChangelogUrl),
                Notes = NullIfBlank(normalized.Notes),
                Patreon = null
            };
            var result = new ReleasePublishResult(
                release,
                assetUrl.AbsoluteUri,
                prepared.Sha256,
                phases.ToArray());

            return new WorkflowResult<ReleasePublishResult>(
                "releaseUploaded",
                result,
                new[] { $"Published {prepared.Preview.AssetFileName} at {assetUrl}." },
                completedPhases: result.CompletedPhases);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GitHub release publication failed after phases {Phases}", phases);
            return Failure<ReleasePublishResult>(
                WorkflowErrorKind.Conflict,
                "githubReleasePublishFailed",
                ex.Message,
                phases);
        }
    }

    private static ReleasePublishRequest NormalizeRequest(ReleasePublishRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceRepo);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LocalZipPath);

        if (request.ChangelogUrl is { Length: > 0 } changelog &&
            (!Uri.TryCreate(changelog, UriKind.Absolute, out var changelogUri) ||
             !string.Equals(changelogUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Changelog URL must be an absolute https:// URL.");
        }

        return request with
        {
            ProjectPath = Path.GetFullPath(request.ProjectPath),
            PluginId = request.PluginId.Trim(),
            GameId = request.GameId.Trim(),
            Version = request.Version.Trim(),
            Channel = request.Channel.Trim(),
            SourceRepo = NormalizeRepo(request.SourceRepo),
            LocalZipPath = Path.GetFullPath(request.LocalZipPath),
            AssetFileName = NullIfBlank(request.AssetFileName),
            Notes = NullIfBlank(request.Notes),
            ChangelogUrl = NullIfBlank(request.ChangelogUrl)
        };
    }

    private static string NormalizeRepo(string repo)
    {
        var value = repo.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("GitHub repository URLs must use https://github.com/owner/name.");
            }

            value = uri.AbsolutePath.Trim('/');
        }

        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("GitHub repository must be written as owner/name.");
        return $"{parts[0]}/{parts[1]}";
    }

    private static MutablePreparedRelease Stage(ReleasePublishRequest request)
    {
        if (!File.Exists(request.LocalZipPath))
        {
            throw new FileNotFoundException(
                $"The wrapped ZIP isn't there: {request.LocalZipPath}",
                request.LocalZipPath);
        }

        var fileName = PathSafety.EnsureLeafFileName(
            string.IsNullOrWhiteSpace(request.AssetFileName)
                ? Path.GetFileName(request.LocalZipPath)
                : request.AssetFileName,
            "Asset filename");
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "AccessibilityModManager.AuthorTool",
            "publish",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var stagedPath = Path.Combine(tempDirectory, fileName);
            File.Copy(request.LocalZipPath, stagedPath);
            var stream = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
                stream.Position = 0;
                var tag = $"v{request.Version}";
                var preview = new ReleasePublishPreview(
                    request.SourceRepo,
                    tag,
                    fileName,
                    sha,
                    CreatesRelease: false,
                    ReplacesAsset: false);
                return new MutablePreparedRelease(tempDirectory, stagedPath, stream, sha, preview, request);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch
        {
            TryDelete(tempDirectory);
            throw;
        }
    }

    private static bool Equivalent(ReleasePublishRequest left, ReleasePublishRequest right) =>
        left == right;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static WorkflowResult<T> Failure<T>(
        WorkflowErrorKind kind,
        string status,
        string message,
        IReadOnlyList<string>? completedPhases = null) =>
        new(status, default, new[] { message }, kind, completedPhases);

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class MutablePreparedRelease : IAsyncDisposable
    {
        private readonly string _tempDirectory;
        private bool _detached;

        public MutablePreparedRelease(
            string tempDirectory,
            string stagedPath,
            FileStream stream,
            string sha256,
            ReleasePublishPreview preview,
            ReleasePublishRequest request)
        {
            _tempDirectory = tempDirectory;
            StagedPath = stagedPath;
            Stream = stream;
            Sha256 = sha256;
            Preview = preview;
            Request = request;
        }

        public string StagedPath { get; }
        public FileStream Stream { get; }
        public string Sha256 { get; }
        public ReleasePublishPreview Preview { get; }
        public ReleasePublishRequest Request { get; }

        public void Detach() => _detached = true;

        public ValueTask DisposeAsync()
        {
            if (_detached)
                return ValueTask.CompletedTask;
            Stream.Dispose();
            TryDelete(_tempDirectory);
            return ValueTask.CompletedTask;
        }
    }
}
