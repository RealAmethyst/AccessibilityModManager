using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed class ReceiptStore : IReceiptStore
{
    private static readonly string ReceiptsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager",
        "receipts");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;

    public ReceiptStore(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<InstallReceipt?> LoadAsync(string gameId, string pluginId)
    {
        var receiptPath = GetReceiptPath(pluginId, gameId);
        var hashPath = receiptPath + ".hash";

        if (!File.Exists(receiptPath))
            return null;

        var json = await File.ReadAllTextAsync(receiptPath);

        // Tamper check
        if (File.Exists(hashPath))
        {
            var storedHash = await File.ReadAllTextAsync(hashPath);
            var computedHash = ComputeHash(json);
            if (storedHash.Trim() != computedHash)
            {
                _logger.Error("Receipt tamper detected for {PluginId}/{GameId} — stored hash does not match", pluginId, gameId);
                return null;
            }
        }
        else
        {
            _logger.Warning("No hash file for receipt {PluginId}/{GameId}", pluginId, gameId);
        }

        try
        {
            var receipt = JsonSerializer.Deserialize<InstallReceipt>(json, JsonOptions);
            _logger.Information("Loaded receipt for {PluginId}/{GameId}", pluginId, gameId);
            return receipt;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to deserialize receipt for {PluginId}/{GameId}", pluginId, gameId);
            return null;
        }
    }

    public async Task SaveAsync(InstallReceipt receipt)
    {
        var receiptPath = GetReceiptPath(receipt.PluginId, receipt.GameId);
        var hashPath = receiptPath + ".hash";

        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        var json = JsonSerializer.Serialize(receipt, JsonOptions);
        await File.WriteAllTextAsync(receiptPath, json);

        // Write tamper-detection hash
        var hash = ComputeHash(json);
        await File.WriteAllTextAsync(hashPath, hash);

        _logger.Information("Saved receipt for {PluginId}/{GameId} v{Version}", receipt.PluginId, receipt.GameId, receipt.InstalledVersion);
    }

    public async Task DeleteAsync(string gameId, string pluginId)
    {
        var receiptPath = GetReceiptPath(pluginId, gameId);
        var hashPath = receiptPath + ".hash";

        if (File.Exists(receiptPath)) File.Delete(receiptPath);
        if (File.Exists(hashPath)) File.Delete(hashPath);

        _logger.Information("Deleted receipt for {PluginId}/{GameId}", pluginId, gameId);
        await Task.CompletedTask;
    }

    public async Task<List<InstallReceipt>> LoadAllForGameAsync(string gameId)
    {
        var receipts = new List<InstallReceipt>();

        if (!Directory.Exists(ReceiptsRoot))
            return receipts;

        foreach (var pluginDir in Directory.GetDirectories(ReceiptsRoot))
        {
            var pluginId = Path.GetFileName(pluginDir);
            var receipt = await LoadAsync(gameId, pluginId);
            if (receipt != null)
                receipts.Add(receipt);
        }

        return receipts;
    }

    public string GetReceiptDirectory(string gameId, string pluginId) =>
        Path.Combine(ReceiptsRoot, pluginId, gameId);

    private static string GetReceiptPath(string pluginId, string gameId) =>
        Path.Combine(ReceiptsRoot, pluginId, gameId, "receipt.json");

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}
