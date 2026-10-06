using System.Text.Json;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Reads verified Steam setup ownership records by Steam game, across catalog game IDs.</summary>
public static class ProtonSteamSetupLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IReadOnlyList<ProtonSteamSetupState> Find(string setupRoot,
        string steamAppId, string installPath)
    {
        if (!Directory.Exists(setupRoot)) return [];
        var owners = new List<ProtonSteamSetupState>();
        foreach (var path in Directory.EnumerateFiles(setupRoot, "*.json", SearchOption.AllDirectories))
        {
            PathSafety.EnsureNoReparseTraversal(setupRoot, path, "Proton setup record");
            var wrapped = AtomicJson.TryReadWrapped(File.ReadAllText(path));
            if (wrapped is not { HashValid: true })
                throw new InvalidDataException($"Proton setup record '{path}' is damaged. It was left untouched for recovery.");
            var state = JsonSerializer.Deserialize<ProtonSteamSetupState>(wrapped.Payload, JsonOptions)
                ?? throw new InvalidDataException($"Proton setup record '{path}' has no data.");
            if (state.SteamAppId == steamAppId && state.InstallPath == installPath)
                owners.Add(state);
        }
        return owners;
    }
}
