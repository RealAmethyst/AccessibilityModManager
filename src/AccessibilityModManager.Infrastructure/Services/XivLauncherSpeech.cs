using System.Security.Cryptography;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Detects the bundled Prism ABI and stages the matching Wine bridges for a new package.</summary>
public static class XivLauncherSpeech
{
    public const string Prism0173Sha256 = "287cd79e5d6cac5204605ba23e73bd043f48192ade54ddadf50ac4fcd9b94aae";
    public const string Prism0183Sha256 = "7c7d09c8c7306e8e1a0c46d603ccb2a391c194c77843400c11b746e7dd56a467";

    public static async Task<string> DetectVersionAsync(string prismPath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(prismPath);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        return VersionForHash(hash);
    }

    public static string VersionForHash(string sha256) => sha256.ToLowerInvariant() switch
    {
        Prism0173Sha256 => "0.17.3",
        Prism0183Sha256 => "0.18.3",
        _ => throw new InvalidDataException("The bundled prism.dll is not a recognized build with a verified Linux speech bridge. " +
            "Keep the plugin's matching Prism DLL; this build needs bridge compatibility support before it can be packaged for Linux.")
    };

    public static async Task PrepareAsync(string pluginDirectory, string bridgeDirectory,
        HttpClient client, string cacheRoot, CancellationToken ct = default)
    {
        var prism = Path.Combine(pluginDirectory, "prism.dll");
        var version = await DetectVersionAsync(prism, ct);
        var bundle = await new PrismBridgeProvisioner(client, cacheRoot).ProvisionAsync(PrismBridgeRelease.V0183X64, ct);
        if (version == "0.17.3")
        {
            var adapter = await Prism0173Compatibility.ExtractAsync(pluginDirectory, ct);
            File.Move(adapter, prism, overwrite: true);
            File.Copy(bundle.WindowsPrismDll, Path.Combine(pluginDirectory, "prism-core.dll"));
        }
        Directory.CreateDirectory(bridgeDirectory);
        foreach (var name in PrismBridgeRelease.V0183X64.BridgeNames)
        {
            File.Copy(Path.Combine(bundle.WindowsPlaceholdersDirectory, name + ".dll"), Path.Combine(pluginDirectory, name + ".dll"));
            File.Copy(Path.Combine(bundle.HostModulesDirectory, name + ".dll.so"), Path.Combine(bridgeDirectory, name + ".dll.so"));
        }
        foreach (var file in Directory.EnumerateFiles(bundle.NoticesDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(bridgeDirectory, "notices", Path.GetRelativePath(bundle.NoticesDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
