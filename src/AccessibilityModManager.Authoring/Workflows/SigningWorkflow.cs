using System.Text.Json;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Authoring.Workflows;

public sealed record SigningKeyStatus(
    string PluginId,
    string KeyId,
    string PublicKeyFingerprint,
    bool ImportedFromBackup,
    bool HasPublisherHead)
{
    /// <summary>The public half authors copy into the signed registry. It is not secret.</summary>
    public string? PublicKeyPem { get; init; }
}

public sealed record ClaimPublishPreview(
    string PluginId,
    string KeyId,
    long PublishNumber,
    IReadOnlyList<string> Changes,
    string DeletionsToken);

/// <summary>A narrow source for the two trust-bearing documents claim signing needs.</summary>
public interface ISigningCatalogSource
{
    Task<string> ReadVerifiedRegistryAsync(string pluginId, CancellationToken ct);
    Task<ServerUploadService.RemoteIndex> ReadLiveIndexAsync(string pluginId, CancellationToken ct);

    Task PublishExactIndexAsync(
        string pluginId,
        byte[] indexJson,
        string expectedTrustContext,
        CancellationToken ct) =>
        throw new NotSupportedException("This signing source is read-only.");
}

/// <summary>Production implementation backed by the signed registry and configured SFTP server.</summary>
public sealed class SigningCatalogSource(
    RegistryMembershipChecker registryChecker,
    ServerUploadService server,
    AuthorConfigService config) : ISigningCatalogSource
{
    public Task<string> ReadVerifiedRegistryAsync(string pluginId, CancellationToken ct) =>
        new RegistryVerifiedSource(registryChecker).ReadVerifiedAsync(pluginId, ct);

    public Task<ServerUploadService.RemoteIndex> ReadLiveIndexAsync(
        string pluginId,
        CancellationToken ct) =>
        server.ReadPluginIndexAsync(RequireConfig(), pluginId, ct);

    public async Task PublishExactIndexAsync(
        string pluginId,
        byte[] indexJson,
        string expectedTrustContext,
        CancellationToken ct)
    {
        var transport = new ServerUploadPublishTransport(server, RequireConfig());
        var handle = await transport.AcquireLockAsync(pluginId, ct);
        try
        {
            await transport.PublishIndexAsync(
                pluginId,
                indexJson,
                async () =>
                {
                    var registry = await ReadVerifiedRegistryAsync(pluginId, CancellationToken.None);
                    var resolution = IndexProofService.ResolveAnchor(registry, pluginId);
                    if (resolution.Status != IndexTrustStatus.Anchored || resolution.Anchor is null ||
                        !string.Equals(
                            ClaimTrustContext.Compute(resolution.Anchor),
                            expectedTrustContext,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The registry changed before the interrupted publish could be resumed. " +
                            "The prepared bytes were not switched live.");
                    }
                },
                CancellationToken.None);
        }
        finally
        {
            var release = await transport.ReleaseLockAsync(handle, CancellationToken.None);
            if (release == PublishLockRelease.NotOurs)
            {
                throw new InvalidOperationException(
                    "The server publish lock changed while the interrupted publish was being resumed.");
            }
        }
    }

    private ServerUploadConfig RequireConfig() =>
        config.GetServerUploadConfig()
        ?? throw new InvalidOperationException(
            "Server upload is not configured. Configure it before reading or resuming signed catalogs.");
}

public interface ISigningWorkflow
{
    WorkflowResult<SigningKeyStatus> GetStatus(string pluginId);
    WorkflowResult<SigningKeyStatus> Create(string pluginId, string passphrase);
    WorkflowResult<string> Export(string pluginId, string destination, string exportPassphrase);
    WorkflowResult<SigningKeyStatus> Import(string source, string importPassphrase);
    WorkflowResult<SigningKeyStatus> ChangePassphrase(
        string pluginId,
        string currentPassphrase,
        string newPassphrase);
    Task<WorkflowResult<ClaimPublishPreview>> PreviewClaimsAsync(string projectPath, CancellationToken ct);
    Task<WorkflowResult<IndexProofService.PreparedPublish>> SignClaimsAsync(
        string projectPath,
        string deletionsToken,
        bool confirmed,
        CancellationToken ct);
    WorkflowResult<IReadOnlyList<PublisherRecord>> GetHeadStatus(string pluginId);
    Task<WorkflowResult<bool>> ConfirmHeadAsync(string projectPath, CancellationToken ct);
    Task<WorkflowResult<bool>> CommitPendingAsync(
        string projectPath,
        bool confirmed,
        CancellationToken ct);
    Task<WorkflowResult<bool>> ResumeHeadAsync(
        string projectPath,
        bool confirmed,
        CancellationToken ct);
}

/// <summary>
/// Headless facade over the existing key, proof, and publisher-journal services. No private-key
/// format, claim validation, or replay decision is duplicated here.
/// </summary>
public sealed class SigningWorkflow(
    ClaimSigningKeyStore keys,
    PublisherHeadStore heads,
    IndexProofService proofs,
    IndexFileService indexFiles,
    ISigningCatalogSource source,
    ILogger logger) : ISigningWorkflow
{
    public WorkflowResult<SigningKeyStatus> GetStatus(string pluginId)
    {
        try
        {
            var signing = keys.TryGet(pluginId);
            if (signing is null)
            {
                return Success(
                    "signingKeyAbsent",
                    new SigningKeyStatus(pluginId, "", "", false, false),
                    $"No signing key is stored for '{pluginId}'.");
            }

            return Success("signingKeyStatus", Status(signing),
                $"Signing key '{signing.KeyId}' is stored for '{pluginId}'.");
        }
        catch (Exception ex)
        {
            return Failure<SigningKeyStatus>("signingStatusFailed", ex.Message);
        }
    }

    public WorkflowResult<SigningKeyStatus> Create(string pluginId, string passphrase)
    {
        try
        {
            var signing = keys.Create(pluginId, passphrase);
            return Success(
                "signingKeyCreated",
                Status(signing),
                $"Created signing key '{signing.KeyId}' for '{pluginId}'. Export a backup before publishing.");
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not create signing key for {PluginId}", pluginId);
            return Failure<SigningKeyStatus>(
                "signingKeyCreateFailed", ex.Message, WorkflowErrorKind.Validation);
        }
    }

    public WorkflowResult<string> Export(
        string pluginId,
        string destination,
        string exportPassphrase)
    {
        try
        {
            var fullDestination = Path.GetFullPath(destination);
            keys.Export(pluginId, fullDestination, exportPassphrase);
            return Success("signingKeyExported", fullDestination,
                $"Wrote the encrypted signing-key backup to {fullDestination}.");
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not export signing key for {PluginId}", pluginId);
            return Failure<string>(
                "signingKeyExportFailed", ex.Message, WorkflowErrorKind.Validation);
        }
    }

    public WorkflowResult<SigningKeyStatus> Import(string sourcePath, string importPassphrase)
    {
        try
        {
            var fullSource = Path.GetFullPath(sourcePath);
            using var document = JsonDocument.Parse(File.ReadAllText(fullSource), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var pluginId = document.RootElement.GetProperty("pluginId").GetString();
            if (string.IsNullOrWhiteSpace(pluginId))
                throw new InvalidOperationException("That backup has no pluginId.");

            var expectedFingerprint = keys.TryGet(pluginId)?.PublicKeyFingerprint;
            var signing = keys.Import(fullSource, importPassphrase, pluginId, expectedFingerprint);
            return Success(
                "signingKeyImported",
                Status(signing),
                $"Restored signing key '{signing.KeyId}' for '{pluginId}'. Its publishing history remains unconfirmed until a live publish is checked.");
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not import a signing-key backup");
            return Failure<SigningKeyStatus>(
                "signingKeyImportFailed", ex.Message, WorkflowErrorKind.Validation);
        }
    }

    public WorkflowResult<SigningKeyStatus> ChangePassphrase(
        string pluginId,
        string currentPassphrase,
        string newPassphrase)
    {
        try
        {
            keys.ChangePassphrase(pluginId, currentPassphrase, newPassphrase);
            var signing = keys.TryGet(pluginId)
                ?? throw new InvalidOperationException("The signing key disappeared after its passphrase changed.");
            return Success("signingPassphraseChanged", Status(signing),
                $"Changed the local passphrase for signing key '{signing.KeyId}'.");
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not change signing passphrase for {PluginId}", pluginId);
            return Failure<SigningKeyStatus>(
                "signingPassphraseChangeFailed", ex.Message, WorkflowErrorKind.Validation);
        }
    }

    public async Task<WorkflowResult<ClaimPublishPreview>> PreviewClaimsAsync(
        string projectPath,
        CancellationToken ct)
    {
        try
        {
            var context = await ReadContextAsync(projectPath, ct);
            var outlook = proofs.PreviewPublish(
                context.LocalIndex,
                context.Registry,
                context.PluginId,
                context.LiveIndex);
            var live = proofs.InspectLive(context.Anchor, context.LiveIndex);
            var key = keys.TryGet(context.PluginId)
                ?? throw new InvalidOperationException($"No signing key is stored for '{context.PluginId}'.");
            var preview = new ClaimPublishPreview(
                context.PluginId,
                key.KeyId,
                (live.Generation ?? 0) + 1,
                Describe(outlook.Changes),
                outlook.DeletionsToken);
            return Success("claimPublishPreviewed", preview,
                $"Claim publish {preview.PublishNumber} was previewed without signing or journalling anything.");
        }
        catch (Exception ex)
        {
            return Failure<ClaimPublishPreview>("claimPublishPreviewFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<IndexProofService.PreparedPublish>> SignClaimsAsync(
        string projectPath,
        string deletionsToken,
        bool confirmed,
        CancellationToken ct)
    {
        if (!confirmed)
        {
            return Failure<IndexProofService.PreparedPublish>(
                "confirmationRequired",
                "Signing claims journals an exact publish and requires --yes after reviewing claims preview.");
        }

        try
        {
            var context = await ReadContextAsync(projectPath, ct);
            var live = proofs.InspectLive(context.Anchor, context.LiveIndex);
            var prepared = proofs.PreparePublish(
                context.LocalIndex,
                context.Registry,
                context.PluginId,
                context.LiveIndex,
                allowBootstrap: !live.Signed,
                acknowledgeRestoredState: true,
                confirmedDeletions: deletionsToken);
            return Success(
                "claimsSigned",
                prepared,
                $"Signed and journalled publish {prepared.Pending.Generation}. No bytes were uploaded; use signing head resume to send this exact publish.");
        }
        catch (Exception ex)
        {
            return Failure<IndexProofService.PreparedPublish>("claimSigningFailed", ex.Message);
        }
    }

    public WorkflowResult<IReadOnlyList<PublisherRecord>> GetHeadStatus(string pluginId)
    {
        try
        {
            var records = heads.RecordsFor(pluginId);
            return Success<IReadOnlyList<PublisherRecord>>(
                "publisherHeadStatus",
                records,
                records.Count == 0
                    ? $"No publishing history is recorded for '{pluginId}'."
                    : $"Found {records.Count} publishing-history record(s) for '{pluginId}'.");
        }
        catch (Exception ex)
        {
            return Failure<IReadOnlyList<PublisherRecord>>("publisherHeadStatusFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<bool>> ConfirmHeadAsync(string projectPath, CancellationToken ct)
    {
        try
        {
            var context = await ReadContextAsync(projectPath, ct);
            if (context.LiveIndex is null)
                throw new InvalidOperationException("There is no published index to confirm.");
            proofs.ConfirmPublished(context.Anchor, context.LiveIndex);
            return Success("publisherHeadConfirmed", true,
                "The live bytes exactly match the pending publish, which is now committed locally.");
        }
        catch (Exception ex)
        {
            return Failure<bool>("publisherHeadConfirmFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<bool>> CommitPendingAsync(
        string projectPath,
        bool confirmed,
        CancellationToken ct)
    {
        if (!confirmed)
            return Failure<bool>("confirmationRequired", "Committing a pending publisher head requires --yes.");

        try
        {
            var context = await ReadContextAsync(projectPath, ct);
            var outcome = proofs.ResolvePending(context.Anchor, context.LiveIndex);
            if (outcome != IndexProofService.PendingOutcome.Landed)
            {
                return Failure<bool>(
                    "pendingPublishNotLive",
                    outcome == IndexProofService.PendingOutcome.NotSent
                        ? "The pending publish never reached the server. Resume it instead of committing it."
                        : "The live catalog diverges from both the pending publish and its parent. Nothing was committed.");
            }

            proofs.CommitPending(context.Anchor);
            return Success("pendingPublishCommitted", true,
                "The exact pending publish is live and has been committed locally.");
        }
        catch (Exception ex)
        {
            return Failure<bool>("pendingPublishCommitFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<bool>> ResumeHeadAsync(
        string projectPath,
        bool confirmed,
        CancellationToken ct)
    {
        if (!confirmed)
            return Failure<bool>("confirmationRequired", "Resuming an interrupted publish requires --yes.");

        try
        {
            var context = await ReadContextAsync(projectPath, ct);
            var outcome = proofs.ResolvePending(context.Anchor, context.LiveIndex);
            if (outcome == IndexProofService.PendingOutcome.Landed)
            {
                proofs.CommitPending(context.Anchor);
                return Success("pendingPublishRecovered", true,
                    "The interrupted publish had already landed and is now committed locally.");
            }

            if (outcome == IndexProofService.PendingOutcome.Diverged)
            {
                return Failure<bool>(
                    "pendingPublishDiverged",
                    "The live catalog is neither the pending publish nor its parent. Nothing was uploaded or committed.");
            }

            var exactBytes = proofs.ReadPendingIndex(context.Anchor);
            var trustContext = ClaimTrustContext.Compute(context.Anchor);
            await source.PublishExactIndexAsync(
                context.PluginId,
                exactBytes,
                trustContext,
                CancellationToken.None);
            var readBack = await source.ReadLiveIndexAsync(context.PluginId, CancellationToken.None);
            if (!readBack.Present || readBack.Bytes is null)
                throw new InvalidOperationException("The server offered no index after the resumed publish.");
            proofs.ConfirmPublished(context.Anchor, readBack.Bytes);
            return Success("pendingPublishResumed", true,
                "The exact journalled bytes were published, read back, and committed locally.");
        }
        catch (Exception ex)
        {
            return Failure<bool>("pendingPublishResumeFailed", ex.Message);
        }
    }

    private SigningKeyStatus Status(ClaimSigningConfig signing) =>
        new(
            signing.PluginId,
            signing.KeyId,
            signing.PublicKeyFingerprint,
            signing.ImportedFromBackup,
            heads.RecordsFor(signing.PluginId).Any(record =>
                record.Committed is not null || record.Pending is not null))
        {
            PublicKeyPem = signing.PublicKeyPem
        };

    private async Task<SigningContext> ReadContextAsync(string projectPath, CancellationToken ct)
    {
        var fullProjectPath = Path.GetFullPath(projectPath);
        var index = indexFiles.Load(fullProjectPath);
        var local = await File.ReadAllBytesAsync(IndexFileService.GetIndexPath(fullProjectPath), ct);
        var registry = await source.ReadVerifiedRegistryAsync(index.PluginId, ct);
        var resolution = IndexProofService.ResolveAnchor(registry, index.PluginId);
        var anchor = resolution.Status switch
        {
            IndexTrustStatus.Anchored => resolution.Anchor!,
            IndexTrustStatus.Unusable => throw new InvalidOperationException(
                $"The registry's signing key for '{index.PluginId}' cannot be used: {resolution.Reason}"),
            _ => throw new InvalidOperationException(
                $"The registry has no signing key recorded for '{index.PluginId}'.")
        };
        var remote = await source.ReadLiveIndexAsync(index.PluginId, ct);
        if (remote.Present && remote.Bytes is null)
            throw new InvalidOperationException("The server reported a live index but returned no bytes.");
        return new SigningContext(index.PluginId, local, registry, anchor, remote.Present ? remote.Bytes : null);
    }

    private static IReadOnlyList<string> Describe(ClaimSetBuilder.PublishPreview preview)
    {
        var changes = new List<string>
        {
            $"{preview.Added} added",
            $"{preview.Updated} updated",
            $"{preview.Unchanged} unchanged"
        };
        changes.AddRange(preview.RemovedReleases.Select(item => $"Permanently withdraw {item.Describe()}"));
        changes.AddRange(preview.RemovedGames.Select(item => $"Remove {item.Describe()}"));
        changes.AddRange(preview.Narrowed.Select(item => $"Narrow access to {item.Describe()}"));
        changes.AddRange(preview.BlockedReleases.Select(item => $"Blocked withdrawn release {item.Describe()}"));
        return changes;
    }

    private sealed record SigningContext(
        string PluginId,
        byte[] LocalIndex,
        string Registry,
        ClaimTrustAnchor Anchor,
        byte[]? LiveIndex);

    private static WorkflowResult<T> Success<T>(string status, T value, string message) =>
        new(status, value, new[] { message });

    private static WorkflowResult<T> Failure<T>(
        string status,
        string message,
        WorkflowErrorKind kind = WorkflowErrorKind.Conflict) =>
        new(status, default, new[] { message }, kind);
}
