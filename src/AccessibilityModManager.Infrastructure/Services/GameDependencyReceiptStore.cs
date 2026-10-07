using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed record GameDependencyReceipt(string InstallPath, string DependencyId, string DownloadUrl,
    string Sha256, Dictionary<string, string> Files);

// Separate from loader refcounts: uninstalling a mod must never uninstall its game/emulator.
public sealed class GameDependencyReceiptStore(string? rootOverride = null)
{
    private readonly string root = rootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager", "gameDependencies");

    public string DirectoryFor(string installPath, string dependencyId)
    {
        var identity = Normalize(installPath) + "\n" + dependencyId;
        return Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }

    public async Task<GameDependencyReceipt?> LoadAsync(string installPath, string dependencyId)
    {
        var file = Path.Combine(DirectoryFor(installPath, dependencyId), "receipt.json");
        if (!File.Exists(file)) return null;
        var wrapped = AtomicJson.TryReadWrapped(await File.ReadAllTextAsync(file));
        if (wrapped is not { HashValid: true }) throw new InvalidDataException("The game dependency record is damaged. Restore its backup before updating.");
        var receipt = JsonSerializer.Deserialize<GameDependencyReceipt>(wrapped.Payload)
            ?? throw new InvalidDataException("The game dependency record is empty.");
        if (Normalize(receipt.InstallPath) != Normalize(installPath) || receipt.DependencyId != dependencyId)
            throw new InvalidDataException("The game dependency record belongs to another installation.");
        foreach (var path in receipt.Files.Keys) PathSafety.CombineContained(installPath, path);
        return receipt;
    }

    public Task SaveAsync(GameDependencyReceipt receipt) => AtomicJson.WriteWrappedAsync(
        Path.Combine(DirectoryFor(receipt.InstallPath, receipt.DependencyId), "receipt.json"), JsonSerializer.Serialize(receipt));

    public static string Normalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }
}
