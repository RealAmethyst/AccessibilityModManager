using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Disk-backed <see cref="IDependencyReceiptStore"/>. Layout mirrors <see cref="ReceiptStore"/>:
/// each receipt lives in its own folder under LocalAppData with a tamper-detection hash file
/// next to the JSON. Scoped per (gameId, dependencyId) — never per plugin — because a single
/// dep can be shared across many mods (F10=C). The plugin refcount lives inside the receipt's
/// <c>DependentPluginIds</c> field.
/// </summary>
public sealed class DependencyReceiptStore : IDependencyReceiptStore
{
    private static readonly string DepReceiptsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager",
        "depReceipts");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;

    public DependencyReceiptStore(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<DependencyReceipt?> LoadAsync(string gameId, string dependencyId)
    {
        var receiptPath = GetReceiptPath(gameId, dependencyId);
        var hashPath = receiptPath + ".hash";

        if (!File.Exists(receiptPath))
            return null;

        var json = await File.ReadAllTextAsync(receiptPath);

        if (File.Exists(hashPath))
        {
            var storedHash = await File.ReadAllTextAsync(hashPath);
            if (storedHash.Trim() != ComputeHash(json))
            {
                _logger.Error("Dep receipt tamper detected for {DepId}/{GameId}", dependencyId, gameId);
                return null;
            }
        }
        else
        {
            // SaveAsync always writes the hash sidecar — a missing hash means tampering; fail closed.
            _logger.Error("Dep receipt for {DepId}/{GameId} has no hash file — refusing to trust it", dependencyId, gameId);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DependencyReceipt>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to deserialize dep receipt for {DepId}/{GameId}", dependencyId, gameId);
            return null;
        }
    }

    public async Task SaveAsync(DependencyReceipt receipt)
    {
        var receiptPath = GetReceiptPath(receipt.GameId, receipt.DependencyId);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        var json = JsonSerializer.Serialize(receipt, JsonOptions);
        await File.WriteAllTextAsync(receiptPath, json);
        await File.WriteAllTextAsync(receiptPath + ".hash", ComputeHash(json));

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
        var gameRoot = PathSafety.CombineContained(DepReceiptsRoot, gameId);
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

    // gameId/dependencyId are untrusted (plugin index) — contain them to the dep-receipts root.
    public string GetBackupDirectory(string gameId, string dependencyId) =>
        PathSafety.CombineContained(DepReceiptsRoot, gameId, dependencyId, "backup");

    private static string GetReceiptPath(string gameId, string dependencyId) =>
        PathSafety.CombineContained(DepReceiptsRoot, gameId, dependencyId, "receipt.json");

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}
