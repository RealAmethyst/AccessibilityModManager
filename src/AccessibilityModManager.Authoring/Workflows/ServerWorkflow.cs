using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Authoring.Workflows;

public sealed record ServerConfigurationInput(
    ServerUploadConfig Config,
    string KeyPassphrase);

public sealed record ServerConfigurationStatus(
    bool IsConfigured,
    string? Host,
    int? Port,
    string? User,
    string? HostKeyFingerprint,
    string? PrivateKeyPath,
    bool HasKeyPassphrase,
    string? RemoteBasePath,
    string? RemoteCatalogRoot,
    string? RemoteLockRoot,
    string? PublicBaseUrl);

public sealed record ServerConnectionReport(
    bool Connected,
    IReadOnlyList<ServerCheckStep> Steps);

public sealed record ServerReleaseRequest(
    string GameId,
    string Version,
    string AssetFileName,
    string LocalZipPath,
    PatreonGate? Gate);

public sealed record ServerReleaseInspection(
    string GameId,
    string Version,
    string AssetFileName,
    string Sha256,
    string PublicUrl,
    ServerUploadService.RemoteReleaseState Remote);

public sealed record ServerReleasePublishResult(
    string GameId,
    string Version,
    string AssetFileName,
    string Sha256,
    ServerUploadService.ReleasePublishOutcome Outcome);

public interface IServerAuthorTransport
{
    Task<ServerConnectionReport> TestAsync(ServerUploadConfig config, CancellationToken ct);
    Task<IReadOnlyList<ServerCheckStep>> SelfTestAsync(
        ServerUploadConfig config,
        string pluginId,
        CancellationToken ct);
    Task<ServerUploadService.RemoteReleaseState> InspectReleaseAsync(
        ServerUploadConfig config,
        ServerReleaseRequest request,
        Stream package,
        string sha256,
        CancellationToken ct);
    Task<ServerUploadService.ReleasePublishOutcome> PublishReleaseAsync(
        ServerUploadConfig config,
        ServerReleaseRequest request,
        Stream package,
        string sha256,
        CancellationToken ct);
    Task<bool> GateExistsAsync(
        ServerUploadConfig config,
        string gameId,
        string version,
        CancellationToken ct);
    Task PublishGateAsync(
        ServerUploadConfig config,
        string gameId,
        string version,
        PatreonGate gate,
        CancellationToken ct);
    Task RemoveGateAsync(
        ServerUploadConfig config,
        string gameId,
        string version,
        CancellationToken ct);
    Task<ServerUploadService.RemoteLock> InspectLockAsync(
        ServerUploadConfig config,
        string pluginId,
        CancellationToken ct);
    Task<bool> BreakLockAsync(
        ServerUploadConfig config,
        string pluginId,
        string expectedFingerprint,
        CancellationToken ct);
}

public sealed class ServerAuthorTransport(
    ServerUploadService server,
    RegistryMembershipChecker registry) : IServerAuthorTransport
{
    public async Task<ServerConnectionReport> TestAsync(
        ServerUploadConfig config,
        CancellationToken ct)
    {
        var error = await server.TestConnectionAsync(config, ct);
        return new ServerConnectionReport(
            error is null,
            new[]
            {
                new ServerCheckStep(
                    "Connect and verify writable paths",
                    error is null,
                    error ?? "Authenticated with the pinned host key and verified the configured paths.")
            });
    }

    public Task<IReadOnlyList<ServerCheckStep>> SelfTestAsync(
        ServerUploadConfig config,
        string pluginId,
        CancellationToken ct)
    {
        var transport = new ServerUploadPublishTransport(server, config);
        return ServerSelfTest.RunAsync(
            transport,
            pluginId,
            ct,
            rehearsal: transport,
            registry: new RegistryVerifiedSource(registry));
    }

    public Task<ServerUploadService.RemoteReleaseState> InspectReleaseAsync(
        ServerUploadConfig config,
        ServerReleaseRequest request,
        Stream package,
        string sha256,
        CancellationToken ct) =>
        server.ProbeReleaseAsync(
            config,
            request.GameId,
            request.Version,
            request.AssetFileName,
            package,
            sha256,
            ct);

    public Task<ServerUploadService.ReleasePublishOutcome> PublishReleaseAsync(
        ServerUploadConfig config,
        ServerReleaseRequest request,
        Stream package,
        string sha256,
        CancellationToken ct) =>
        server.PublishReleaseAsync(
            config,
            request.GameId,
            request.Version,
            request.AssetFileName,
            package,
            sha256,
            request.Gate,
            ct);

    public Task<bool> GateExistsAsync(
        ServerUploadConfig config,
        string gameId,
        string version,
        CancellationToken ct) =>
        server.GateExistsAsync(config, gameId, version, ct);

    public Task PublishGateAsync(
        ServerUploadConfig config,
        string gameId,
        string version,
        PatreonGate gate,
        CancellationToken ct) =>
        server.PublishGateOnlyAsync(config, gameId, version, gate, ct);

    public Task RemoveGateAsync(
        ServerUploadConfig config,
        string gameId,
        string version,
        CancellationToken ct) =>
        server.RemoveGateAsync(config, gameId, version, ct);

    public Task<ServerUploadService.RemoteLock> InspectLockAsync(
        ServerUploadConfig config,
        string pluginId,
        CancellationToken ct) =>
        server.ReadPublishLockAsync(config, pluginId, ct);

    public Task<bool> BreakLockAsync(
        ServerUploadConfig config,
        string pluginId,
        string expectedFingerprint,
        CancellationToken ct) =>
        server.BreakPublishLockAsync(config, pluginId, expectedFingerprint, ct);
}

public interface IServerWorkflow
{
    WorkflowResult<ServerConfigurationStatus> GetStatus();
    WorkflowResult<ServerConfigurationStatus> Configure(ServerConfigurationInput input, bool dryRun);
    WorkflowResult<bool> Clear(bool confirmed, bool dryRun);
    Task<WorkflowResult<ServerConnectionReport>> TestAsync(CancellationToken ct);
    Task<WorkflowResult<ServerConnectionReport>> SelfTestAsync(string pluginId, CancellationToken ct);
    Task<WorkflowResult<ServerReleaseInspection>> InspectReleaseAsync(
        string pluginId,
        ServerReleaseRequest request,
        CancellationToken ct);
    Task<WorkflowResult<ServerReleaseInspection>> InspectPreparedReleaseAsync(
        string pluginId,
        ServerReleaseRequest request,
        PreparedRelease package,
        CancellationToken ct);
    Task<WorkflowResult<ServerReleasePublishResult>> UploadReleaseAsync(
        string pluginId,
        ServerReleaseRequest request,
        bool confirmed,
        bool dryRun,
        CancellationToken ct);
    Task<WorkflowResult<ServerReleasePublishResult>> UploadPreparedReleaseAsync(
        string pluginId,
        ServerReleaseRequest request,
        PreparedRelease package,
        bool confirmed,
        bool dryRun,
        CancellationToken ct);
    Task<WorkflowResult<bool>> SetGateAsync(
        string gameId,
        string version,
        PatreonGate gate,
        bool confirmed,
        bool dryRun,
        CancellationToken ct);
    Task<WorkflowResult<bool>> RemoveGateAsync(
        string gameId,
        string version,
        bool confirmed,
        bool dryRun,
        CancellationToken ct);
    Task<WorkflowResult<ServerUploadService.RemoteLock>> InspectLockAsync(
        string pluginId,
        CancellationToken ct);
    Task<WorkflowResult<bool>> BreakLockAsync(
        string pluginId,
        string expectedFingerprint,
        bool confirmed,
        bool dryRun,
        CancellationToken ct);
}

public sealed class ServerWorkflow(
    AuthorConfigService config,
    IServerAuthorTransport transport,
    IReleaseWorkflow releases) : IServerWorkflow
{
    public WorkflowResult<ServerConfigurationStatus> GetStatus()
    {
        var current = config.GetServerUploadConfig();
        return Success(
            "serverStatus",
            Describe(current),
            current is null ? "Server upload is not configured." : $"Server upload is configured for {current.Host}.");
    }

    public WorkflowResult<ServerConfigurationStatus> Configure(
        ServerConfigurationInput input,
        bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Config);
        var normalized = NormalizeConfig(input.Config, input.KeyPassphrase);
        var validation = ValidateConfig(normalized, requirePublicUrl: true);
        if (validation is not null)
            return Failure<ServerConfigurationStatus>(WorkflowErrorKind.Validation, "serverConfigurationInvalid", validation);

        if (!dryRun)
            config.SaveServerUploadConfig(normalized);
        return Success(
            dryRun ? "serverConfigurationPreviewed" : "serverConfigured",
            Describe(normalized),
            dryRun
                ? $"The server configuration for {normalized.Host} is valid and would be saved."
                : $"Saved the server configuration for {normalized.Host}.");
    }

    public WorkflowResult<bool> Clear(bool confirmed, bool dryRun)
    {
        if (!dryRun && !confirmed)
            return Failure<bool>(WorkflowErrorKind.Conflict, "confirmationRequired", "Clearing the saved server configuration requires confirmation.");
        if (!dryRun)
            config.SaveServerUploadConfig(null);
        return Success(
            dryRun ? "serverClearPreviewed" : "serverCleared",
            true,
            dryRun ? "The saved server configuration would be removed." : "Removed the saved server configuration.");
    }

    public async Task<WorkflowResult<ServerConnectionReport>> TestAsync(CancellationToken ct)
    {
        var current = RequireConfig<ServerConnectionReport>();
        if (current.Failure is not null) return current.Failure;
        try
        {
            var report = await transport.TestAsync(current.Config!, ct);
            return report.Connected
                ? Success("serverConnectionPassed", report, "The server connection and configured paths passed.")
                : new WorkflowResult<ServerConnectionReport>(
                    "serverConnectionFailed",
                    report,
                    report.Steps.Where(step => !step.Ok).Select(step => step.Detail).ToArray(),
                    WorkflowErrorKind.Authentication);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<ServerConnectionReport>(WorkflowErrorKind.Authentication, "serverConnectionFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<ServerConnectionReport>> SelfTestAsync(
        string pluginId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var current = RequireConfig<ServerConnectionReport>();
        if (current.Failure is not null) return current.Failure;
        try
        {
            var steps = await transport.SelfTestAsync(current.Config!, pluginId, ct);
            var report = new ServerConnectionReport(steps.Count > 0 && steps.All(step => step.Ok), steps);
            return report.Connected
                ? Success("serverSelfTestPassed", report, "Every server publishing self-test step passed.")
                : new WorkflowResult<ServerConnectionReport>(
                    "serverSelfTestFailed",
                    report,
                    steps.Where(step => !step.Ok).Select(step => $"{step.Name}: {step.Detail}").ToArray(),
                    WorkflowErrorKind.Conflict);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<ServerConnectionReport>(WorkflowErrorKind.Conflict, "serverSelfTestFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<ServerReleaseInspection>> InspectReleaseAsync(
        string pluginId,
        ServerReleaseRequest request,
        CancellationToken ct)
    {
        var validation = ValidateReleaseRequest(request);
        if (validation is not null)
            return Failure<ServerReleaseInspection>(WorkflowErrorKind.Validation, "serverReleaseInvalid", validation);

        var staged = await releases.StagePackageAsync(
            new PackageStageRequest(pluginId, request.GameId, request.Version, request.LocalZipPath, request.AssetFileName),
            ct);
        if (staged.ErrorKind != WorkflowErrorKind.None || staged.Value is null)
            return ForwardFailure<PreparedRelease, ServerReleaseInspection>(staged);

        await using var package = staged.Value;
        return await InspectPreparedReleaseAsync(pluginId, request, package, ct);
    }

    public async Task<WorkflowResult<ServerReleaseInspection>> InspectPreparedReleaseAsync(
        string pluginId,
        ServerReleaseRequest request,
        PreparedRelease package,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        package.ThrowIfDisposed();
        var current = RequireConfig<ServerReleaseInspection>();
        if (current.Failure is not null) return current.Failure;
        var validation = ValidatePreparedRelease(pluginId, request, package);
        if (validation is not null)
            return Failure<ServerReleaseInspection>(WorkflowErrorKind.Conflict, "preparedReleaseMismatch", validation);

        try
        {
            var normalized = request with { AssetFileName = package.Preview.AssetFileName };
            package.Stream.Position = 0;
            var remote = await transport.InspectReleaseAsync(
                current.Config!,
                normalized,
                package.Stream,
                package.Sha256,
                ct);
            return Success(
                "serverReleaseInspected",
                new ServerReleaseInspection(
                    normalized.GameId,
                    normalized.Version,
                    normalized.AssetFileName,
                    package.Sha256,
                    ServerUploadService.BuildPublicUrl(
                        current.Config!,
                        normalized.GameId,
                        normalized.Version,
                        normalized.AssetFileName),
                    remote),
                DescribeRemote(remote));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<ServerReleaseInspection>(WorkflowErrorKind.Conflict, "serverReleaseInspectionFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<ServerReleasePublishResult>> UploadReleaseAsync(
        string pluginId,
        ServerReleaseRequest request,
        bool confirmed,
        bool dryRun,
        CancellationToken ct)
    {
        var validation = ValidateReleaseRequest(request);
        if (validation is not null)
            return Failure<ServerReleasePublishResult>(WorkflowErrorKind.Validation, "serverReleaseInvalid", validation);
        var staged = await releases.StagePackageAsync(
            new PackageStageRequest(pluginId, request.GameId, request.Version, request.LocalZipPath, request.AssetFileName),
            ct);
        if (staged.ErrorKind != WorkflowErrorKind.None || staged.Value is null)
            return ForwardFailure<PreparedRelease, ServerReleasePublishResult>(staged);

        await using var package = staged.Value;
        return await UploadPreparedReleaseAsync(pluginId, request, package, confirmed, dryRun, ct);
    }

    public async Task<WorkflowResult<ServerReleasePublishResult>> UploadPreparedReleaseAsync(
        string pluginId,
        ServerReleaseRequest request,
        PreparedRelease package,
        bool confirmed,
        bool dryRun,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        package.ThrowIfDisposed();
        var inspection = await InspectPreparedReleaseAsync(pluginId, request, package, ct);
        if (inspection.ErrorKind != WorkflowErrorKind.None || inspection.Value is null)
            return ForwardFailure<ServerReleaseInspection, ServerReleasePublishResult>(inspection);

        var remote = inspection.Value.Remote;
        if (remote.OtherAssets.Count > 0)
        {
            return Failure<ServerReleasePublishResult>(
                WorkflowErrorKind.Conflict,
                "serverVersionFolderOccupied",
                $"The version folder already contains another package: {string.Join(", ", remote.OtherAssets)}.");
        }
        if (remote.PackageExists && !remote.PackageMatches)
        {
            return Failure<ServerReleasePublishResult>(
                WorkflowErrorKind.Conflict,
                "serverReleaseImmutable",
                "This version already exists with different bytes. Bump the version instead of replacing it.");
        }

        if (dryRun)
        {
            return Success(
                "serverReleaseUploadPreviewed",
                new ServerReleasePublishResult(
                    inspection.Value.GameId,
                    inspection.Value.Version,
                    inspection.Value.AssetFileName,
                    inspection.Value.Sha256,
                    new ServerUploadService.ReleasePublishOutcome(false, false, false, false, string.Empty)),
                "The exact validated package is safe to publish; dry run changed nothing.");
        }
        if (!confirmed)
        {
            return Failure<ServerReleasePublishResult>(
                WorkflowErrorKind.Conflict,
                "confirmationRequired",
                "Server release upload requires confirmation after inspecting the exact version folder.");
        }

        var current = RequireConfig<ServerReleasePublishResult>();
        if (current.Failure is not null) return current.Failure;
        try
        {
            var normalized = request with { AssetFileName = package.Preview.AssetFileName };
            package.Stream.Position = 0;
            var outcome = await transport.PublishReleaseAsync(
                current.Config!,
                normalized,
                package.Stream,
                package.Sha256,
                ct);
            return Success(
                "serverReleaseUploaded",
                new ServerReleasePublishResult(
                    normalized.GameId,
                    normalized.Version,
                    normalized.AssetFileName,
                    package.Sha256,
                    outcome),
                outcome.PackageUploaded
                    ? $"Published {normalized.AssetFileName} and verified SHA256 {package.Sha256}."
                    : $"The server already held the same {normalized.AssetFileName}; no package bytes changed.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<ServerReleasePublishResult>(WorkflowErrorKind.Conflict, "serverReleaseUploadFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<bool>> SetGateAsync(
        string gameId,
        string version,
        PatreonGate gate,
        bool confirmed,
        bool dryRun,
        CancellationToken ct)
    {
        var gateError = ValidateGate(gate);
        if (gateError is not null)
            return Failure<bool>(WorkflowErrorKind.Validation, "patreonGateInvalid", gateError);
        if (!dryRun && !confirmed)
            return Failure<bool>(WorkflowErrorKind.Conflict, "confirmationRequired", "Changing the server's Patreon tiers requires confirmation.");

        var current = RequireConfig<bool>();
        if (current.Failure is not null) return current.Failure;
        try
        {
            if (!dryRun)
                await transport.PublishGateAsync(current.Config!, gameId, version, gate, ct);
            return Success(
                dryRun ? "serverGateSetPreviewed" : "serverGateSet",
                true,
                dryRun
                    ? $"The gate for {gameId} {version} would be updated."
                    : $"Updated the gate for {gameId} {version}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<bool>(WorkflowErrorKind.Conflict, "serverGateSetFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<bool>> RemoveGateAsync(
        string gameId,
        string version,
        bool confirmed,
        bool dryRun,
        CancellationToken ct)
    {
        if (!dryRun && !confirmed)
            return Failure<bool>(WorkflowErrorKind.Conflict, "confirmationRequired", "Removing the Patreon gate makes this version public and requires confirmation.");
        var current = RequireConfig<bool>();
        if (current.Failure is not null) return current.Failure;
        try
        {
            var exists = await transport.GateExistsAsync(current.Config!, gameId, version, ct);
            if (!dryRun && exists)
                await transport.RemoveGateAsync(current.Config!, gameId, version, ct);
            return Success(
                dryRun ? "serverGateRemovePreviewed" : "serverGateRemoved",
                exists,
                exists
                    ? dryRun
                        ? $"The gate for {gameId} {version} would be removed, making it public."
                        : $"Removed the gate for {gameId} {version}; it is public now."
                    : $"No gate exists for {gameId} {version}; nothing changed.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<bool>(WorkflowErrorKind.Conflict, "serverGateRemoveFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<ServerUploadService.RemoteLock>> InspectLockAsync(
        string pluginId,
        CancellationToken ct)
    {
        var current = RequireConfig<ServerUploadService.RemoteLock>();
        if (current.Failure is not null) return current.Failure;
        try
        {
            var remote = await transport.InspectLockAsync(current.Config!, pluginId, ct);
            return Success(
                "serverLockInspected",
                remote,
                remote.Present
                    ? $"A publish lock is present with fingerprint {remote.Fingerprint}."
                    : "No publish lock is present.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<ServerUploadService.RemoteLock>(WorkflowErrorKind.Conflict, "serverLockInspectionFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<bool>> BreakLockAsync(
        string pluginId,
        string expectedFingerprint,
        bool confirmed,
        bool dryRun,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        var current = RequireConfig<bool>();
        if (current.Failure is not null) return current.Failure;

        var inspected = await InspectLockAsync(pluginId, ct);
        if (inspected.ErrorKind != WorkflowErrorKind.None || inspected.Value.Fingerprint is null)
            return ForwardFailure<ServerUploadService.RemoteLock, bool>(inspected);
        if (!inspected.Value.Present ||
            !string.Equals(inspected.Value.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            return Failure<bool>(WorkflowErrorKind.Conflict, "serverLockChanged", "The current lock does not match the supplied fingerprint and was left alone.");
        }
        if (dryRun)
            return Success("serverLockBreakPreviewed", true, "The exact displayed server lock would be removed.");
        if (!confirmed)
            return Failure<bool>(WorkflowErrorKind.Conflict, "confirmationRequired", "Breaking a server publish lock requires confirmation.");

        try
        {
            var removed = await transport.BreakLockAsync(
                current.Config!,
                pluginId,
                expectedFingerprint,
                ct);
            return removed
                ? Success("serverLockBroken", true, "Removed the exact displayed server lock.")
                : Failure<bool>(WorkflowErrorKind.Conflict, "serverLockChanged", "The server lock changed before removal and was left alone.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure<bool>(WorkflowErrorKind.Conflict, "serverLockBreakFailed", ex.Message);
        }
    }

    private (ServerUploadConfig? Config, WorkflowResult<T>? Failure) RequireConfig<T>()
    {
        var current = config.GetServerUploadConfig();
        return current is null
            ? (null, Failure<T>(WorkflowErrorKind.Authentication, "serverNotConfigured", "Server upload is not configured."))
            : (current, null);
    }

    private static string? ValidateReleaseRequest(ServerReleaseRequest request)
    {
        if (request is null) return "A server release request is required.";
        if (string.IsNullOrWhiteSpace(request.GameId)) return "Game id is required.";
        if (string.IsNullOrWhiteSpace(request.Version)) return "Version is required.";
        if (string.IsNullOrWhiteSpace(request.AssetFileName)) return "Asset filename is required.";
        if (string.IsNullOrWhiteSpace(request.LocalZipPath)) return "Local package path is required.";
        if (!File.Exists(request.LocalZipPath)) return $"Package not found at {request.LocalZipPath}.";
        return request.Gate is null ? null : ValidateGate(request.Gate);
    }

    private static string? ValidatePreparedRelease(
        string pluginId,
        ServerReleaseRequest request,
        PreparedRelease package)
    {
        if (request is null) return "A server release request is required.";
        var gateError = request.Gate is null ? null : ValidateGate(request.Gate);
        if (gateError is not null) return gateError;

        var staged = package.PackageRequest;
        if (!string.Equals(staged.PluginId, pluginId.Trim(), StringComparison.Ordinal) ||
            !string.Equals(staged.GameId, request.GameId.Trim(), StringComparison.Ordinal) ||
            !string.Equals(staged.Version, request.Version.Trim(), StringComparison.Ordinal) ||
            !string.Equals(package.Preview.AssetFileName, request.AssetFileName.Trim(), StringComparison.Ordinal))
        {
            return "The server request does not match the plugin, game, version, and asset name used to validate the staged package.";
        }

        return null;
    }

    private static string? ValidateGate(PatreonGate gate)
    {
        if (gate is null) return "A Patreon gate is required.";
        if (string.IsNullOrWhiteSpace(gate.CampaignId)) return "Patreon campaign id is required.";
        if (gate.TierIds.Count == 0 || gate.TierIds.Any(string.IsNullOrWhiteSpace))
            return "At least one nonempty Patreon tier id is required.";
        if (gate.TierIds.Distinct(StringComparer.Ordinal).Count() != gate.TierIds.Count)
            return "Patreon tier ids must be unique.";
        return null;
    }

    private static ServerUploadConfig NormalizeConfig(ServerUploadConfig source, string passphrase) =>
        new()
        {
            Host = source.Host?.Trim() ?? string.Empty,
            HostKeyFingerprint = NullIfBlank(source.HostKeyFingerprint),
            User = source.User?.Trim() ?? string.Empty,
            PrivateKeyPath = string.IsNullOrWhiteSpace(source.PrivateKeyPath)
                ? string.Empty
                : Path.GetFullPath(source.PrivateKeyPath.Trim()),
            KeyPassphrase = passphrase ?? string.Empty,
            KeyPassphraseProtected = false,
            RemoteBasePath = source.RemoteBasePath?.Trim() ?? string.Empty,
            RemoteCatalogRoot = source.RemoteCatalogRoot?.Trim() ?? string.Empty,
            RemoteLockRoot = source.RemoteLockRoot?.Trim() ?? string.Empty,
            PublicBaseUrl = source.PublicBaseUrl?.Trim().TrimEnd('/') ?? string.Empty,
            Port = source.Port == 0 ? 22 : source.Port
        };

    private static string? ValidateConfig(ServerUploadConfig value, bool requirePublicUrl)
    {
        if (string.IsNullOrWhiteSpace(value.Host)) return "Server host is required.";
        if (value.Port is < 1 or > 65535) return "SFTP port must be between 1 and 65535.";
        if (string.IsNullOrWhiteSpace(value.HostKeyFingerprint)) return "A verified SSH host-key fingerprint is required.";
        if (string.IsNullOrWhiteSpace(value.User)) return "Server user is required.";
        if (string.IsNullOrWhiteSpace(value.PrivateKeyPath)) return "SSH private key path is required.";
        if (!File.Exists(value.PrivateKeyPath)) return $"SSH private key file not found at '{value.PrivateKeyPath}'.";
        if (string.IsNullOrWhiteSpace(value.RemoteBasePath) || !value.RemoteBasePath.StartsWith('/'))
            return "Remote releases path must be an absolute POSIX path.";
        if (string.IsNullOrWhiteSpace(value.RemoteCatalogRoot) || !value.RemoteCatalogRoot.StartsWith('/'))
            return "Remote catalog path must be an absolute POSIX path.";
        if (!string.IsNullOrWhiteSpace(value.RemoteLockRoot) && !value.RemoteLockRoot.StartsWith('/'))
            return "An explicit remote lock path must be an absolute POSIX path.";
        if (requirePublicUrl &&
            (!Uri.TryCreate(value.PublicBaseUrl, UriKind.Absolute, out var uri) ||
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return "Public download base URL must be an absolute https:// URL.";
        }
        return null;
    }

    private static ServerConfigurationStatus Describe(ServerUploadConfig? value) =>
        value is null
            ? new ServerConfigurationStatus(false, null, null, null, null, null, false, null, null, null, null)
            : new ServerConfigurationStatus(
                true,
                value.Host,
                value.Port,
                value.User,
                value.HostKeyFingerprint,
                value.PrivateKeyPath,
                !string.IsNullOrEmpty(value.KeyPassphrase),
                value.RemoteBasePath,
                value.RemoteCatalogRoot,
                value.RemoteLockRoot,
                value.PublicBaseUrl);

    private static string DescribeRemote(ServerUploadService.RemoteReleaseState remote)
    {
        if (remote.OtherAssets.Count > 0)
            return $"The version folder contains another package: {string.Join(", ", remote.OtherAssets)}.";
        if (!remote.PackageExists)
            return remote.GateExists
                ? "No package exists, but a Patreon gate file is present."
                : "This version is not published on the server yet.";
        return remote.PackageMatches
            ? remote.GateExists
                ? "The exact package already exists and is Patreon-gated."
                : "The exact package already exists and is public."
            : "This version exists with different package bytes.";
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static WorkflowResult<TOut> ForwardFailure<TIn, TOut>(WorkflowResult<TIn> result) =>
        new(result.Status, default, result.Messages, result.ErrorKind, result.CompletedPhases);

    private static WorkflowResult<T> Success<T>(string status, T value, string message) =>
        new(status, value, new[] { message });

    private static WorkflowResult<T> Failure<T>(
        WorkflowErrorKind kind,
        string status,
        string message) =>
        new(status, default, new[] { message }, kind);
}
