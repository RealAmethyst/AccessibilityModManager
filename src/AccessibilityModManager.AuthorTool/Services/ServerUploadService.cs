using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// A publish that did not complete, and the one thing that decides what may be done about it.
/// </summary>
/// <param name="renameAttempted">
/// Whether the rename that switches the live index was reached.
///
/// <para>False means the live index is provably untouched: the connection, the upload, or the
/// pre-switch check failed, and the file the world reads is still the old one. Only then may the
/// caller drop its journalled record of the attempt.</para>
///
/// <para>True means it may have landed — including when the rename itself threw, because a
/// connection lost mid-rename is indistinguishable from one the server completed before the reply
/// went missing. The journal must survive, or the only evidence that this machine may have
/// published is gone, and the next attempt signs a second version of the same publish.</para>
/// </param>
public sealed class IndexPublishFailedException(string message, bool renameAttempted, Exception? inner)
    : Exception(message, inner)
{
    public bool RenameAttempted { get; } = renameAttempted;
}

/// <summary>
/// Uploads a wrapped ZIP + generated <c>gate.json</c> to the author's Patreon-gate download
/// server over SFTP, and publishes the catalog (signed registry pair, per-plugin indexes) to
/// the same box. Authentication uses the same SSH private key the author already configured
/// for PuTTY / FileZilla (see <see cref="ServerUploadConfig.PrivateKeyPath"/>), so there's no
/// second secret to manage, and the host key is pinned.
/// <para>
/// Every publish here is staged under a hidden temp name and posix-renamed into place, so a
/// half-uploaded file is never publicly reachable, and a published release version is
/// immutable: re-publishing the same bytes succeeds as a no-op, re-publishing different bytes
/// under a live version is refused.
/// </para>
/// </summary>
public sealed class ServerUploadService
{
    private const string GateFileName = "gate.json";

    private static readonly JsonSerializerOptions GateJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;

    public ServerUploadService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The "Test connection" button. Authenticates against the pinned host key, then proves the
    /// two things every publish depends on, in both roots the tool writes to: the folder is
    /// there and writable, and the server supports the atomic posix rename that switches a
    /// staged file live. Proving it here means the author finds out from a button rather than
    /// from a half-finished publish. Returns null when everything checks out, or a
    /// human-readable description of the first thing that didn't.
    /// </summary>
    public async Task<string?> TestConnectionAsync(ServerUploadConfig cfg, CancellationToken ct)
    {
        var validationError = ValidateForConnection(cfg);
        if (validationError != null) return validationError;

        return await Task.Run(() =>
        {
            try
            {
                using var sftp = OpenSftp(cfg);

                if (!sftp.Exists(cfg.RemoteBasePath))
                    return $"Remote releases path '{cfg.RemoteBasePath}' doesn't exist on the server. " +
                           "Create it on the VPS first (see PATREON_VPS_SETUP.md section 6.7).";
                var entries = sftp.ListDirectory(cfg.RemoteBasePath).Count();

                var releasesProblem = ProveWritableAndRenamable(sftp, cfg.RemoteBasePath, "releases folder");
                if (releasesProblem != null) return releasesProblem;

                var catalogRoot = cfg.RemoteCatalogRoot?.TrimEnd('/');
                if (!string.IsNullOrEmpty(catalogRoot))
                {
                    if (!sftp.Exists(catalogRoot))
                        return $"Catalog root '{catalogRoot}' doesn't exist on the server. That's where " +
                               "plugin-registry.json and the plugin indexes are published from — check the " +
                               "path in Server upload settings.";
                    var catalogProblem = ProveWritableAndRenamable(sftp, catalogRoot, "catalog root");
                    if (catalogProblem != null) return catalogProblem;
                }

                _logger.Information(
                    "SFTP test ok — {Count} entries under {Path}; both roots writable and rename-capable",
                    entries, cfg.RemoteBasePath);
                return null;
            }
            catch (SshAuthenticationException ex)
            {
                _logger.Warning(ex, "SFTP authentication failed");
                return $"Authentication failed: {ex.Message}. Verify the user, key path, and that " +
                       $"the matching public key is in /home/{cfg.User}/.ssh/authorized_keys on the VPS.";
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "SFTP connection test failed");
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        }, ct);
    }

    /// <summary>
    /// Writes a tiny hidden probe file, renames it (the exact operation that switches a publish
    /// live), and removes it. Anything left behind on a failure is cleaned up here.
    /// </summary>
    private string? ProveWritableAndRenamable(SftpClient sftp, string remoteDir, string label)
    {
        var token = Guid.NewGuid().ToString("N");
        var probe = JoinPosix(remoteDir, $".amm-write-test.{token}.tmp");
        var renamed = JoinPosix(remoteDir, $".amm-write-test.{token}.live");

        try
        {
            using (var src = new MemoryStream(Encoding.UTF8.GetBytes("accessibility mod manager write test")))
                sftp.UploadFile(src, probe, canOverride: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Write test failed in {Path}", remoteDir);
            return $"Can't write to the {label} '{remoteDir}': {ex.Message}. Publishing needs write " +
                   $"access there for the user '{sftp.ConnectionInfo.Username}'.";
        }

        try
        {
            sftp.RenameFile(probe, renamed, isPosix: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Posix rename test failed in {Path}", remoteDir);
            TryDeleteRemote(sftp, probe);
            return $"The server wouldn't rename a file inside the {label} '{remoteDir}': {ex.Message}. " +
                   "Publishing switches files live with an atomic rename, so it can't work without it.";
        }

        TryDeleteRemote(sftp, renamed);
        return null;
    }

    /// <summary>
    /// What already sits at <c>{RemoteBasePath}/{gameId}/{version}/</c> on the server.
    /// </summary>
    /// <param name="PackageExists">A file with this asset name is already published there.</param>
    /// <param name="PackageLength">Its size in bytes (0 when it doesn't exist).</param>
    /// <param name="PackageMatches">
    /// True when the published bytes are byte-for-byte the ones about to be uploaded. Only
    /// computed when the sizes agree — a size difference already proves they differ.
    /// </param>
    /// <param name="GateExists">A <c>gate.json</c> is published alongside it.</param>
    /// <param name="OtherAssets">
    /// Any OTHER published file sitting in the same version folder. This matters because the
    /// download server's tier check is folder-scoped: one <c>gate.json</c> governs everything
    /// beside it. A second file in the folder would therefore inherit — or, when the folder goes
    /// public, lose — the first one's tier lock, and it would sidestep the
    /// same-version-different-bytes refusal simply by having a different name. One folder holds
    /// exactly one package.
    /// </param>
    public sealed record RemoteReleaseState(
        bool PackageExists, long PackageLength, bool PackageMatches, bool GateExists,
        IReadOnlyList<string> OtherAssets);

    /// <summary>
    /// What a publish actually did, so the caller can say so plainly in the status line.
    /// <paramref name="GateRemovalPending"/> means this release is public but a tier lock is
    /// still in force on the server — the caller removes it once the catalog agrees.
    /// </summary>
    public sealed record ReleasePublishOutcome(
        bool PackageUploaded, bool GateWritten, bool GateRemovalPending, bool GateChangePending,
        string PublicUrl);

    /// <summary>
    /// Looks at a release folder on the server without changing anything, so the author can be
    /// told what's about to happen BEFORE a large upload starts: whether this version is already
    /// published, whether the published bytes are the same ones, and whether a Patreon gate is
    /// currently in force (which flipping to public would remove).
    /// </summary>
    public async Task<RemoteReleaseState> ProbeReleaseAsync(
        ServerUploadConfig cfg,
        string gameId,
        string version,
        string assetFileName,
        Stream package,
        string expectedSha256,
        CancellationToken ct)
    {
        var validationError = ValidateForUpload(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        RequireSafeRemoteSegment(gameId, "game id");
        RequireSafeRemoteSegment(version, "version");
        RequireSafeRemoteSegment(assetFileName, "file name");
        RequireSafeAssetName(assetFileName);

        var remoteFolder = JoinPosix(cfg.RemoteBasePath, gameId, version);
        var remoteZip = JoinPosix(remoteFolder, assetFileName);
        var remoteGate = JoinPosix(remoteFolder, GateFileName);
        var localLength = package.Length;

        return await Task.Run(() =>
        {
            using var sftp = OpenSftp(cfg);
            var others = ListOtherAssets(sftp, remoteFolder, assetFileName);

            if (!sftp.Exists(remoteZip))
                return new RemoteReleaseState(false, 0, false, sftp.Exists(remoteGate), others);

            var length = sftp.GetAttributes(remoteZip).Size;
            var matches = length == localLength &&
                          string.Equals(ComputeRemoteSha256(sftp, remoteZip), expectedSha256,
                              StringComparison.OrdinalIgnoreCase);
            return new RemoteReleaseState(true, length, matches, sftp.Exists(remoteGate), others);
        }, ct);
    }

    /// <summary>
    /// Published files in a version folder other than the one being published — ignoring the
    /// gate and any staged temp (dot-prefixed) file. See <see cref="RemoteReleaseState"/> for
    /// why a second file there is refused rather than tolerated.
    /// </summary>
    private static List<string> ListOtherAssets(SftpClient sftp, string remoteFolder, string assetFileName)
    {
        var others = new List<string>();
        if (!sftp.Exists(remoteFolder)) return others;

        foreach (var entry in sftp.ListDirectory(remoteFolder))
        {
            if (entry.IsDirectory) continue;
            var name = entry.Name;
            if (string.IsNullOrEmpty(name)) continue;
            if (string.Equals(name, GateFileName, StringComparison.Ordinal)) continue;
            // Staged temporaries only — a real asset can't start with a dot (RequireSafeAssetName).
            if (name.StartsWith('.')) continue;
            // ORDINAL: the server's filesystem is case-sensitive, so "Mod.zip" beside "mod.zip"
            // really is a second published file, and treating it as ours would let both stand.
            if (string.Equals(name, assetFileName, StringComparison.Ordinal)) continue;
            others.Add(name);
        }
        return others;
    }

    /// <summary>
    /// Publishes one release's wrapped ZIP (and its <c>gate.json</c>, when the release is
    /// Patreon-gated) to <c>{RemoteBasePath}/{gameId}/{version}/</c>.
    /// <para>
    /// The ordering rules come from audit finding 37 and are the whole point of this method:
    /// </para>
    /// <list type="bullet">
    /// <item>A published version is immutable. If the asset already exists with different
    /// bytes, this throws instead of overwriting — the author bumps the version, exactly the
    /// rhythm the registry already enforces. Re-publishing identical bytes is a no-op success,
    /// so an interrupted publish is safe to retry.</item>
    /// <item>The gate goes up BEFORE the bytes it protects, so there is never a window where
    /// new bytes are downloadable under a missing or stale gate.</item>
    /// <item>Removing a gate (a release flipping from patrons-only to public) happens only
    /// AFTER the bytes are in place, and only when the caller has explicitly confirmed it with
    /// <paramref name="removeExistingGate"/>.</item>
    /// <item>The ZIP is streamed to a hidden temp name and posix-renamed into place, so the
    /// public URL only ever serves a complete file.</item>
    /// </list>
    /// <paramref name="package"/> is the caller's open, verified handle on the ZIP — the same
    /// stream it hashed and validated — so the published bytes are provably those bytes.
    /// </summary>
    public async Task<ReleasePublishOutcome> PublishReleaseAsync(
        ServerUploadConfig cfg,
        string gameId,
        string version,
        string assetFileName,
        Stream package,
        string expectedSha256,
        PatreonGate? gate,
        CancellationToken ct)
    {
        var validationError = ValidateForUpload(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        // gameId / version / fileName become remote path segments — reject anything that could walk
        // outside the releases folder (path separators, "..") before building the SFTP path.
        RequireSafeRemoteSegment(gameId, "game id");
        RequireSafeRemoteSegment(version, "version");
        RequireSafeRemoteSegment(assetFileName, "file name");
        RequireSafeAssetName(assetFileName);

        var remoteFolder = JoinPosix(cfg.RemoteBasePath, gameId, version);
        var remoteZip = JoinPosix(remoteFolder, assetFileName);
        var remoteGate = JoinPosix(remoteFolder, GateFileName);
        var zipTmp = JoinPosix(remoteFolder, $".{assetFileName}.{Guid.NewGuid():N}.tmp");
        var gateTmp = JoinPosix(remoteFolder, $".{GateFileName}.{Guid.NewGuid():N}.tmp");
        var localLength = package.Length;

        return await Task.Run(() =>
        {
            using var sftp = OpenSftp(cfg);

            // Make sure each path component exists. SFTP MakeDirectory only creates one
            // level at a time, so walk down from RemoteBasePath.
            EnsureRemoteDirectory(sftp, cfg.RemoteBasePath, gameId, version);

            // One folder, one package. The gate that protects this folder protects everything in
            // it, so a stray second file would ride another release's tier lock (or lose its own
            // when the folder goes public) — and renaming the asset would otherwise be a way
            // around the immutability rule below.
            var others = ListOtherAssets(sftp, remoteFolder, assetFileName);
            if (others.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The folder for {gameId} {version} on the server already holds a different file " +
                    $"({string.Join(", ", others)}). A version folder holds exactly one package, because " +
                    "its Patreon tier lock covers everything inside it. Bump the version and publish that.");
            }

            // Immutability gate, re-checked here rather than trusting the earlier probe: the
            // published bytes for a version never change under us.
            var uploadPackage = true;
            if (sftp.Exists(remoteZip))
            {
                var publishedLength = sftp.GetAttributes(remoteZip).Size;
                var identical = publishedLength == localLength &&
                                string.Equals(ComputeRemoteSha256(sftp, remoteZip), expectedSha256,
                                    StringComparison.OrdinalIgnoreCase);
                if (!identical)
                {
                    throw new InvalidOperationException(
                        $"Version {version} of {gameId} is already published on {cfg.Host} with different " +
                        "file contents. Published versions are never overwritten — anyone who already " +
                        "downloaded it would get a hash mismatch. Bump the version number and publish " +
                        "that instead.");
                }
                uploadPackage = false;
                _logger.Information("{Remote} is already the exact file being published — skipping upload", remoteZip);
            }

            var gateWritten = false;
            var gateChangePending = false;
            try
            {
                if (gate != null && (uploadPackage || !sftp.Exists(remoteGate)))
                {
                    // Inline, because this is the RESTRICTIVE direction: the lock must be in
                    // force before the bytes it protects exist, and putting a lock on something
                    // currently unlocked never exposes anything by being early.
                    var gateJson = BuildGateJson(gate);
                    using (var src = new MemoryStream(Encoding.UTF8.GetBytes(gateJson)))
                        sftp.UploadFile(src, gateTmp, canOverride: true);
                    sftp.RenameFile(gateTmp, remoteGate, isPosix: true);
                    gateWritten = true;
                }
                else if (gate != null)
                {
                    // The package is already published and unchanged, so this is purely a change
                    // to WHO may download it — and that follows the catalog like every other
                    // enforcement change, rather than running ahead of what users are told.
                    gateChangePending = true;
                }

                if (uploadPackage)
                {
                    // SSH.NET streams the upload, so memory use is bounded by the SFTP buffer
                    // (~32 KB) no matter how big the wrapped ZIP is.
                    _logger.Information("Uploading {Bytes} bytes to {Remote}", localLength, remoteZip);
                    package.Position = 0;
                    sftp.UploadFile(package, zipTmp, canOverride: true);
                    sftp.RenameFile(zipTmp, remoteZip, isPosix: true);
                }
            }
            catch
            {
                TryDeleteRemote(sftp, zipTmp);
                TryDeleteRemote(sftp, gateTmp);
                throw;
            }

            // Note what is deliberately NOT done here: removing an existing gate when this
            // release is now public. That transition exposes patrons-only bytes, so it belongs
            // AFTER the catalog says the release is public — see RemoveGateAsync, which the
            // index editor calls once the index is live.
            _logger.Information("Publish complete: {Folder}", remoteFolder);
            return new ReleasePublishOutcome(
                uploadPackage, gateWritten,
                GateRemovalPending: gate == null && sftp.Exists(remoteGate),
                GateChangePending: gateChangePending,
                BuildPublicUrl(cfg, gameId, version, assetFileName));
        }, ct);
    }

    /// <summary>
    /// Rewrites just the <c>gate.json</c> of an already-published version, for the case where the
    /// author changes which tiers unlock a release without touching the package. Without this,
    /// the index would carry the new tiers while the download server kept enforcing the old ones
    /// — removed tiers keeping access, added tiers being turned away — and no amount of re-saving
    /// would ever reconcile them.
    /// <para>
    /// Refuses when the version folder isn't there: writing a gate for a package that was never
    /// published would silently create a folder that serves nothing.
    /// </para>
    /// </summary>
    public async Task PublishGateOnlyAsync(
        ServerUploadConfig cfg, string gameId, string version, PatreonGate gate, CancellationToken ct)
    {
        var validationError = ValidateForUpload(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        RequireSafeRemoteSegment(gameId, "game id");
        RequireSafeRemoteSegment(version, "version");

        var remoteFolder = JoinPosix(cfg.RemoteBasePath, gameId, version);
        var remoteGate = JoinPosix(remoteFolder, GateFileName);
        var gateTmp = JoinPosix(remoteFolder, $".{GateFileName}.{Guid.NewGuid():N}.tmp");

        await Task.Run(() =>
        {
            using var sftp = OpenSftp(cfg);
            if (!sftp.Exists(remoteFolder))
            {
                throw new InvalidOperationException(
                    $"There's nothing published for {gameId} {version} on {cfg.Host}, so there's no tier " +
                    "lock to update. Pick the wrapped ZIP for this release and save again.");
            }

            // An empty (or gate-only) folder means the package was never actually published, and
            // writing a lock for a file that isn't there just leaves a gate guarding nothing.
            var packages = ListOtherAssets(sftp, remoteFolder, assetFileName: "");
            if (packages.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The folder for {gameId} {version} on {cfg.Host} holds no package, so a tier lock there " +
                    "would guard nothing. Pick the wrapped ZIP for this release and save again.");
            }

            try
            {
                using (var src = new MemoryStream(Encoding.UTF8.GetBytes(BuildGateJson(gate))))
                    sftp.UploadFile(src, gateTmp, canOverride: true);
                sftp.RenameFile(gateTmp, remoteGate, isPosix: true);
            }
            catch
            {
                TryDeleteRemote(sftp, gateTmp);
                throw;
            }

            _logger.Information("Tier lock updated at {Path}", remoteGate);
        }, ct);
    }

    /// <summary>
    /// Whether a tier lock is currently in force for a published version. Cheap enough to ask on
    /// a metadata save, and it's what makes a half-finished "make this public" recoverable: the
    /// local index having already dropped the gate can't tell us what the SERVER still enforces.
    /// </summary>
    public async Task<bool> GateExistsAsync(
        ServerUploadConfig cfg, string gameId, string version, CancellationToken ct)
    {
        var validationError = ValidateForUpload(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        RequireSafeRemoteSegment(gameId, "game id");
        RequireSafeRemoteSegment(version, "version");
        var remoteGate = JoinPosix(cfg.RemoteBasePath, gameId, version, GateFileName);

        return await Task.Run(() =>
        {
            using var sftp = OpenSftp(cfg);
            return sftp.Exists(remoteGate);
        }, ct);
    }

    /// <summary>
    /// Removes the tier lock from an already-published version, making it publicly downloadable.
    /// Split out from publishing so the caller can do it AFTER the catalog says the release is
    /// public — exposing bytes before the index agrees is the wrong order to fail in.
    /// </summary>
    public async Task RemoveGateAsync(
        ServerUploadConfig cfg, string gameId, string version, CancellationToken ct)
    {
        var validationError = ValidateForUpload(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        RequireSafeRemoteSegment(gameId, "game id");
        RequireSafeRemoteSegment(version, "version");

        var remoteGate = JoinPosix(cfg.RemoteBasePath, gameId, version, GateFileName);

        await Task.Run(() =>
        {
            using var sftp = OpenSftp(cfg);
            if (!sftp.Exists(remoteGate)) return;
            sftp.DeleteFile(remoteGate);
            _logger.Information("Removed the Patreon gate at {Remote} — the release is public now", remoteGate);
        }, ct);
    }

    /// <summary>
    /// Streams a published file back through SFTP to hash it. Only ever called when the sizes
    /// already agree, so the transfer is the price of proving two same-sized files are the
    /// same file — not something a normal publish pays.
    /// </summary>
    private static string ComputeRemoteSha256(SftpClient sftp, string remotePath)
    {
        using var remote = sftp.OpenRead(remotePath);
        return Convert.ToHexStringLower(SHA256.HashData(remote));
    }

    /// <summary>
    /// Publishes the signed registry pair to the catalog web root. Both files go up under
    /// hidden unguessable temp names first; only when both are fully uploaded are they
    /// posix-renamed over the live names, back to back — no partially-written file is ever
    /// publicly visible, and the json/sig mismatch window is the width of one rename. Callers
    /// verify the PUBLIC urls afterwards (fetch + signature check) before declaring success.
    /// </summary>
    public async Task PublishRegistryPairAsync(
        ServerUploadConfig cfg, byte[] registryJson, byte[] signature, CancellationToken ct)
    {
        var validationError = ValidateForCatalog(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        var remoteDir = cfg.RemoteCatalogRoot.TrimEnd('/');
        var liveJson = JoinPosix(remoteDir, "plugin-registry.json");
        var liveSig = JoinPosix(remoteDir, "plugin-registry.json.sig");
        var jsonTmp = JoinPosix(remoteDir, $".plugin-registry.json.{Guid.NewGuid():N}.tmp");
        var sigTmp = JoinPosix(remoteDir, $".plugin-registry.json.sig.{Guid.NewGuid():N}.tmp");

        await Task.Run(() =>
        {
            using var sftp = OpenSftp(cfg);

            // The catalog root is the live web root — it must already exist; creating it here
            // would only mask a misconfigured path writing to the wrong place.
            if (!sftp.Exists(remoteDir))
                throw new InvalidOperationException(
                    $"Remote catalog folder '{remoteDir}' doesn't exist on the server. Check the " +
                    "catalog root in Server upload settings.");

            try
            {
                using (var src = new MemoryStream(registryJson))
                    sftp.UploadFile(src, jsonTmp, canOverride: true);
                using (var src = new MemoryStream(signature))
                    sftp.UploadFile(src, sigTmp, canOverride: true);

                _logger.Information("Registry pair staged; activating {Json} + {Sig}", liveJson, liveSig);
                sftp.RenameFile(jsonTmp, liveJson, isPosix: true);
                sftp.RenameFile(sigTmp, liveSig, isPosix: true);
            }
            catch
            {
                TryDeleteRemote(sftp, jsonTmp);
                TryDeleteRemote(sftp, sigTmp);
                throw;
            }

            _logger.Information("Registry pair live at {Dir}", remoteDir);
        }, ct);
    }

    /// <summary>
    /// Reads the published registry pair straight off the server over SFTP, for when the public
    /// HTTPS address can't be reached. Without this, "I couldn't read the live registry" would be
    /// indistinguishable from "there is no live registry" — and publishing an older version on
    /// top of a newer one strands every manager that already recorded the newer version.
    /// Returns nulls for files that genuinely aren't there yet (a first publish).
    /// </summary>
    public async Task<(byte[]? Json, byte[]? Signature)> ReadPublishedRegistryAsync(
        ServerUploadConfig cfg, CancellationToken ct)
    {
        var validationError = ValidateForCatalog(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        var remoteDir = cfg.RemoteCatalogRoot.TrimEnd('/');
        var liveJson = JoinPosix(remoteDir, "plugin-registry.json");
        var liveSig = JoinPosix(remoteDir, "plugin-registry.json.sig");

        return await Task.Run<(byte[]?, byte[]?)>(() =>
        {
            using var sftp = OpenSftp(cfg);
            return (ReadAllIfPresent(sftp, liveJson), ReadAllIfPresent(sftp, liveSig));
        }, ct);
    }

    private static byte[]? ReadAllIfPresent(SftpClient sftp, string remotePath)
    {
        if (!sftp.Exists(remotePath)) return null;
        using var remote = sftp.OpenRead(remotePath);
        using var buffer = new MemoryStream();
        remote.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Publishes one plugin's index.json to the catalog web root via upload-to-temp then a
    /// single atomic posix-rename — every manager request sees either the complete old index
    /// or the complete new one, never a partial file.
    /// </summary>
    /// <param name="beforeSwitchAsync">
    /// Run after the upload and immediately before the rename, to make a last check that the world
    /// has not moved while the file was going up.
    ///
    /// <para>It sits here rather than in the caller because a check made before this method returns
    /// leaves the entire upload — seconds to minutes of it — between the check and the switch. Here
    /// the gap is a few instructions.</para>
    ///
    /// <para>Deliberately takes no cancellation token: this runs after the publish has been
    /// journalled, where "give up" is not one of the available answers. It refuses by throwing, and
    /// a refusal is the one failure that guarantees the live index was never touched.</para>
    /// </param>
    /// <exception cref="IndexPublishFailedException">
    /// Every failure, carrying whether the rename was attempted.
    /// </exception>
    public async Task PublishIndexAsync(
        ServerUploadConfig cfg, string pluginId, byte[] indexJson,
        Func<Task>? beforeSwitchAsync, CancellationToken ct)
    {
        // The one fact the caller has to branch on, so it is one variable, set in one place,
        // immediately before the call it describes, and never cleared. It lives out here because
        // every exit from this method has to be able to report it — including the ones that happen
        // before the connection is even opened.
        var renameAttempted = false;

        try
        {
            var validationError = ValidateForCatalog(cfg);
            if (validationError != null)
                throw new InvalidOperationException(validationError);

            RequireSafeRemoteSegment(pluginId, "plugin id");
            var remoteDir = JoinPosix(cfg.RemoteCatalogRoot.TrimEnd('/'), "plugins", pluginId);
            var liveIndex = JoinPosix(remoteDir, "index.json");
            var tempIndex = JoinPosix(remoteDir, $".index.json.{Guid.NewGuid():N}.tmp");

            await Task.Run(() =>
            {
                SftpClient? sftp = null;
                try
                {
                    sftp = OpenSftp(cfg);

                    if (!sftp.Exists(cfg.RemoteCatalogRoot.TrimEnd('/')))
                        throw new InvalidOperationException(
                            $"Remote catalog folder '{cfg.RemoteCatalogRoot}' doesn't exist on the server. " +
                            "Check the catalog root in Server upload settings.");
                    EnsureRemoteDirectory(sftp, cfg.RemoteCatalogRoot.TrimEnd('/'), "plugins", pluginId);

                    using (var src = new MemoryStream(indexJson))
                        sftp.UploadFile(src, tempIndex, canOverride: true);

                    // Blocking rather than restructuring this method around an await, so the SFTP
                    // session stays on the single thread every other operation in this class uses it
                    // from. There is no synchronisation context on a thread-pool thread, so there is
                    // nothing here to deadlock against.
                    beforeSwitchAsync?.Invoke().GetAwaiter().GetResult();

                    renameAttempted = true;
                    sftp.RenameFile(tempIndex, liveIndex, isPosix: true);

                    _logger.Information("Index live at {Path}", liveIndex);
                }
                catch
                {
                    // Best-effort, and it swallows its own failures: tidying up must never become
                    // the exception the caller sees, because that exception is what says whether the
                    // switch ran.
                    if (sftp is not null) TryDeleteRemote(sftp, tempIndex);
                    throw;
                }
                finally
                {
                    // Same reason. Disposing a session that has just died can throw, and that would
                    // replace a classified failure with an unclassified one.
                    try { sftp?.Dispose(); }
                    catch (Exception ex) { _logger.Warning(ex, "Couldn't close the SFTP session cleanly"); }
                }
            }, ct);
        }
        catch (Exception ex)
        {
            // One boundary around everything, so there is no way out of this method that does not
            // say whether the rename was reached — not a bad config, not a failed connection, not
            // something nobody thought of.
            throw new IndexPublishFailedException(ex.Message, renameAttempted, ex);
        }
    }

    /// <summary>
    /// A plugin's published index as read straight off the server.
    /// </summary>
    /// <param name="Present">
    /// False ONLY when the server answered that the path does not exist. Every other outcome —
    /// permission denied, a dropped connection, a truncated read — throws instead of arriving here,
    /// because publishing treats absence as permission to start a history over. A read that folded
    /// failures into "there is nothing published" would hand a hostile or merely broken server the
    /// one answer it most wants to give.
    /// </param>
    public sealed record RemoteIndex(bool Present, byte[]? Bytes);

    /// <summary>
    /// Reads a plugin's live index over SFTP, which is the only way the publisher may look at it.
    ///
    /// Not over HTTPS: once the read API is serving filtered catalogs, an HTTP fetch returns a
    /// response with the manifest stripped by design, and that is indistinguishable — from here —
    /// from a server that deleted it.
    /// </summary>
    public async Task<RemoteIndex> ReadPluginIndexAsync(
        ServerUploadConfig cfg, string pluginId, CancellationToken ct)
    {
        var validationError = ValidateForCatalog(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        RequireSafeRemoteSegment(pluginId, "plugin id");
        var remotePath = JoinPosix(cfg.RemoteCatalogRoot.TrimEnd('/'), "plugins", pluginId, "index.json");

        return await Task.Run(() =>
        {
            using var sftp = OpenSftp(cfg);

            // Only the OPEN may answer "absent" — deliberately not the read that follows it. A
            // path-not-found surfacing mid-stream is not a file that was never there; it is a
            // transfer that broke, and letting it reach the same branch would turn a failed read
            // into permission to start a history over.
            Stream remote;
            try
            {
                remote = sftp.OpenRead(remotePath);
            }
            catch (SftpPathNotFoundException)
            {
                // The one failure that is genuinely an answer. Permission denied, a dropped
                // connection and everything else are separate types and propagate.
                _logger.Information("No index is published at {Path}", remotePath);
                return new RemoteIndex(false, null);
            }

            using (remote)
                return new RemoteIndex(true, ReadBounded(remote, ClaimProof.MaxIndexBytes, remotePath));
        }, ct);
    }

    /// <summary>
    /// Reads a stream, refusing at the limit rather than after it. Checking the size once everything
    /// has been buffered is not a limit — by then the server has already had whatever it wanted out
    /// of this machine's memory.
    /// </summary>
    private static byte[] ReadBounded(Stream source, int maxBytes, string what)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidOperationException(
                    $"'{what}' on the server is larger than {maxBytes} bytes, so it was not read. " +
                    "An index that size is not one this tool published.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A publish lock this machine is holding: where it is, and what is in it.
    /// </summary>
    /// <param name="RemotePath">
    /// Resolved once, at acquisition, and carried rather than recomputed — releasing must target the
    /// file that was actually taken, even if the configured lock root changed in between.
    /// </param>
    public sealed record PublishLockHandle(string RemotePath, PublishLockBody Body);

    /// <summary>
    /// Takes the publish lock for one plugin, so two copies of the tool cannot build on the same
    /// head at once.
    ///
    /// The exclusive create is what decides: SSH.NET's <see cref="FileMode.CreateNew"/> is
    /// <c>SSH_FXF_CREAT|SSH_FXF_EXCL</c> (verified against the shipped assembly: <c>Flags.CreateNew</c>
    /// is <c>0x28</c>), which OpenSSH's sftp-server performs as a single <c>O_CREAT|O_EXCL</c> open.
    ///
    /// <para>See <see cref="PublishLock"/> for what this does and does not protect against — in
    /// short, it coordinates the author's own machines and is no defence against a hostile
    /// server.</para>
    /// </summary>
    /// <exception cref="PublishLockHeldException">Someone else holds it.</exception>
    public async Task<PublishLockHandle> AcquirePublishLockAsync(
        ServerUploadConfig cfg, string pluginId, CancellationToken ct)
    {
        var validationError = ValidateForCatalog(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        var fileName = PublishLock.FileNameFor(pluginId);
        var body = PublishLock.NewBody(pluginId);

        return await Task.Run(() =>
        {
            using var sftp = OpenSftp(cfg);
            var lockRoot = ResolveLockRoot(sftp, cfg);
            var lockPath = JoinPosix(lockRoot, fileName);

            var created = false;
            try
            {
                using var stream = sftp.Open(lockPath, FileMode.CreateNew, FileAccess.Write);
                created = true;
                stream.Write(PublishLock.Serialize(body));
            }
            catch (Exception ex) when (ex is SshException or IOException)
            {
                // Failing AFTER the create is our own mess, not somebody else's lock. Without this
                // branch the probe below would find the empty file we just made and report it as a
                // lock held by an unreadable holder — leaving a lock nobody holds, blocking the next
                // publish for a reason that has nothing to do with concurrency.
                if (created)
                {
                    TryDeleteRemote(sftp, lockPath);
                    throw new InvalidOperationException(
                        $"Couldn't write the publish lock at '{lockPath}': {ex.Message}", ex);
                }

                // SFTP version 3 — which is what OpenSSH speaks — has no distinct "already exists"
                // status, so an existing file comes back as the generic SSH_FX_FAILURE. The probe
                // below only decides the WORDING: the exclusive create above is what actually
                // arbitrates, and it already failed.
                var existing = PublishLock.TryParse(ReadCapped(sftp, lockPath, PublishLock.MaxBodyBytes), pluginId);
                if (existing is not null || sftp.Exists(lockPath))
                {
                    throw new PublishLockHeldException(
                        existing is not null
                            ? $"Another copy of this tool is publishing '{pluginId}' right now " +
                              $"({existing.Describe()}). Wait for it to finish. If nothing is really " +
                              "running, an earlier publish was interrupted and left the lock behind — " +
                              "that has to be cleared deliberately."
                            : $"There is already a publish lock for '{pluginId}' on the server, and it " +
                              "couldn't be read. Publishing is stopped until it's clear what holds it.",
                        existing);
                }

                throw new InvalidOperationException(
                    $"Couldn't take the publish lock at '{lockPath}': {ex.Message}", ex);
            }

            _logger.Information("Took the publish lock for {PluginId} at {Path}", pluginId, lockPath);
            return new PublishLockHandle(lockPath, body);
        }, ct);
    }

    /// <summary>
    /// Gives the lock back, but only when the token in it is still ours.
    ///
    /// A lock that was broken and retaken belongs to whoever is publishing under it now, and
    /// deleting it because we once held it would take it out from under them.
    /// </summary>
    public async Task<PublishLockRelease> ReleasePublishLockAsync(
        ServerUploadConfig cfg, PublishLockHandle handle, CancellationToken ct)
    {
        var validationError = ValidateForCatalog(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        return await Task.Run(() =>
        {
            using var sftp = OpenSftp(cfg);

            var found = PublishLock.TryParse(ReadCapped(sftp, handle.RemotePath, PublishLock.MaxBodyBytes), handle.Body.PluginId);
            if (found is null && !sftp.Exists(handle.RemotePath))
            {
                _logger.Warning("The publish lock at {Path} was already gone", handle.RemotePath);
                return PublishLockRelease.AlreadyGone;
            }

            if (!PublishLock.IsOurs(found, handle.Body))
            {
                _logger.Warning(
                    "The publish lock at {Path} is no longer ours — leaving it alone", handle.RemotePath);
                return PublishLockRelease.NotOurs;
            }

            sftp.DeleteFile(handle.RemotePath);
            _logger.Information("Released the publish lock at {Path}", handle.RemotePath);
            return PublishLockRelease.Released;
        }, ct);
    }

    /// <summary>
    /// A publish lock as it stands right now.
    /// </summary>
    /// <param name="Present">Whether there is anything at the lock's path.</param>
    /// <param name="Body">
    /// The lock's contents, when they parse as a lock this version understands. Null with
    /// <paramref name="Present"/> true means there is a file there that does not.
    /// </param>
    /// <param name="Fingerprint">
    /// A digest of the exact bytes found, whatever they were. It exists so that "is this still the
    /// lock I was shown" can be answered for a lock that does NOT parse — the recovery case that
    /// most needs asking, and the one where there is otherwise nothing to compare. Null only when
    /// nothing is there.
    /// </param>
    public readonly record struct RemoteLock(bool Present, PublishLockBody? Body, string? Fingerprint);

    /// <summary>What is holding a plugin's publish lock, if anything.</summary>
    public async Task<RemoteLock> ReadPublishLockAsync(
        ServerUploadConfig cfg, string pluginId, CancellationToken ct)
    {
        var validationError = ValidateForCatalog(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        var fileName = PublishLock.FileNameFor(pluginId);

        return await Task.Run(() =>
        {
            using var sftp = OpenSftp(cfg);
            var lockPath = JoinPosix(ResolveLockRoot(sftp, cfg), fileName);
            return ReadLockAt(sftp, lockPath, pluginId);
        }, ct);
    }

    /// <summary>
    /// Reads whatever is at a lock path, in the one form both reading and breaking agree on.
    /// </summary>
    private static RemoteLock ReadLockAt(SftpClient sftp, string lockPath, string pluginId)
    {
        var raw = ReadCapped(sftp, lockPath, PublishLock.MaxBodyBytes);
        if (raw is null)
        {
            // Either nothing is there, or there is something too big or unreadable to have come
            // from this tool. Both are "present, unidentifiable" if the path exists.
            return new RemoteLock(sftp.Exists(lockPath), null, null);
        }

        return new RemoteLock(true, PublishLock.TryParse(raw, pluginId),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(raw)));
    }

    /// <summary>
    /// Removes the publish lock the caller was shown, and only that one.
    ///
    /// Only ever called behind an explicit confirmation that every other copy of the tool is closed.
    /// An interrupted publish leaves a lock nobody holds, and this is how that is cleared — but a
    /// lock in the way is not by itself a reason to remove it, which is why this is separate from
    /// acquisition rather than a retry inside it.
    ///
    /// <para><paramref name="expectedFingerprint"/> is the digest of the exact bytes the author was
    /// shown, and the lock is re-read here and required to match it. Between being shown a lock and
    /// consenting to remove it a person takes as long as they take: the lock they were told about
    /// can be released and a genuinely active one taken at the same path in that time, and deleting
    /// by path alone would then clear a publish that is running — the exact concurrent-publisher
    /// case the lock exists for.</para>
    ///
    /// <para>A digest of the raw bytes rather than a field out of the parsed body, so that a lock
    /// which does not parse is checked just as strictly as one that does. That case is the whole
    /// reason this command exists, so exempting it — "there is nothing to compare" — would leave
    /// the race wide open on precisely the path most likely to be used. There is always something
    /// to compare.</para>
    ///
    /// <para>Not atomic, and cannot be: SFTP offers no conditional delete. The read and the delete
    /// are two round trips and the interval between them is not bounded by anything — a slow link or
    /// a stalled connection widens it. What this buys is the removal of the human-length window,
    /// which was the one that mattered; closing the rest needs a server-side conditional operation
    /// or a different locking protocol.</para>
    /// </summary>
    /// <returns>False when the lock changed under the author and was therefore left alone.</returns>
    public async Task<bool> BreakPublishLockAsync(
        ServerUploadConfig cfg, string pluginId, string? expectedFingerprint, CancellationToken ct)
    {
        var validationError = ValidateForCatalog(cfg);
        if (validationError != null)
            throw new InvalidOperationException(validationError);

        var fileName = PublishLock.FileNameFor(pluginId);

        return await Task.Run(() =>
        {
            using var sftp = OpenSftp(cfg);
            var lockPath = JoinPosix(ResolveLockRoot(sftp, cfg), fileName);

            var now = ReadLockAt(sftp, lockPath, pluginId);
            if (!now.Present) return true;

            // No wildcard. A caller that cannot name what it saw does not get to delete what is
            // there now, because "I could not identify it" and "it is the same one" are different
            // statements and only the second authorises this.
            if (now.Fingerprint is null ||
                !string.Equals(now.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
            {
                _logger.Warning(
                    "Refused to break the publish lock for {PluginId}: it is no longer the one that was read",
                    pluginId);
                return false;
            }

            sftp.DeleteFile(lockPath);
            _logger.Warning("Broke the publish lock for {PluginId} at {Path}", pluginId, lockPath);
            return true;
        }, ct);
    }

    /// <summary>
    /// Works out the lock directory for this connection and makes sure it exists.
    ///
    /// A root the author configured must already be there: creating it would turn a typo into a
    /// stray directory that silently becomes the lock namespace, and two machines disagreeing about
    /// where the lock lives is exactly the state the lock exists to prevent. The default one, under
    /// the SSH home, is created on first use because nobody asked for it.
    /// </summary>
    private string ResolveLockRoot(SftpClient sftp, ServerUploadConfig cfg)
    {
        var configured = cfg.RemoteLockRoot;
        var lockRoot = PublishLock.ResolveRoot(
            configured, sftp.WorkingDirectory, cfg.RemoteCatalogRoot, cfg.RemoteBasePath);

        if (sftp.Exists(lockRoot)) return lockRoot;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"The publish lock folder '{lockRoot}' doesn't exist on the server. Create it, or " +
                "clear the setting to use the default under your home directory.");
        }

        try
        {
            sftp.CreateDirectory(lockRoot);
            _logger.Information("Created the publish lock folder {Path}", lockRoot);
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            // Two machines publishing different plugins for the first time can both find it missing.
            // Whoever lost the race still has the directory they needed.
            if (!sftp.Exists(lockRoot))
            {
                throw new InvalidOperationException(
                    $"Couldn't create the publish lock folder '{lockRoot}': {ex.Message}", ex);
            }
        }

        return lockRoot;
    }

    /// <summary>
    /// Reads a small remote file, or null when it isn't there. Bounded, because the file comes from
    /// a server that may be lying about how big a lock is.
    /// </summary>
    private static byte[]? ReadCapped(SftpClient sftp, string remotePath, int maxBytes)
    {
        try
        {
            using var remote = sftp.OpenRead(remotePath);
            using var buffer = new MemoryStream();

            var chunk = new byte[8192];
            int read;
            while ((read = remote.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes) return null;
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            return null;
        }
    }

    private void TryDeleteRemote(SftpClient sftp, string path)
    {
        try { if (sftp.Exists(path)) sftp.DeleteFile(path); }
        catch (Exception ex) { _logger.Warning(ex, "Couldn't clean up staged file {Path}", path); }
    }

    private static string? ValidateForCatalog(ServerUploadConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Host)) return "Server host isn't configured.";
        if (string.IsNullOrWhiteSpace(cfg.User)) return "Server user isn't configured.";
        if (string.IsNullOrWhiteSpace(cfg.PrivateKeyPath)) return "SSH private key path isn't configured.";
        if (string.IsNullOrWhiteSpace(cfg.RemoteCatalogRoot)) return "Remote catalog root isn't configured.";
        return null;
    }

    /// <summary>
    /// Build the public download URL the manager hits for a given release. Used to write
    /// the <c>serverUrl</c> field into the index entry's Patreon block at save time.
    /// </summary>
    public static string BuildPublicUrl(
        ServerUploadConfig cfg, string gameId, string version, string fileName)
    {
        var trimmedBase = cfg.PublicBaseUrl.TrimEnd('/');
        return $"{trimmedBase}/{Uri.EscapeDataString(gameId)}/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(fileName)}";
    }

    private static SftpClient OpenSftp(ServerUploadConfig cfg)
    {
        // Read the key from disk every time — paths can change between calls and the user
        // could swap keys. SSH.NET's PrivateKeyFile accepts an optional passphrase.
        var key = string.IsNullOrEmpty(cfg.KeyPassphrase)
            ? new PrivateKeyFile(cfg.PrivateKeyPath)
            : new PrivateKeyFile(cfg.PrivateKeyPath, cfg.KeyPassphrase);
        var sftp = new SftpClient(cfg.Host, cfg.Port == 0 ? 22 : cfg.Port, cfg.User, key);
        sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        sftp.OperationTimeout = TimeSpan.FromMinutes(15); // tolerant of slow uploads of big ZIPs

        var expectedFingerprint = NormalizeHostKeyFingerprint(cfg.HostKeyFingerprint);
        string? hostKeyError = null;
        sftp.HostKeyReceived += (_, e) =>
        {
            var presentedFingerprint = FormatHostKeyFingerprint(e.HostKey);
            if (expectedFingerprint == null)
            {
                hostKeyError = BuildHostKeyError(
                    "Server host key fingerprint isn't configured.", presentedFingerprint);
                e.CanTrust = false;
                return;
            }

            e.CanTrust = string.Equals(
                expectedFingerprint, presentedFingerprint, StringComparison.Ordinal);
            if (!e.CanTrust)
            {
                hostKeyError = BuildHostKeyError(
                    "Server host key fingerprint doesn't match the configured value.",
                    presentedFingerprint);
            }
        };

        try
        {
            sftp.Connect();
        }
        catch (Exception ex) when (hostKeyError != null)
        {
            sftp.Dispose();
            throw new InvalidOperationException(hostKeyError, ex);
        }

        return sftp;
    }

    private static string FormatHostKeyFingerprint(byte[] hostKey)
    {
        var hash = SHA256.HashData(hostKey);
        return $"SHA256:{Convert.ToBase64String(hash).TrimEnd('=')}";
    }

    private static string? NormalizeHostKeyFingerprint(string? storedFingerprint)
    {
        if (string.IsNullOrWhiteSpace(storedFingerprint)) return null;

        var trimmed = storedFingerprint.Trim();
        return trimmed.StartsWith("SHA256:", StringComparison.Ordinal)
            ? trimmed
            : $"SHA256:{trimmed}";
    }

    private static string BuildHostKeyError(string reason, string presentedFingerprint) =>
        $"{reason} Presented fingerprint: {presentedFingerprint}. " +
        "Verify it out-of-band, then paste it into Server host key fingerprint in " +
        "Server upload settings.";

    private static void EnsureRemoteDirectory(SftpClient sftp, params string[] parts)
    {
        var current = "";
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            current = string.IsNullOrEmpty(current) ? part : JoinPosix(current, part);
            if (!sftp.Exists(current))
            {
                sftp.CreateDirectory(current);
            }
        }
    }

    /// <summary>
    /// Names a published asset may not take. <c>gate.json</c> is the tier lock itself: an asset
    /// with that name would be mistaken for a standing gate, skipped by the one-package-per-folder
    /// check, and then deleted outright when the release went public. A leading dot is how staged
    /// temporaries are marked, so those would be invisible to the same check.
    /// </summary>
    private static void RequireSafeAssetName(string fileName)
    {
        if (string.Equals(fileName, GateFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{fileName}' is the name the server uses for a release's Patreon tier lock, so a package " +
                "can't be called that. Rename the wrapped ZIP and publish again.");
        }

        if (fileName.StartsWith('.'))
        {
            throw new InvalidOperationException(
                $"'{fileName}' starts with a dot, which marks a half-uploaded staging file on the server. " +
                "Rename the wrapped ZIP and publish again.");
        }
    }

    private static void RequireSafeRemoteSegment(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('/') || value.Contains('\\') ||
            value == "." || value.Contains("..", StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Unsafe {what} for server upload: '{value}'. It must be a simple name with no path " +
                "separators or '..'.");
        }
    }

    private static string JoinPosix(params string[] parts)
    {
        // Always forward slashes — SFTP paths are POSIX even when the client runs on Windows.
        return string.Join("/", parts.Select(p => p.Trim('/')))
            .Replace("//", "/")
            .TrimEnd('/')
            .Insert(0, parts[0].StartsWith('/') ? "/" : "");
    }

    private static string BuildGateJson(PatreonGate gate)
    {
        var dto = new GateDto(gate.CampaignId, gate.TierIds);
        return JsonSerializer.Serialize(dto, GateJsonOptions);
    }

    /// <summary>
    /// Connection-only validation. Skips the public URL — it's only consumed at upload
    /// time when we write the URL into the index, so requiring it for "Test connection"
    /// would block the user from verifying SFTP works before they've finalised the URL.
    /// </summary>
    private static string? ValidateForConnection(ServerUploadConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Host)) return "Server host is required.";
        if (string.IsNullOrWhiteSpace(cfg.User)) return "Server user is required.";
        if (string.IsNullOrWhiteSpace(cfg.PrivateKeyPath))
            return "SSH private key path is required.";
        if (!File.Exists(cfg.PrivateKeyPath))
            return $"SSH private key file not found at '{cfg.PrivateKeyPath}'.";
        if (string.IsNullOrWhiteSpace(cfg.RemoteBasePath))
            return "Remote releases path is required.";
        if (!cfg.RemoteBasePath.StartsWith('/'))
            return "Remote releases path must be an absolute POSIX path (start with '/').";
        return null;
    }

    /// <summary>
    /// Full validation for actually uploading a release. Connection fields plus the
    /// public URL the manager needs to download from.
    /// </summary>
    private static string? ValidateForUpload(ServerUploadConfig cfg)
    {
        var connectionError = ValidateForConnection(cfg);
        if (connectionError != null) return connectionError;
        if (string.IsNullOrWhiteSpace(cfg.PublicBaseUrl))
            return "Public download base URL is required.";
        if (!cfg.PublicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "Public download base URL must use https://.";
        return null;
    }

    private sealed record GateDto(string CampaignId, List<string> TierIds);
}
