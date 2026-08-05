using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Disk-backed receipt store. Format v2 is a single self-contained file — a wrapper holding the
/// receipt JSON plus its SHA256 — written atomically (temp file + rename), so a crash can never
/// produce the old "receipt without its hash sidecar" state that read as tampering. Legacy v1
/// receipts (raw JSON + a .hash sidecar) still load and are upgraded on the next save. A file
/// that exists but can't be trusted is preserved with a '.corrupt' copy and reported through
/// <see cref="UnreadablePluginIdsForGameAsync"/> so the engine can fail closed instead of
/// treating the mod as absent.
/// </summary>
public sealed class ReceiptStore : IReceiptStore
{
    private static readonly string DefaultReceiptsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager",
        "receipts");

    private readonly string _root;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;

    public ReceiptStore(ILogger logger, string? rootOverride = null)
    {
        _logger = logger;
        _root = rootOverride ?? DefaultReceiptsRoot;
    }

    public async Task<InstallReceipt?> LoadAsync(string gameId, string pluginId)
    {
        var receiptPath = GetReceiptPath(pluginId, gameId);
        if (!File.Exists(receiptPath))
            return null;

        var json = await File.ReadAllTextAsync(receiptPath);

        var payload = ExtractVerifiedPayload(json, receiptPath, $"{pluginId}/{gameId}");
        if (payload == null)
            return null;

        try
        {
            var receipt = JsonSerializer.Deserialize<InstallReceipt>(payload, JsonOptions);
            _logger.Information("Loaded receipt for {PluginId}/{GameId}", pluginId, gameId);
            return receipt;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to deserialize receipt for {PluginId}/{GameId}", pluginId, gameId);
            PreserveCorrupt(receiptPath);
            return null;
        }
    }

    public async Task SaveAsync(InstallReceipt receipt)
    {
        var receiptPath = GetReceiptPath(receipt.PluginId, receipt.GameId);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        var payload = JsonSerializer.Serialize(receipt, JsonOptions);
        await AtomicJson.WriteWrappedAsync(receiptPath, payload);

        // A v2 save supersedes any legacy sidecar.
        var legacyHash = receiptPath + ".hash";
        try { if (File.Exists(legacyHash)) File.Delete(legacyHash); } catch { /* best effort */ }

        _logger.Information("Saved receipt for {PluginId}/{GameId} v{Version}", receipt.PluginId, receipt.GameId, receipt.InstalledVersion);
    }

    public async Task DeleteAsync(string gameId, string pluginId)
    {
        var receiptPath = GetReceiptPath(pluginId, gameId);

        if (File.Exists(receiptPath)) File.Delete(receiptPath);
        foreach (var sidecar in new[] { receiptPath + ".hash", receiptPath + ".corrupt" })
        {
            try { if (File.Exists(sidecar)) File.Delete(sidecar); } catch { /* best effort */ }
        }

        _logger.Information("Deleted receipt for {PluginId}/{GameId}", pluginId, gameId);
        await Task.CompletedTask;
    }

    public async Task<List<InstallReceipt>> LoadAllForGameAsync(string gameId)
    {
        var receipts = new List<InstallReceipt>();

        if (!Directory.Exists(_root))
            return receipts;

        foreach (var pluginDir in Directory.GetDirectories(_root))
        {
            var pluginId = Path.GetFileName(pluginDir);
            var receipt = await LoadAsync(gameId, pluginId);
            if (receipt != null)
                receipts.Add(receipt);
        }

        return receipts;
    }

    public async Task<List<string>> UnreadablePluginIdsForGameAsync(string gameId)
    {
        var unreadable = new List<string>();
        if (!Directory.Exists(_root))
            return unreadable;

        foreach (var pluginDir in Directory.GetDirectories(_root))
        {
            var pluginId = Path.GetFileName(pluginDir);
            var receiptPath = GetReceiptPath(pluginId, gameId);
            if (!File.Exists(receiptPath)) continue;
            if (await LoadAsync(gameId, pluginId) == null)
                unreadable.Add(pluginId);
        }
        return unreadable;
    }

    /// <summary>
    /// Returns the verified receipt-payload JSON, or null when the file can't be trusted.
    /// Handles both formats: the v2 wrapper (embedded hash) and legacy v1 (raw receipt JSON with a
    /// .hash sidecar; a missing sidecar means tampering — SaveAsync always wrote one).
    /// </summary>
    private string? ExtractVerifiedPayload(string json, string receiptPath, string label)
    {
        var wrapped = AtomicJson.TryReadWrapped(json);
        if (wrapped is { } w)
        {
            if (!w.HashValid)
            {
                _logger.Error("Receipt tamper detected for {Label} — embedded hash does not match", label);
                PreserveCorrupt(receiptPath);
                return null;
            }
            return w.Payload;
        }

        // Legacy v1: raw receipt JSON + .hash sidecar.
        var hashPath = receiptPath + ".hash";
        if (!File.Exists(hashPath))
        {
            _logger.Error("Receipt for {Label} has no integrity data — refusing to trust it", label);
            PreserveCorrupt(receiptPath);
            return null;
        }
        var storedHash = File.ReadAllText(hashPath).Trim();
        if (storedHash != ComputeHash(json))
        {
            _logger.Error("Receipt tamper detected for {Label} — stored hash does not match", label);
            PreserveCorrupt(receiptPath);
            return null;
        }
        return json;
    }

    private void PreserveCorrupt(string receiptPath)
    {
        try
        {
            File.Copy(receiptPath, receiptPath + ".corrupt", overwrite: true);
            _logger.Error("Preserved unreadable receipt as {Path}", receiptPath + ".corrupt");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't preserve corrupt receipt copy for {Path}", receiptPath);
        }
    }

    // pluginId/gameId come from the (unsigned) plugin index and manifest, so contain them to the
    // receipts root — a value like "..\..\evil" must never redirect a receipt write elsewhere.
    public string GetReceiptDirectory(string gameId, string pluginId) =>
        PathSafety.CombineContained(_root, pluginId, gameId);

    /// <summary>
    /// Plugin ids with something installed, taken from the top-level receipt folder names — the
    /// layout is <c>{root}/{pluginId}/{gameId}/receipt.json</c>, so the directory name IS the id.
    ///
    /// <para>Reads names only. The question is whether an identity is spoken for, and a receipt too
    /// damaged to parse still means it is — so an unreadable one reserves its id rather than
    /// quietly freeing it for someone else to claim.</para>
    /// </summary>
    public Task<List<string>> InstalledPluginIdsAsync()
    {
        if (!Directory.Exists(_root)) return Task.FromResult(new List<string>());

        try
        {
            // A folder alone is not an install. DeleteAsync removes the receipt and its sidecars but
            // leaves the {pluginId}/{gameId} directories behind, so counting bare directories would
            // reserve the id of a developer whose mods were ALL uninstalled — permanently, and
            // invisibly, because nothing would ever clean it up. Requiring a file underneath means
            // the reservation lasts exactly as long as something is actually installed.
            var ids = Directory.EnumerateDirectories(_root)
                .Where(HasAnyReceipt)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();
            return Task.FromResult(ids);
        }
        catch (Exception ex)
        {
            // Failing to read the folder must not be read as "nothing is installed" — that would
            // free every installed identity at once. Report none reserved is exactly the wrong
            // answer, so this rethrows rather than swallowing.
            _logger.Error(ex, "Couldn't enumerate receipt folders under {Root}", _root);
            throw;
        }
    }

    /// <summary>
    /// Whether a plugin folder still holds anything an install left behind — a receipt, its hash
    /// sidecar, a preserved corrupt copy, or a cached uninstall script. A DAMAGED receipt counts:
    /// the question is whether the identity is spoken for, and something too broken to parse is
    /// still something to uninstall.
    /// </summary>
    private static bool HasAnyReceipt(string pluginDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(pluginDirectory, "*", SearchOption.AllDirectories).Any();
        }
        catch
        {
            // Unreadable means "cannot prove it is empty", and the safe answer to that is that the
            // id stays reserved.
            return true;
        }
    }

    private string GetReceiptPath(string pluginId, string gameId) =>
        PathSafety.CombineContained(_root, pluginId, gameId, "receipt.json");

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}

/// <summary>
/// Shared plumbing for the v2 store format: a wrapper JSON holding a payload and its SHA256,
/// written atomically via temp-file + rename so readers only ever see the old file or the
/// complete new one — never a torn write or a payload without its hash.
/// </summary>
internal static class AtomicJson
{
    internal sealed record Wrapped(string Payload, bool HashValid);

    public static async Task WriteWrappedAsync(string path, string payload)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 2);
            writer.WriteString("sha256", hash);
            writer.WritePropertyName("payload");
            writer.WriteRawValue(payload);
            writer.WriteEndObject();
        }

        await WriteAtomicAsync(path, buffer.ToArray());
    }

    /// <summary>
    /// Parses a v2 wrapper. Null when the text isn't a wrapper at all (legacy format or foreign
    /// JSON); a Wrapped with <c>HashValid=false</c> when it is a wrapper whose hash fails.
    /// </summary>
    public static Wrapped? TryReadWrapped(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("formatVersion", out _) ||
                !root.TryGetProperty("sha256", out var hashEl) ||
                !root.TryGetProperty("payload", out var payloadEl))
                return null;

            var payload = payloadEl.GetRawText();
            var expected = hashEl.GetString() ?? "";
            var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            return new Wrapped(payload, string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes bytes to a temp file in the destination's directory, then renames over the target.
    /// Same-volume rename is atomic on NTFS, so a crash leaves either the old file or the new one.
    /// </summary>
    public static async Task WriteAtomicAsync(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }
}
