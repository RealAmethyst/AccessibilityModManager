using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// Wave-2 audit coverage for finding 6: atomic store writes, corrupt-file preservation, legacy
/// format fallback, unreadable reporting, and the config backup chain.
/// </summary>
public class StoreResilienceTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _tempRoot;

    public StoreResilienceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_stores_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private ReceiptStore MakeReceiptStore() =>
        new(TestLogger.Create(), Path.Combine(_tempRoot, "receipts"));

    private DependencyReceiptStore MakeDepStore() =>
        new(TestLogger.Create(), Path.Combine(_tempRoot, "depReceipts"));

    private ConfigService MakeConfigService() =>
        new(TestLogger.Create(), Path.Combine(_tempRoot, "config"));

    private static InstallReceipt MakeReceipt() => new()
    {
        GameId = "game-1",
        PluginId = "plug-a",
        InstalledVersion = "1.0.0",
        InstalledAt = DateTime.UtcNow,
        Changes = new List<FileChange> { new() { Type = ChangeType.Added, RelativePath = "mod.dll" } },
        BackupFolder = "unused",
        ManifestHash = "abc"
    };

    [Fact]
    public async Task ReceiptStore_SaveAndLoad_RoundtripsAndLeavesNoTempFiles()
    {
        var store = MakeReceiptStore();
        await store.SaveAsync(MakeReceipt());

        var loaded = await store.LoadAsync("game-1", "plug-a");
        Assert.NotNull(loaded);
        Assert.Equal("1.0.0", loaded!.InstalledVersion);
        Assert.Single(loaded.Changes);

        var dir = store.GetReceiptDirectory("game-1", "plug-a");
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.False(File.Exists(Path.Combine(dir, "receipt.json.hash")),
            "v2 format embeds the hash — no sidecar should exist");
    }

    [Fact]
    public async Task ReceiptStore_TamperedPayload_RefusedPreservedAndReported()
    {
        var store = MakeReceiptStore();
        await store.SaveAsync(MakeReceipt());

        var path = Path.Combine(store.GetReceiptDirectory("game-1", "plug-a"), "receipt.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("1.0.0", "6.6.6"));

        Assert.Null(await store.LoadAsync("game-1", "plug-a"));
        Assert.True(File.Exists(path + ".corrupt"), "the unreadable file must be preserved");
        var unreadable = await store.UnreadablePluginIdsForGameAsync("game-1");
        Assert.Contains("plug-a", unreadable);
    }

    [Fact]
    public async Task ReceiptStore_TruncatedFile_RefusedAndReported()
    {
        var store = MakeReceiptStore();
        await store.SaveAsync(MakeReceipt());

        var path = Path.Combine(store.GetReceiptDirectory("game-1", "plug-a"), "receipt.json");
        var full = File.ReadAllText(path);
        File.WriteAllText(path, full[..(full.Length / 2)]); // torn write

        Assert.Null(await store.LoadAsync("game-1", "plug-a"));
        Assert.Contains("plug-a", await store.UnreadablePluginIdsForGameAsync("game-1"));
    }

    [Fact]
    public async Task ReceiptStore_LegacyV1WithSidecar_StillLoads_AndUpgradesOnSave()
    {
        var store = MakeReceiptStore();
        var dir = store.GetReceiptDirectory("game-1", "plug-a");
        Directory.CreateDirectory(dir);

        var receiptJson = JsonSerializer.Serialize(MakeReceipt(), CamelCase);
        var path = Path.Combine(dir, "receipt.json");
        File.WriteAllText(path, receiptJson);
        File.WriteAllText(path + ".hash",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(receiptJson))));

        var loaded = await store.LoadAsync("game-1", "plug-a");
        Assert.NotNull(loaded);
        Assert.Equal("1.0.0", loaded!.InstalledVersion);

        // Saving upgrades to v2 and removes the sidecar.
        await store.SaveAsync(loaded);
        Assert.False(File.Exists(path + ".hash"));
        Assert.NotNull(await store.LoadAsync("game-1", "plug-a"));
    }

    [Fact]
    public async Task ReceiptStore_LegacyV1MissingSidecar_RefusedAsTampered()
    {
        var store = MakeReceiptStore();
        var dir = store.GetReceiptDirectory("game-1", "plug-a");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "receipt.json"),
            JsonSerializer.Serialize(MakeReceipt(), CamelCase));

        Assert.Null(await store.LoadAsync("game-1", "plug-a"));
        Assert.Contains("plug-a", await store.UnreadablePluginIdsForGameAsync("game-1"));
    }

    [Fact]
    public async Task DepStore_Tampered_ReportsUnreadable()
    {
        var store = MakeDepStore();
        await store.SaveAsync(new DependencyReceipt
        {
            GameId = "game-1",
            DependencyId = "melonloader",
            Kind = "extractZip",
            InstalledAt = DateTime.UtcNow,
            Sha256 = "deadbeef",
            Changes = new List<FileChange>(),
            BackupFolder = "unused",
            DependentPluginIds = new List<string> { "plug-a" }
        });
        Assert.False(await store.AnyUnreadableForGameAsync("game-1"));

        var path = Path.Combine(Path.Combine(_tempRoot, "depReceipts"), "game-1", "melonloader", "receipt.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("plug-a", "plug-x"));

        Assert.Null(await store.LoadAsync("game-1", "melonloader"));
        Assert.True(await store.AnyUnreadableForGameAsync("game-1"));
    }

    [Fact]
    public async Task Config_Save_WritesBackup_AndCorruptMainFallsBackToIt()
    {
        var service = MakeConfigService();
        var config = new AppConfig { DefaultChannel = "beta" };
        config.KnownGameOverrides["ptcgl"] = @"C:\PokemonTCGLive";
        await service.SaveAsync(config);

        var configPath = Path.Combine(_tempRoot, "config", "config.json");
        Assert.True(File.Exists(configPath + ".bak"));

        // Corrupt the main file — the backup must bring the settings back, with the problem
        // surfaced instead of silently resetting.
        File.WriteAllText(configPath, "{ definitely not json");
        var loaded = await service.LoadAsync();

        Assert.Equal("beta", loaded.DefaultChannel);
        Assert.Equal(@"C:\PokemonTCGLive", loaded.KnownGameOverrides["ptcgl"]);
        Assert.NotNull(service.LastLoadProblem);
        Assert.True(File.Exists(configPath + ".corrupt"));
    }

    [Fact]
    public async Task Config_MainAndBackupCorrupt_DefaultsWithProblemReported()
    {
        var service = MakeConfigService();
        await service.SaveAsync(new AppConfig { DefaultChannel = "beta" });

        var configPath = Path.Combine(_tempRoot, "config", "config.json");
        File.WriteAllText(configPath, "{ nope");
        File.WriteAllText(configPath + ".bak", "{ also nope");

        var loaded = await service.LoadAsync();
        Assert.Equal("stable", loaded.DefaultChannel); // defaults
        Assert.NotNull(service.LastLoadProblem);
        Assert.Contains("reset", service.LastLoadProblem);
    }

    [Fact]
    public async Task Config_CleanLoad_HasNoProblem()
    {
        var service = MakeConfigService();
        await service.SaveAsync(new AppConfig { DefaultChannel = "beta" });

        var loaded = await service.LoadAsync();
        Assert.Equal("beta", loaded.DefaultChannel);
        Assert.Null(service.LastLoadProblem);
    }
}
