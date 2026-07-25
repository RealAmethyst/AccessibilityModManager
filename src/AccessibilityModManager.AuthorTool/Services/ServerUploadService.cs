using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

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
    public async Task PublishIndexAsync(
        ServerUploadConfig cfg, string pluginId, byte[] indexJson, CancellationToken ct)
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
            using var sftp = OpenSftp(cfg);

            if (!sftp.Exists(cfg.RemoteCatalogRoot.TrimEnd('/')))
                throw new InvalidOperationException(
                    $"Remote catalog folder '{cfg.RemoteCatalogRoot}' doesn't exist on the server. " +
                    "Check the catalog root in Server upload settings.");
            EnsureRemoteDirectory(sftp, cfg.RemoteCatalogRoot.TrimEnd('/'), "plugins", pluginId);

            try
            {
                using (var src = new MemoryStream(indexJson))
                    sftp.UploadFile(src, tempIndex, canOverride: true);
                sftp.RenameFile(tempIndex, liveIndex, isPosix: true);
            }
            catch
            {
                TryDeleteRemote(sftp, tempIndex);
                throw;
            }

            _logger.Information("Index live at {Path}", liveIndex);
        }, ct);
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
