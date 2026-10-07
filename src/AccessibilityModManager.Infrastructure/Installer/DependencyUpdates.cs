using System.Security.Cryptography;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Installer;

public sealed record DependencyUpdate(Dependency Dependency, bool PreviouslyUntracked);

public sealed class DependencyUpdates(DependencyAutoInstaller installer, ILogger logger,
    GameDependencyReceiptStore? gameReceipts = null)
{
    private readonly GameDependencyReceiptStore records = gameReceipts ?? new();

    public async Task EnsureReadyToLaunchAsync(GameInstall game, string target)
    {
        foreach (var dep in DependencyTargeting.ForTarget(game.Game.Dependencies, target))
        {
            var state = records.DirectoryFor(game.InstallPath, dep.Id);
            if (File.Exists(Path.Combine(state, "loader-pending.json")))
                throw new IOException("An interrupted dependency update needs recovery before launching. Its backup is recorded in " + state);
            if (!File.Exists(Path.Combine(state, "pending.json"))) continue;
            await using var locked = new FileStream(Path.Combine(state, "update.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            await RecoverAsync(game.InstallPath, state);
        }
    }

    public async Task<IReadOnlyList<DependencyUpdate>> FindAsync(GameInstall game, string target, CancellationToken ct = default)
    {
        if (await installer.HasUnreadableDependencyReceiptsAsync(game.Game.GameId))
            throw new InvalidDataException("An installed dependency record could not be read. Restore its backup before updating.");
        var updates = new List<DependencyUpdate>();
        foreach (var dep in DependencyTargeting.ForTarget(game.Game.Dependencies, target))
        {
            ct.ThrowIfCancellationRequested();
            if (dep.Fix?.AutoInstall is not { } auto || string.IsNullOrWhiteSpace(dep.Fix.DownloadUrl)) continue;
            if (OperatingSystem.IsLinux() && auto is RunInstallerAutoInstall) continue;
            if (dep.IsGameInstaller)
            {
                if (auto is not (ExtractAppAutoInstall or RunInstallerAutoInstall)) continue;
                var record = await records.LoadAsync(game.InstallPath, dep.Id);
                if (File.Exists(Path.Combine(records.DirectoryFor(game.InstallPath, dep.Id), "pending.json")) ||
                    record is null || record.Sha256 != auto.Sha256.ToLowerInvariant() || record.DownloadUrl != dep.Fix.DownloadUrl)
                    updates.Add(new(dep, record is null));
            }
            else
            {
                var state = records.DirectoryFor(game.InstallPath, dep.Id);
                if (File.Exists(Path.Combine(state, "loader-pending.json")))
                    throw new IOException("An interrupted dependency update needs recovery before launching. Its backup is recorded in " + state);
                var record = await installer.LoadReceiptAsync(game.Game.GameId, dep.Id);
                if (record?.InstallPath is { } recordedPath && GameDependencyReceiptStore.Normalize(recordedPath) != GameDependencyReceiptStore.Normalize(game.InstallPath))
                    throw new InvalidDataException("The dependency record belongs to a different game folder. Reinstall the dependency in this folder before updating.");
                if (record is not null && (!record.Sha256.Equals(auto.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    record.DownloadUrl is not null && record.DownloadUrl != dep.Fix.DownloadUrl))
                    updates.Add(new(dep, false));
            }
        }
        return updates;
    }

    public async Task ApplyAsync(GameInstall game, string target, IReadOnlyList<DependencyUpdate> updates,
        IDependencyHost? host, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        LinuxDependencySupport.EnsureSupported(game.Game, target);
        var allowed = DependencyTargeting.ForTarget(game.Game.Dependencies, target);
        foreach (var update in updates)
        {
            ct.ThrowIfCancellationRequested();
            var dep = update.Dependency;
            if (!allowed.Contains(dep)) throw new InvalidOperationException("The dependency changed. Check for updates again.");
            if (dep.IsGameInstaller && dep.Fix?.AutoInstall is ExtractAppAutoInstall)
                await UpdatePortableAsync(game, dep, target, host, progress, ct);
            else if (dep.IsGameInstaller && dep.Fix?.AutoInstall is RunInstallerAutoInstall)
            {
                await installer.RunGameInstallerAsync(dep, host, ct, progress, requireSuccess: true);
                await records.SaveAsync(new(game.InstallPath, dep.Id, dep.Fix.DownloadUrl!, dep.Fix.AutoInstall.Sha256.ToLowerInvariant(), []));
            }
            else
            {
                await UpdateManagedAsync(game, dep, host, ct);
            }
        }
    }

    private async Task UpdateManagedAsync(GameInstall game, Dependency dep, IDependencyHost? host, CancellationToken ct)
    {
        var state = records.DirectoryFor(game.InstallPath, dep.Id);
        Directory.CreateDirectory(state);
        await using var locked = new FileStream(Path.Combine(state, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var pending = Path.Combine(state, "loader-pending.json");
        if (File.Exists(pending)) throw new IOException("An interrupted dependency update needs recovery from " + state);
        var prior = await installer.LoadReceiptAsync(game.Game.GameId, dep.Id)
            ?? throw new InvalidOperationException("The installed dependency record changed. Check again before updating.");
        var backup = Path.Combine(records.DirectoryFor(game.InstallPath, dep.Id), "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var files = new Dictionary<string, bool>();
        foreach (var change in prior.Changes)
        {
            var path = PathSafety.CombineContained(game.InstallPath, change.RelativePath);
            PathSafety.EnsureNoReparseTraversal(game.InstallPath, path, "dependency update backup");
            files[change.RelativePath] = File.Exists(path);
            if (File.Exists(path)) Copy(path, PathSafety.CombineContained(backup, "files", change.RelativePath));
        }
        if (Directory.Exists(prior.BackupFolder))
            foreach (var file in Directory.EnumerateFiles(prior.BackupFolder, "*", SearchOption.AllDirectories))
                Copy(file, PathSafety.CombineContained(backup, "originals", Path.GetRelativePath(prior.BackupFolder, file)));
        await AtomicJson.WriteWrappedAsync(Path.Combine(backup, "receipt.json"), JsonSerializer.Serialize(prior));
        await AtomicJson.WriteWrappedAsync(pending, JsonSerializer.Serialize(new { Backup = backup, Receipt = prior, Files = files }));
        try
        {
            var result = await installer.InstallAsync(dep, game, game.PluginId, host, ct);
            if (!result.Succeeded) throw new IOException(result.ErrorMessage ?? "The dependency update failed.");
            File.Delete(pending);
        }
        catch
        {
            foreach (var file in files)
            {
                var destination = PathSafety.CombineContained(game.InstallPath, file.Key);
                PathSafety.EnsureNoReparseTraversal(game.InstallPath, destination, "dependency recovery");
                if (file.Value) Copy(PathSafety.CombineContained(backup, "files", file.Key), destination);
                else if (File.Exists(destination)) File.Delete(destination);
            }
            var originals = Path.Combine(backup, "originals");
            if (Directory.Exists(originals))
                foreach (var file in Directory.EnumerateFiles(originals, "*", SearchOption.AllDirectories))
                    Copy(file, PathSafety.CombineContained(prior.BackupFolder, Path.GetRelativePath(originals, file)));
            await installer.SaveReceiptAsync(prior);
            File.Delete(pending);
            throw;
        }
    }

    public async Task RecordPortableAsync(string installPath, Dependency dep, CancellationToken ct = default)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories))
        {
            PathSafety.EnsureNoReparseTraversal(installPath, file, "emulator file");
            files.Add(Path.GetRelativePath(installPath, file).Replace('\\', '/'), await HashAsync(file, ct));
        }
        await records.SaveAsync(new(installPath, dep.Id, dep.Fix!.DownloadUrl!, dep.Fix.AutoInstall!.Sha256.ToLowerInvariant(), files));
    }

    public async Task UpdatePortableAsync(GameInstall game, Dependency dep, string target,
        IDependencyHost? host, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(game.InstallPath);
        var state = records.DirectoryFor(root, dep.Id);
        Directory.CreateDirectory(state);
        // A process-wide and cross-process lock prevents simultaneous Play/update attempts.
        await using var locked = new FileStream(Path.Combine(state, "update.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        await RecoverAsync(root, state);
        var prior = await records.LoadAsync(root, dep.Id);
        var staging = Path.Combine(state, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await installer.ExtractPortableAppAsync(dep, staging, host, ct, progress);
            var exe = target == ReleaseTarget.Linux ? game.Game.LinuxExeName : game.Game.ExeName;
            if (string.IsNullOrWhiteSpace(exe)) throw new InvalidDataException("The game's executable is not configured for this platform.");
            exe = exe.Replace('\\', '/');
            var source = PortableAppLayout.ResolveInstallRoot(staging, exe)
                ?? throw new InvalidDataException("The dependency archive does not contain the configured game executable.");
            var incoming = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                incoming.Add(Path.GetRelativePath(source, file).Replace('\\', '/'), await HashAsync(file, ct));
            if (OperatingSystem.IsLinux())
            {
                var executable = PathSafety.CombineContained(source, exe);
                File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
            }
            // Remove obsolete files only when they still match the installed archive. User-
            // modified files, saves, ROMs and mod files outside the incoming payload stay put.
            var paths = new HashSet<string>(incoming.Keys, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (prior is not null)
                foreach (var old in prior.Files)
                {
                    var file = PathSafety.CombineContained(root, old.Key);
                    PathSafety.EnsureNoReparseTraversal(root, file, "emulator update");
                    if (!paths.Contains(old.Key) && File.Exists(file) && await HashAsync(file, ct) == old.Value)
                        paths.Add(old.Key);
                }
            var backupId = "backup-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N");
            var backup = Path.Combine(state, backupId);
            Directory.CreateDirectory(backup);
            var changes = new Dictionary<string, bool>();
            foreach (var relative in paths)
            {
                ct.ThrowIfCancellationRequested();
                var file = PathSafety.CombineContained(root, relative);
                PathSafety.EnsureNoReparseTraversal(root, file, "emulator update");
                if (Directory.Exists(file)) throw new IOException("A folder occupies an emulator file path: " + relative);
                var exists = File.Exists(file);
                changes.Add(relative, exists);
                if (exists) Copy(file, PathSafety.CombineContained(backup, "files", relative));
            }
            var journal = new UpdateJournal(backupId, changes, prior);
            await AtomicJson.WriteWrappedAsync(Path.Combine(state, "pending.json"), JsonSerializer.Serialize(journal));
            try
            {
                foreach (var relative in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    var destination = PathSafety.CombineContained(root, relative);
                    PathSafety.EnsureNoReparseTraversal(root, destination, "emulator update");
                    if (incoming.ContainsKey(relative)) Copy(PathSafety.CombineContained(source, relative), destination);
                    else File.Delete(destination);
                }
                foreach (var file in incoming)
                    if (await HashAsync(PathSafety.CombineContained(root, file.Key), ct) != file.Value)
                        throw new IOException("An updated emulator file did not verify: " + file.Key);
                await records.SaveAsync(new(root, dep.Id, dep.Fix!.DownloadUrl!, dep.Fix.AutoInstall!.Sha256.ToLowerInvariant(), incoming));
                File.Delete(Path.Combine(state, "pending.json"));
                logger.Information("Updated {Dependency}; previous files backed up at {Backup}", dep.Id, backup);
            }
            catch
            {
                await RecoverAsync(root, state);
                throw;
            }
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static async Task RecoverAsync(string root, string state)
    {
        var path = Path.Combine(state, "pending.json");
        if (!File.Exists(path)) return;
        var wrapped = AtomicJson.TryReadWrapped(await File.ReadAllTextAsync(path));
        if (wrapped is not { HashValid: true }) throw new InvalidDataException("The interrupted dependency update record is damaged.");
        var journal = JsonSerializer.Deserialize<UpdateJournal>(wrapped.Payload)!;
        var backup = PathSafety.CombineContained(state, journal.Backup);
        foreach (var change in journal.Files)
        {
            var destination = PathSafety.CombineContained(root, change.Key);
            PathSafety.EnsureNoReparseTraversal(root, destination, "dependency recovery");
            if (change.Value) Copy(PathSafety.CombineContained(backup, "files", change.Key), destination);
            else if (File.Exists(destination)) File.Delete(destination);
        }
        var receiptPath = Path.Combine(state, "receipt.json");
        if (journal.PreviousReceipt is not null)
            await AtomicJson.WriteWrappedAsync(receiptPath, JsonSerializer.Serialize(journal.PreviousReceipt));
        else if (File.Exists(receiptPath)) File.Delete(receiptPath);
        File.Delete(path);
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }

    private sealed record UpdateJournal(string Backup, Dictionary<string, bool> Files, GameDependencyReceipt? PreviousReceipt);
}
