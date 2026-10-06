using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Moves an earlier same-author Proton receipt to the catalog's current game ID.</summary>
public sealed class ProtonSetupIdentityMigration(string setupRoot, ReceiptStore receipts, ILogger logger,
    string? backupRoot = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<string> ImportAsync(GameInstall game, ProtonSteamSetupState existing,
        CancellationToken ct = default)
    {
        var pluginId = game.PluginId;
        var newId = game.Game.GameId;
        if (existing.GameId == newId || existing.PluginId != pluginId ||
            existing.SteamAppId != game.Game.SteamAppId ||
            existing.InstallPath != game.InstallPath ||
            existing.ProtonPrefixPath != game.ProtonPrefixPath)
            throw new InvalidOperationException("The existing setup does not match this author and Steam installation.");

        var oldSetup = PathSafety.CombineContained(setupRoot, pluginId, existing.GameId + ".json");
        var newSetup = PathSafety.CombineContained(setupRoot, pluginId, newId + ".json");
        var oldReceipt = PathSafety.CombineContained(receipts.GetReceiptDirectory(existing.GameId, pluginId), "receipt.json");
        var newReceipt = PathSafety.CombineContained(receipts.GetReceiptDirectory(newId, pluginId), "receipt.json");
        PathSafety.EnsureNoReparseTraversal(setupRoot, oldSetup, "earlier Proton setup");
        PathSafety.EnsureNoReparseTraversal(setupRoot, newSetup, "catalog Proton setup");
        var lockPaths = new[]
        {
            PathSafety.CombineContained(setupRoot, pluginId + "-" + existing.GameId + ".lock"),
            PathSafety.CombineContained(setupRoot, pluginId + "-" + newId + ".lock")
        }.Order(StringComparer.Ordinal).ToArray();
        Directory.CreateDirectory(setupRoot);
        using var first = OpenLock(lockPaths[0]);
        using var second = OpenLock(lockPaths[1]);
        if (File.Exists(newSetup) || File.Exists(newReceipt))
            throw new InvalidOperationException("The catalog identity already has an installation record. Nothing was changed.");

        var setupWrapped = AtomicJson.TryReadWrapped(await File.ReadAllTextAsync(oldSetup, ct));
        if (setupWrapped is not { HashValid: true })
            throw new InvalidDataException("The existing Proton setup record is damaged.");
        var recorded = JsonSerializer.Deserialize<ProtonSteamSetupState>(setupWrapped.Payload, JsonOptions);
        if (recorded is null || recorded.GameId != existing.GameId ||
            recorded.PluginId != pluginId || recorded.SteamAppId != existing.SteamAppId ||
            recorded.InstallPath != existing.InstallPath || recorded.ProtonPrefixPath != existing.ProtonPrefixPath ||
            recorded.Version != existing.Version || recorded.Plan.Installed != existing.Plan.Installed)
            throw new InvalidDataException("The Proton setup changed since it was displayed.");
        var receiptWrapped = AtomicJson.TryReadWrapped(await File.ReadAllTextAsync(oldReceipt, ct));
        if (receiptWrapped is not { HashValid: true })
            throw new InvalidDataException("The existing mod receipt is damaged.");
        var receipt = await receipts.LoadAsync(existing.GameId, pluginId);
        if (receipt is null || receipt.GameId != existing.GameId || receipt.PluginId != pluginId ||
            receipt.InstalledVersion != existing.Version || !Directory.Exists(receipt.BackupFolder) ||
            receipt.PostUninstall is not null || receipt.CachedPostUninstallExecutable is not null)
            throw new InvalidOperationException("The earlier receipt cannot be imported safely. Nothing was changed.");
        if (Directory.EnumerateFiles(Path.GetDirectoryName(oldReceipt)!).Count() != 1)
            throw new InvalidOperationException("The earlier receipt has extra recovery files. Nothing was changed.");

        var depRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessibilityModManager", "depReceipts");
        var oldDeps = PathSafety.CombineContained(depRoot, existing.GameId);
        if (Directory.Exists(oldDeps) && Directory.EnumerateFiles(oldDeps, "*", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("The earlier setup owns shared dependencies and needs a separate migration.");

        foreach (var owned in existing.ReloadedFiles)
        {
            var ownedRoot = PathSafety.IsContained(game.InstallPath, owned.Path)
                ? game.InstallPath
                : game.ProtonPrefixPath is { } prefix && PathSafety.IsContained(prefix, owned.Path)
                    ? prefix
                    : throw new InvalidDataException("A Reloaded II setting is outside the game and Proton prefix.");
            PathSafety.EnsureNoReparseTraversal(ownedRoot, owned.Path, "Reloaded II setting");
            if (!File.Exists(owned.Path) ||
                !File.ReadAllBytes(owned.Path).AsSpan().SequenceEqual(Convert.FromBase64String(owned.InstalledBase64)))
                throw new InvalidOperationException("An owned Reloaded II setting changed. Nothing was imported.");
        }

        var backup = PathSafety.CombineContained(
            backupRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AccessibilityModManager", "backups"),
            "identity-" + pluginId + "-" + existing.GameId + "-" +
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(backup);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var backupSetup = Path.Combine(backup, "setup.json");
        var backupReceipt = Path.Combine(backup, "receipt.json");
        File.Copy(oldSetup, backupSetup);
        File.Copy(oldReceipt, backupReceipt);
        var setupNode = JsonNode.Parse(setupWrapped.Payload)!.AsObject();
        var receiptNode = JsonNode.Parse(receiptWrapped.Payload)!.AsObject();
        setupNode["gameId"] = newId;
        receiptNode["gameId"] = newId;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newSetup)!);
            Directory.CreateDirectory(Path.GetDirectoryName(newReceipt)!);
            await AtomicJson.WriteWrappedAsync(newReceipt, receiptNode.ToJsonString());
            await AtomicJson.WriteWrappedAsync(newSetup, setupNode.ToJsonString());
            File.Delete(oldSetup);
            File.Delete(oldReceipt);
        }
        catch
        {
            if (!File.Exists(oldSetup)) File.Copy(backupSetup, oldSetup);
            if (!File.Exists(oldReceipt)) File.Copy(backupReceipt, oldReceipt);
            if (File.Exists(newSetup)) File.Delete(newSetup);
            if (File.Exists(newReceipt)) File.Delete(newReceipt);
            throw;
        }
        logger.Information("Imported Proton setup {PluginId}/{OldId} as {NewId}; backup {Backup}",
            pluginId, existing.GameId, newId, backup);
        return backup;
    }

    private static FileStream OpenLock(string path) =>
        new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
}
