using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Disk-backed <see cref="IDependencyReceiptStore"/>. Layout mirrors <see cref="ReceiptStore"/>:
/// each receipt lives in its own folder under LocalAppData, stored in the atomic single-file v2
/// format (embedded hash, temp-file + rename) with legacy v1 (.hash sidecar) fallback, and
/// unreadable files preserved as '.corrupt'. Scoped per (gameId, dependencyId) — never per
/// plugin — because a single dep can be shared across many mods (F10=C). The plugin refcount
/// lives inside the receipt's <c>DependentPluginIds</c> field.
/// </summary>
public sealed class DependencyReceiptStore : IDependencyReceiptStore
{
    private static readonly string DefaultDepReceiptsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager",
        "depReceipts");

    private readonly string _root;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;

    public DependencyReceiptStore(ILogger logger, string? rootOverride = null)
    {
        _logger = logger;
        _root = rootOverride ?? DefaultDepReceiptsRoot;
    }

    public async Task<DependencyReceipt?> LoadAsync(string gameId, string dependencyId)
    {
        var receiptPath = GetReceiptPath(gameId, dependencyId);
        if (!File.Exists(receiptPath))
            return null;

        var json = await File.ReadAllTextAsync(receiptPath);
        var label = $"{dependencyId}/{gameId}";

        string? payload;
        var wrapped = AtomicJson.TryReadWrapped(json);
        if (wrapped is { } w)
        {
            if (!w.HashValid)
            {
                _logger.Error("Dep receipt tamper detected for {Label} — embedded hash does not match", label);
                PreserveCorrupt(receiptPath);
                return null;
            }
            payload = w.Payload;
        }
        else
        {
            // Legacy v1: raw receipt JSON + .hash sidecar; missing sidecar means tampering.
            var hashPath = receiptPath + ".hash";
            if (!File.Exists(hashPath))
            {
                _logger.Error("Dep receipt for {Label} has no integrity data — refusing to trust it", label);
                PreserveCorrupt(receiptPath);
                return null;
            }
            var storedHash = (await File.ReadAllTextAsync(hashPath)).Trim();
            var actual = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
            if (storedHash != actual)
            {
                _logger.Error("Dep receipt tamper detected for {Label}", label);
                PreserveCorrupt(receiptPath);
                return null;
            }
            payload = json;
        }

        try
        {
            return JsonSerializer.Deserialize<DependencyReceipt>(payload, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to deserialize dep receipt for {Label}", label);
            PreserveCorrupt(receiptPath);
            return null;
        }
    }

    public async Task SaveAsync(DependencyReceipt receipt)
    {
        var receiptPath = GetReceiptPath(receipt.GameId, receipt.DependencyId);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        var payload = JsonSerializer.Serialize(receipt, JsonOptions);
        await AtomicJson.WriteWrappedAsync(receiptPath, payload);

        var legacyHash = receiptPath + ".hash";
        try { if (File.Exists(legacyHash)) File.Delete(legacyHash); } catch { /* best effort */ }

        _logger.Information("Saved dep receipt for {DepId}/{GameId} (refcount={Count})",
            receipt.DependencyId, receipt.GameId, receipt.DependentPluginIds.Count);
    }

    public Task DeleteAsync(string gameId, string dependencyId)
    {
        var receiptPath = GetReceiptPath(gameId, dependencyId);
        var hashPath = receiptPath + ".hash";
        if (File.Exists(receiptPath)) File.Delete(receiptPath);
        if (File.Exists(hashPath)) File.Delete(hashPath);

        var dir = Path.GetDirectoryName(receiptPath)!;
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }

        _logger.Information("Deleted dep receipt for {DepId}/{GameId}", dependencyId, gameId);
        return Task.CompletedTask;
    }

    public async Task<List<DependencyReceipt>> LoadAllForGameAsync(string gameId)
    {
        var receipts = new List<DependencyReceipt>();
        // gameId is untrusted — contain it so a "..\.." value can't enumerate outside the root.
        var gameRoot = PathSafety.CombineContained(_root, gameId);
        if (!Directory.Exists(gameRoot))
            return receipts;

        foreach (var depDir in Directory.GetDirectories(gameRoot))
        {
            var depId = Path.GetFileName(depDir);
            var r = await LoadAsync(gameId, depId);
            if (r != null) receipts.Add(r);
        }
        return receipts;
    }

    public async Task<bool> AnyUnreadableForGameAsync(string gameId)
    {
        var gameRoot = PathSafety.CombineContained(_root, gameId);
        if (!Directory.Exists(gameRoot))
            return false;

        foreach (var depDir in Directory.GetDirectories(gameRoot))
        {
            var depId = Path.GetFileName(depDir);
            if (!File.Exists(GetReceiptPath(gameId, depId))) continue;
            if (await LoadAsync(gameId, depId) == null)
                return true;
        }
        return false;
    }

    private void PreserveCorrupt(string receiptPath)
    {
        try
        {
            File.Copy(receiptPath, receiptPath + ".corrupt", overwrite: true);
            _logger.Error("Preserved unreadable dep receipt as {Path}", receiptPath + ".corrupt");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't preserve corrupt dep receipt copy for {Path}", receiptPath);
        }
    }

    // gameId/dependencyId are untrusted (plugin index) — contain them to the dep-receipts root.
    public string GetBackupDirectory(string gameId, string dependencyId) =>
        PathSafety.CombineContained(_root, gameId, dependencyId, "backup");

    private string GetReceiptPath(string gameId, string dependencyId) =>
        PathSafety.CombineContained(_root, gameId, dependencyId, "receipt.json");
}
