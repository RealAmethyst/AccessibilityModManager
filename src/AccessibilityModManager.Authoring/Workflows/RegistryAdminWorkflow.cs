using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.Authoring.Workflows;

public sealed record RegistryAdminStatus(
    bool Enabled,
    string? RepositoryPath,
    string? RegistryJsonPath);

public sealed record RegistryDocumentResult(string Path, string Sha256, bool SignaturePresent);

public sealed record RegistryJsonDocument(string Path, string Sha256, string Content);

public sealed record RegistryPublishResult(
    string Destination,
    string JsonSha256,
    string SignatureSha256,
    IReadOnlyList<string> CompletedPhases);

public interface IRegistryAdminWorkflow
{
    WorkflowResult<RegistryAdminStatus> GetStatus();
    Task<WorkflowResult<RegistryDocumentResult>> OpenAsync(string? registryRepoPath, CancellationToken ct);
    Task<WorkflowResult<RegistryDocumentResult>> RefreshAsync(string registryRepoPath, CancellationToken ct);
    WorkflowResult<RegistryJsonDocument> ShowJson(string registryRepoOrJsonPath);
    WorkflowResult<RegistryDocumentResult> Validate(string registryJsonPath);
    WorkflowResult<RegistryDocumentResult> Save(string registryJsonPath, string content);
    WorkflowResult<RegistryDocumentResult> Sign(
        string registryJsonPath,
        string privateKeyPath,
        string passphrase,
        bool confirmed);
    Task<WorkflowResult<RegistryPublishResult>> PublishAsync(
        string registryRepoPath,
        bool confirmed,
        CancellationToken ct);
    Task<WorkflowResult<ProcessResult>> CommitAsync(
        string registryRepoPath,
        string message,
        CancellationToken ct);
    Task<WorkflowResult<ProcessResult>> PushAsync(string registryRepoPath, CancellationToken ct);
}

/// <summary>
/// Registry maintenance shared by the admin WPF build and CLI. Every public method checks the
/// compile-time gate before touching configuration, files, Git, keys, or the network.
/// </summary>
public sealed class RegistryAdminWorkflow(
    AuthorConfigService config,
    GitService git,
    ServerUploadService server,
    HttpClient http,
    ILogger logger) : IRegistryAdminWorkflow
{
    private const string RegistryFileName = "plugin-registry.json";

    public WorkflowResult<RegistryAdminStatus> GetStatus()
    {
        if (AdminRequired<RegistryAdminStatus>() is { } blocked) return blocked;

        try
        {
            var repo = config.Load().LastRegistryRepoPath ?? DefaultRepoPath();
            var json = FindRegistry(repo, requireExists: false);
            return Success(
                "registryAdminStatus",
                new RegistryAdminStatus(true, repo, json),
                json is null
                    ? $"Registry administration is enabled; no registry JSON was found in {repo}."
                    : $"Registry administration is enabled and {json} is available.");
        }
        catch (Exception ex)
        {
            return Failure<RegistryAdminStatus>("registryStatusFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<RegistryDocumentResult>> OpenAsync(
        string? registryRepoPath,
        CancellationToken ct)
    {
        if (AdminRequired<RegistryDocumentResult>() is { } blocked) return blocked;

        try
        {
            var repo = Path.GetFullPath(string.IsNullOrWhiteSpace(registryRepoPath)
                ? DefaultRepoPath()
                : registryRepoPath);
            if (!await git.IsAvailableAsync(ct))
                return Failure<RegistryDocumentResult>("gitUnavailable", "Git for Windows is required.");

            if (!await git.IsRepoAsync(repo, ct))
            {
                if (Directory.Exists(repo) && Directory.EnumerateFileSystemEntries(repo).Any())
                    return Failure<RegistryDocumentResult>(
                        "registryRepoNotEmpty",
                        $"{repo} exists and is not an empty Git repository, so it was left alone.");

                var clone = await git.CloneAsync(
                    $"https://github.com/{RegistryMembershipChecker.RegistryRepo}.git",
                    repo,
                    ct);
                if (!clone.Success)
                    return ProcessFailure<RegistryDocumentResult>("registryCloneFailed", clone);
            }

            SaveRepoPath(repo);
            var path = FindRegistry(repo, requireExists: true)!;
            var validated = Validate(path);
            return validated.ErrorKind == WorkflowErrorKind.None
                ? new WorkflowResult<RegistryDocumentResult>(
                    "registryOpened",
                    validated.Value,
                    new[] { $"Opened and validated {path}." })
                : validated;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not open the registry repository");
            return Failure<RegistryDocumentResult>("registryOpenFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<RegistryDocumentResult>> RefreshAsync(
        string registryRepoPath,
        CancellationToken ct)
    {
        if (AdminRequired<RegistryDocumentResult>() is { } blocked) return blocked;

        try
        {
            var repo = Path.GetFullPath(registryRepoPath);
            if (!await git.IsRepoAsync(repo, ct))
                return Failure<RegistryDocumentResult>("registryRepoInvalid", $"{repo} is not a Git repository.");
            var pull = await git.PullAsync(repo, ct);
            if (!pull.Success) return ProcessFailure<RegistryDocumentResult>("registryRefreshFailed", pull);
            SaveRepoPath(repo);
            var validated = Validate(FindRegistry(repo, requireExists: true)!);
            return validated.ErrorKind == WorkflowErrorKind.None
                ? new WorkflowResult<RegistryDocumentResult>(
                    "registryRefreshed", validated.Value, new[] { "Pulled the registry repository and validated its JSON." })
                : validated;
        }
        catch (Exception ex)
        {
            return Failure<RegistryDocumentResult>("registryRefreshFailed", ex.Message);
        }
    }

    public WorkflowResult<RegistryJsonDocument> ShowJson(string registryRepoOrJsonPath)
    {
        if (AdminRequired<RegistryJsonDocument>() is { } blocked) return blocked;

        try
        {
            var path = ResolveRegistryPath(registryRepoOrJsonPath);
            var bytes = File.ReadAllBytes(path);
            return Success(
                "registryJsonShown",
                new RegistryJsonDocument(path, Sha(bytes), Encoding.UTF8.GetString(bytes)),
                $"Read {path}.");
        }
        catch (Exception ex)
        {
            return Failure<RegistryJsonDocument>("registryJsonReadFailed", ex.Message);
        }
    }

    public WorkflowResult<RegistryDocumentResult> Validate(string registryJsonPath)
    {
        if (AdminRequired<RegistryDocumentResult>() is { } blocked) return blocked;

        try
        {
            var path = Path.GetFullPath(registryJsonPath);
            var bytes = File.ReadAllBytes(path);
            RequireNoBom(bytes);
            var report = PluginRegistryValidation.Validate(Encoding.UTF8.GetString(bytes));
            if (!report.IsValid)
            {
                return new WorkflowResult<RegistryDocumentResult>(
                    "registryValidationFailed",
                    null,
                    report.Errors,
                    WorkflowErrorKind.Validation);
            }

            return Success(
                "registryValidated",
                Document(path, bytes),
                $"The registry passes the same validation rules used by the manager. SHA256 {Sha(bytes)}.");
        }
        catch (Exception ex)
        {
            return Failure<RegistryDocumentResult>("registryValidationFailed", ex.Message, WorkflowErrorKind.Validation);
        }
    }

    public WorkflowResult<RegistryDocumentResult> Save(string registryJsonPath, string content)
    {
        if (AdminRequired<RegistryDocumentResult>() is { } blocked) return blocked;

        try
        {
            using var _ = JsonDocument.Parse(content);
            var path = Path.GetFullPath(registryJsonPath);
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            DurableFile.Write(path, bytes);
            return Success(
                "registryJsonSaved",
                Document(path, bytes),
                $"Saved {path}. Its previous detached signature is now stale until the file is signed again.");
        }
        catch (Exception ex)
        {
            return Failure<RegistryDocumentResult>("registryJsonSaveFailed", ex.Message, WorkflowErrorKind.Validation);
        }
    }

    public WorkflowResult<RegistryDocumentResult> Sign(
        string registryJsonPath,
        string privateKeyPath,
        string passphrase,
        bool confirmed)
    {
        if (AdminRequired<RegistryDocumentResult>() is { } blocked) return blocked;
        if (!confirmed)
            return Failure<RegistryDocumentResult>(
                "confirmationRequired",
                "Signing the registry requires --yes after its validated hash has been reviewed.");

        try
        {
            var validation = Validate(registryJsonPath);
            if (validation.ErrorKind != WorkflowErrorKind.None || validation.Value is null) return validation;

            var path = validation.Value.Path;
            var bytes = File.ReadAllBytes(path);
            using var rsa = RSA.Create();
            rsa.ImportFromEncryptedPem(File.ReadAllText(Path.GetFullPath(privateKeyPath)), passphrase);
            ClaimKeyPolicy.Require(rsa);
            var fingerprint = ClaimTrustContext.PublicKeyFingerprint(rsa.ExportSubjectPublicKeyInfoPem());
            if (!string.Equals(fingerprint, RegistryTrustKey.ExpectedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Failure<RegistryDocumentResult>(
                    "registryKeyMismatch",
                    $"That private key's public fingerprint is {fingerprint}, not the manager's registry trust key. Nothing was signed.",
                    WorkflowErrorKind.Authentication);
            }

            var signature = Convert.ToBase64String(rsa.SignData(
                bytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss));
            DurableFile.Write(path + ".sig", Encoding.UTF8.GetBytes(signature));
            return Success(
                "registrySigned",
                Document(path, bytes),
                $"Signed the exact registry bytes and wrote {path}.sig.");
        }
        catch (CryptographicException)
        {
            return Failure<RegistryDocumentResult>(
                "registrySigningFailed",
                "The private key could not be opened. The passphrase may be wrong or the key file may be damaged.",
                WorkflowErrorKind.Authentication);
        }
        catch (Exception ex)
        {
            return Failure<RegistryDocumentResult>("registrySigningFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<RegistryPublishResult>> PublishAsync(
        string registryRepoPath,
        bool confirmed,
        CancellationToken ct)
    {
        if (AdminRequired<RegistryPublishResult>() is { } blocked) return blocked;
        if (!confirmed)
            return Failure<RegistryPublishResult>(
                "confirmationRequired",
                "Publishing the registry requires --yes after reviewing the exact validated hashes.");

        var phases = new List<string>();
        try
        {
            var path = ResolveRegistryPath(registryRepoPath);
            var validation = Validate(path);
            if (validation.ErrorKind != WorkflowErrorKind.None || validation.Value is null)
                return Forward<RegistryDocumentResult, RegistryPublishResult>(validation, phases);

            var jsonBytes = File.ReadAllBytes(path);
            var sigPath = path + ".sig";
            var sigBytes = File.ReadAllBytes(sigPath);
            VerifyRegistryPair(jsonBytes, sigBytes);
            phases.Add("localPairVerified");

            var serverConfig = config.GetServerUploadConfig()
                ?? throw new InvalidOperationException("Server upload is not configured.");
            var (liveJson, liveSignature) = await server.ReadPublishedRegistryAsync(serverConfig, ct);
            phases.Add("livePairInspected");

            if (liveJson is not null && liveJson.AsSpan().SequenceEqual(jsonBytes))
            {
                if (liveSignature is null) throw new InvalidOperationException("The live registry has no signature.");
                VerifyRegistryPair(liveJson, liveSignature);
                phases.Add("alreadyLive");
                return PublishSuccess(serverConfig, jsonBytes, sigBytes, phases,
                    "The live registry pair is already byte-identical and valid.");
            }

            RequireVersionMovesForward(jsonBytes, liveJson);
            await server.PublishRegistryPairAsync(serverConfig, jsonBytes, sigBytes, CancellationToken.None);
            phases.Add("pairUploaded");

            var (readBackJson, readBackSignature) = await server.ReadPublishedRegistryAsync(
                serverConfig,
                CancellationToken.None);
            if (readBackJson is null || readBackSignature is null ||
                !readBackJson.AsSpan().SequenceEqual(jsonBytes) ||
                !readBackSignature.AsSpan().SequenceEqual(sigBytes))
            {
                throw new InvalidOperationException(
                    "The registry pair read back from the server differs from the exact files uploaded.");
            }
            VerifyRegistryPair(readBackJson, readBackSignature);
            phases.Add("serverReadBackVerified");

            await VerifyPublicPairAsync(jsonBytes, CancellationToken.None);
            phases.Add("publicReadBackVerified");
            return PublishSuccess(serverConfig, jsonBytes, sigBytes, phases,
                "Published the signed registry pair and verified the exact public bytes.");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Registry publication failed after {Phases}", string.Join(",", phases));
            return new WorkflowResult<RegistryPublishResult>(
                "registryPublishFailed",
                null,
                new[] { ex.Message },
                WorkflowErrorKind.Conflict,
                phases);
        }
    }

    public async Task<WorkflowResult<ProcessResult>> CommitAsync(
        string registryRepoPath,
        string message,
        CancellationToken ct)
    {
        if (AdminRequired<ProcessResult>() is { } blocked) return blocked;

        try
        {
            var repo = Path.GetFullPath(registryRepoPath);
            if (!await git.IsRepoAsync(repo, ct))
                return Failure<ProcessResult>("registryRepoInvalid", $"{repo} is not a Git repository.");
            var status = await git.StatusPorcelainAsync(repo, ct);
            if (!status.Success) return ProcessFailure<ProcessResult>("registryGitStatusFailed", status);
            if (string.IsNullOrWhiteSpace(status.Stdout))
                return Success("registryCommitNotNeeded", status, "The registry working tree is clean.");
            var add = await git.AddAsync(repo, ".", ct);
            if (!add.Success) return ProcessFailure<ProcessResult>("registryGitAddFailed", add);
            var commit = await git.CommitAsync(
                repo,
                string.IsNullOrWhiteSpace(message) ? "Update plugin registry" : message.Trim(),
                ct);
            return commit.Success
                ? Success("registryCommitted", commit, "Committed the registry repository locally.")
                : ProcessFailure<ProcessResult>("registryCommitFailed", commit);
        }
        catch (Exception ex)
        {
            return Failure<ProcessResult>("registryCommitFailed", ex.Message);
        }
    }

    public async Task<WorkflowResult<ProcessResult>> PushAsync(
        string registryRepoPath,
        CancellationToken ct)
    {
        if (AdminRequired<ProcessResult>() is { } blocked) return blocked;

        try
        {
            var repo = Path.GetFullPath(registryRepoPath);
            if (!await git.IsRepoAsync(repo, ct))
                return Failure<ProcessResult>("registryRepoInvalid", $"{repo} is not a Git repository.");
            var push = await git.PushAsync(repo, ct);
            return push.Success
                ? Success("registryPushed", push,
                    "Pushed registry Git history. This does not change what managers read; registry publish does.")
                : ProcessFailure<ProcessResult>("registryPushFailed", push);
        }
        catch (Exception ex)
        {
            return Failure<ProcessResult>("registryPushFailed", ex.Message);
        }
    }

    private string DefaultRepoPath() => Path.Combine(
        config.StorageDirectory,
        "repos",
        RegistryMembershipChecker.RegistryRepo.Replace('/', '-'));

    private void SaveRepoPath(string repo)
    {
        var current = config.Load();
        current.LastRegistryRepoPath = repo;
        config.Save(current);
    }

    private static string ResolveRegistryPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full))
            return FindRegistry(full, requireExists: true)!;
        return full;
    }

    private static string? FindRegistry(string repo, bool requireExists)
    {
        var candidates = new[]
        {
            Path.Combine(repo, RegistryFileName),
            Path.Combine(repo, "registry.json")
        };
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is null && requireExists)
            throw new FileNotFoundException($"No {RegistryFileName} or registry.json was found in {repo}.");
        return found;
    }

    private static RegistryDocumentResult Document(string path, byte[] bytes) =>
        new(path, Sha(bytes), File.Exists(path + ".sig"));

    private static string Sha(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void RequireNoBom(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            throw new InvalidOperationException("The registry starts with a UTF-8 byte-order mark, which managers do not accept.");
    }

    private static void VerifyRegistryPair(byte[] json, byte[] signatureFile)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(RegistryTrustKey.PublicKeyPem);
        var signature = Convert.FromBase64String(Encoding.UTF8.GetString(signatureFile).Trim());
        if (!rsa.VerifyData(json, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidOperationException("The detached signature does not verify over the exact registry JSON bytes.");
    }

    private static void RequireVersionMovesForward(byte[] candidate, byte[]? live)
    {
        if (live is null) return;
        var candidateVersion = ReadVersion(candidate);
        var liveVersion = ReadVersion(live);
        if (VersionComparer.Instance.Compare(candidateVersion, liveVersion) <= 0)
        {
            throw new InvalidOperationException(
                $"The live registry is version {liveVersion}; changed content must raise registryVersion above it, not publish version {candidateVersion}.");
        }
    }

    private static string ReadVersion(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("registryVersion").GetString()
            ?? throw new InvalidOperationException("registryVersion is null.");
    }

    private async Task VerifyPublicPairAsync(byte[] expectedJson, CancellationToken ct)
    {
        var cacheBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var jsonUri = new Uri(RegistryMembershipChecker.RegistryUrl.AbsoluteUri + "?_=" + cacheBust);
        var sigUri = new Uri(RegistryMembershipChecker.RegistryUrl.AbsoluteUri + ".sig?_=" + cacheBust);
        var publicJson = await http.GetByteArrayAsync(jsonUri, ct);
        var publicSignature = await http.GetByteArrayAsync(sigUri, ct);
        if (!publicJson.AsSpan().SequenceEqual(expectedJson))
            throw new InvalidOperationException("The public registry bytes differ from what was uploaded.");
        VerifyRegistryPair(publicJson, publicSignature);
    }

    private static RegistryPublishResult PublishValue(
        ServerUploadConfig cfg,
        byte[] json,
        byte[] signature,
        IReadOnlyList<string> phases) =>
        new($"{cfg.Host}:{cfg.RemoteCatalogRoot}", Sha(json), Sha(signature), phases);

    private static WorkflowResult<RegistryPublishResult> PublishSuccess(
        ServerUploadConfig cfg,
        byte[] json,
        byte[] signature,
        IReadOnlyList<string> phases,
        string message) =>
        new("registryPublished", PublishValue(cfg, json, signature, phases), new[] { message },
            completedPhases: phases);

    private static WorkflowResult<T>? AdminRequired<T>() =>
        AuthoringBuildFlags.IsRegistryAdmin
            ? null
            : new WorkflowResult<T>(
                "registryAdminBuildRequired",
                default,
                new[]
                {
                    "Registry administration requires an admin build. This ordinary build exposes the commands for discovery but cannot read private admin configuration or perform registry work."
                },
                WorkflowErrorKind.Authentication);

    private static WorkflowResult<T> Success<T>(string status, T value, string message) =>
        new(status, value, new[] { message });

    private static WorkflowResult<T> Failure<T>(
        string status,
        string message,
        WorkflowErrorKind kind = WorkflowErrorKind.Conflict) =>
        new(status, default, new[] { message }, kind);

    private static WorkflowResult<T> ProcessFailure<T>(string status, ProcessResult process) =>
        Failure<T>(status, string.IsNullOrWhiteSpace(process.Combined)
            ? $"The process exited with code {process.ExitCode}."
            : process.Combined);

    private static WorkflowResult<TOut> Forward<TIn, TOut>(
        WorkflowResult<TIn> result,
        IReadOnlyList<string>? phases = null) =>
        new(result.Status, default, result.Messages, result.ErrorKind, phases ?? result.CompletedPhases);
}
