using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Installer;

public sealed record DependencyInstallResult(
    bool Succeeded,
    DependencyReceipt? Receipt,
    string? ErrorMessage);

/// <summary>
/// Downloads and applies a single <see cref="DependencyAutoInstall"/>. Same security model as
/// the mod ZIP: HTTPS only, mandatory SHA256 (Q2=A), zip-slip-safe extraction (for
/// <see cref="ExtractZipAutoInstall"/>), backup-on-conflict per F4/F7=A so a hand-installed
/// loader's files are restorable on rollback.
/// </summary>
public sealed class DependencyAutoInstaller
{
    private readonly HttpClient _httpClient;
    private readonly IDependencyReceiptStore _receiptStore;
    private readonly ILogger _logger;

    public DependencyAutoInstaller(
        HttpClient httpClient,
        IDependencyReceiptStore receiptStore,
        ILogger logger)
    {
        _httpClient = httpClient;
        _receiptStore = receiptStore;
        _logger = logger;
    }

    public async Task<DependencyInstallResult> InstallAsync(
        Dependency dependency,
        GameInstall game,
        string requestingPluginId,
        IDependencyHost? host,
        CancellationToken ct)
    {
        var auto = dependency.Fix?.AutoInstall;
        if (auto == null)
            throw new InvalidOperationException(
                $"Dependency '{dependency.Id}' has no AutoInstall — caller should not have routed it here.");

        var url = dependency.Fix?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                $"Dependency '{dependency.Id}' AutoInstall is set but DownloadUrl is empty.");

        UrlValidator.RequireHttps(new Uri(url), $"dependency '{dependency.Id}' download");

        var existing = await _receiptStore.LoadAsync(game.Game.GameId, dependency.Id);
        if (existing != null)
        {
            // Already installed by another plugin (or this same plugin earlier). Bump the
            // refcount and short-circuit — the loader's already on disk, no work to do.
            if (!existing.DependentPluginIds.Contains(requestingPluginId))
            {
                existing.DependentPluginIds.Add(requestingPluginId);
                await _receiptStore.SaveAsync(existing);
                _logger.Information("Dep {DepId} already installed; added {Plugin} to refcount",
                    dependency.Id, requestingPluginId);
            }
            return new DependencyInstallResult(true, existing, null);
        }

        host?.OnDependencyStarting(dependency.Id, KindLabel(auto), dependency.Id);

        string? tempFile = null;
        try
        {
            tempFile = await DownloadAsync(url, dependency.Id, ct);
            await VerifySha256Async(tempFile, auto.Sha256, dependency.Id, ct);

            var backupFolder = _receiptStore.GetBackupDirectory(game.Game.GameId, dependency.Id);
            Directory.CreateDirectory(backupFolder);

            var (kind, changes) = auto switch
            {
                ExtractZipAutoInstall ez => ("extractZip",
                    ExtractZip(tempFile, ez, game.InstallPath, backupFolder)),
                RunInstallerAutoInstall ri => ("runInstaller",
                    await RunInstallerAsync(tempFile, ri, host, ct)),
                CopyFileAutoInstall cf => ("copyFile",
                    CopyFile(tempFile, cf, url, game.InstallPath, backupFolder)),
                _ => throw new InvalidOperationException($"Unknown AutoInstall kind: {auto.GetType().Name}")
            };

            var receipt = new DependencyReceipt
            {
                GameId = game.Game.GameId,
                DependencyId = dependency.Id,
                Kind = kind,
                InstalledAt = DateTime.UtcNow,
                Sha256 = auto.Sha256,
                Changes = changes,
                BackupFolder = backupFolder,
                DependentPluginIds = new List<string> { requestingPluginId }
            };
            await _receiptStore.SaveAsync(receipt);

            host?.OnDependencyFinished(dependency.Id, succeeded: true);
            return new DependencyInstallResult(true, receipt, null);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Dep auto-install failed: {DepId}", dependency.Id);
            host?.OnDependencyFinished(dependency.Id, succeeded: false);
            return new DependencyInstallResult(false, null, ex.Message);
        }
        finally
        {
            if (tempFile != null)
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
        }
    }

    private async Task<string> DownloadAsync(string url, string depId, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "AccessibilityModManager",
            $"depdl_{depId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);

        _logger.Information("Downloading dep {DepId} from {Url}", depId, url);
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var fs = File.Create(tempFile);
        await response.Content.CopyToAsync(fs, ct);
        return tempFile;
    }

    private async Task VerifySha256Async(string filePath, string expected, string depId, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, ct);
        var actual = Convert.ToHexStringLower(hashBytes);
        if (!string.Equals(actual, expected.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Dependency '{depId}' SHA256 mismatch. Expected {expected}, got {actual}. Aborting.");
        }
        _logger.Information("Dep {DepId} SHA256 verified: {Hash}", depId, actual);
    }

    private List<FileChange> ExtractZip(
        string zipPath, ExtractZipAutoInstall action, string gameDir, string backupFolder)
    {
        var targetDir = ResolveTargetDir(gameDir, action.TargetDir);
        Directory.CreateDirectory(targetDir);

        var fullGameDir = Path.GetFullPath(gameDir);
        var changes = new List<FileChange>();
        var blocklist = action.Blocklist ?? new List<string>();

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            if (IsBlocked(entry.FullName, blocklist)) continue;

            var destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!destPath.StartsWith(Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar))
                throw new SecurityException(
                    $"Zip slip detected in dependency: '{entry.FullName}' would extract outside '{targetDir}'.");

            // Backup-on-conflict (F7=A): if the file already exists, stash a copy before overwriting.
            var relativeToGame = Path.GetRelativePath(fullGameDir, destPath);
            FileChange change;
            if (File.Exists(destPath))
            {
                var backupRel = Path.Combine(relativeToGame);
                var backupFull = Path.Combine(backupFolder, backupRel);
                Directory.CreateDirectory(Path.GetDirectoryName(backupFull)!);
                File.Copy(destPath, backupFull, overwrite: true);
                change = new FileChange
                {
                    Type = ChangeType.Replaced,
                    RelativePath = relativeToGame,
                    BackupRelativePath = backupRel
                };
            }
            else
            {
                change = new FileChange
                {
                    Type = ChangeType.Added,
                    RelativePath = relativeToGame
                };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var es = entry.Open();
            using var fs = File.Create(destPath);
            es.CopyTo(fs);

            changes.Add(change);
        }

        _logger.Information("Dep extract complete: {Count} files placed in {Dir}", changes.Count, targetDir);
        return changes;
    }

    private List<FileChange> CopyFile(
        string sourcePath, CopyFileAutoInstall action, string downloadUrl,
        string gameDir, string backupFolder)
    {
        var targetDir = ResolveTargetDir(gameDir, action.TargetDir);
        var fileName = string.IsNullOrWhiteSpace(action.TargetFileName)
            ? Path.GetFileName(new Uri(downloadUrl).LocalPath)
            : action.TargetFileName!;

        var destPath = Path.GetFullPath(Path.Combine(targetDir, fileName));
        var fullGameDir = Path.GetFullPath(gameDir);
        if (!destPath.StartsWith(fullGameDir + Path.DirectorySeparatorChar))
            throw new InvalidOperationException(
                $"copyFile target '{destPath}' resolves outside the game folder.");

        Directory.CreateDirectory(targetDir);

        var relativeToGame = Path.GetRelativePath(fullGameDir, destPath);
        FileChange change;
        if (File.Exists(destPath))
        {
            var backupRel = relativeToGame;
            var backupFull = Path.Combine(backupFolder, backupRel);
            Directory.CreateDirectory(Path.GetDirectoryName(backupFull)!);
            File.Copy(destPath, backupFull, overwrite: true);
            change = new FileChange
            {
                Type = ChangeType.Replaced,
                RelativePath = relativeToGame,
                BackupRelativePath = backupRel
            };
        }
        else
        {
            change = new FileChange
            {
                Type = ChangeType.Added,
                RelativePath = relativeToGame
            };
        }

        File.Copy(sourcePath, destPath, overwrite: true);
        _logger.Information("Dep copyFile placed {File}", relativeToGame);
        return new List<FileChange> { change };
    }

    private async Task<List<FileChange>> RunInstallerAsync(
        string installerPath, RunInstallerAutoInstall action, IDependencyHost? host,
        CancellationToken ct)
    {
        // Move the downloaded artifact to a stable path with the right extension before
        // spawning — Windows resolves runners (.msi, .exe) by file extension.
        var renamed = installerPath;
        var declaredExt = Path.GetExtension(installerPath);
        if (string.IsNullOrEmpty(declaredExt))
        {
            renamed = installerPath + ".exe";
            File.Move(installerPath, renamed);
        }

        var psi = new ProcessStartInfo
        {
            FileName = renamed,
            UseShellExecute = action.NeedsAdmin,
            CreateNoWindow = !action.NeedsAdmin,
            RedirectStandardOutput = !action.NeedsAdmin,
            RedirectStandardError = !action.NeedsAdmin
        };
        if (action.NeedsAdmin)
            psi.Verb = "runas";
        else
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        foreach (var a in action.Args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        if (!action.NeedsAdmin)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data != null) host?.OnDependencyOutputLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) host?.OnDependencyOutputLine(e.Data); };
        }

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start dependency installer process.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (action.NeedsAdmin && ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("User declined the elevation prompt for the dependency installer.", ex);
        }

        if (!action.NeedsAdmin)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Dependency installer exited with code {process.ExitCode}.");

        // runInstaller's outputs aren't tracked as FileChanges — the installer owns its files.
        return new List<FileChange>();
    }

    private static string ResolveTargetDir(string gameDir, string? targetDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir)) return Path.GetFullPath(gameDir);

        var resolved = Path.GetFullPath(Path.Combine(gameDir, targetDir));
        var fullGameDir = Path.GetFullPath(gameDir);
        if (!resolved.StartsWith(fullGameDir + Path.DirectorySeparatorChar) && resolved != fullGameDir)
            throw new InvalidOperationException(
                $"AutoInstall targetDir '{targetDir}' resolves outside the game folder.");
        return resolved;
    }

    private static bool IsBlocked(string entryFullName, List<string> blocklist)
    {
        var name = entryFullName.Replace('\\', '/');
        foreach (var pattern in blocklist)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            // Simple glob: '*' matches any sequence of chars (no path-separator semantics — the
            // author can write 'docs/*' or '*.md' and we honor them as case-insensitive substrings.)
            var trimmed = pattern.Replace('\\', '/');
            if (GlobMatch(name, trimmed)) return true;
        }
        return false;
    }

    private static bool GlobMatch(string input, string pattern)
    {
        // Translate the glob to a simple regex (case-insensitive, anchored, '*' → '.*').
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => System.Text.RegularExpressions.Regex.Escape(c.ToString())
            });
        }
        sb.Append('$');
        return System.Text.RegularExpressions.Regex.IsMatch(input, sb.ToString(),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string KindLabel(DependencyAutoInstall auto) => auto switch
    {
        ExtractZipAutoInstall => "extractZip",
        RunInstallerAutoInstall => "runInstaller",
        CopyFileAutoInstall => "copyFile",
        _ => auto.GetType().Name
    };
}
