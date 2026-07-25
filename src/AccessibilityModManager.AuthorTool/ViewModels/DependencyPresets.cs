using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed class DependencyPreset
{
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required Func<Dependency> Build { get; init; }

    public Dependency ToModel() => Build();
    public override string ToString() => DisplayName;
}

public static class DependencyPresetsBag
{
    public static IReadOnlyList<DependencyPreset> Presets => DependencyPresets.All;
}

public static class DependencyPresets
{
    public static IReadOnlyList<DependencyPreset> All { get; } = new[]
    {
        new DependencyPreset
        {
            DisplayName = "Emulator (portable app)",
            Description = "The emulator itself, delivered as a portable ZIP. \"This dependency is the " +
                          "game itself\" is already ticked and the auto-install kind is set to extractApp. " +
                          "Set the game's Exe name (General tab) to the emulator's exe, then paste the " +
                          "ZIP's HTTPS URL below and click \"Fetch from URL\" for the SHA256.",
            Build = () => new Dependency
            {
                Id = "emulator",
                Type = "system",
                Required = true,
                IsGameInstaller = true,
                Fix = new DependencyFix
                {
                    // Author fills these in: the emulator ZIP's HTTPS URL, and its SHA256 (Fetch from URL).
                    DownloadUrl = "",
                    AutoInstall = new ExtractAppAutoInstall { Sha256 = "" }
                }
            }
        },
        new DependencyPreset
        {
            DisplayName = "MelonLoader",
            Description = "MelonLoader runtime; checked by version.dll in the game folder.",
            Build = () => new Dependency
            {
                Id = "melonloader",
                Type = "framework",
                Required = true,
                Check = new DependencyCheck { FilePath = "version.dll" },
                Fix = new DependencyFix { DownloadUrl = "https://github.com/LavaGang/MelonLoader/releases" }
            }
        },
        new DependencyPreset
        {
            DisplayName = "BepInEx",
            Description = "BepInEx framework; checked by winhttp.dll in the game folder.",
            Build = () => new Dependency
            {
                Id = "bepinex",
                Type = "framework",
                Required = true,
                Check = new DependencyCheck { FilePath = "winhttp.dll" },
                Fix = new DependencyFix { DownloadUrl = "https://github.com/BepInEx/BepInEx/releases" }
            }
        },
        new DependencyPreset
        {
            DisplayName = ".NET 10 Desktop Runtime",
            Description = "Required for managers/mods that need the .NET 10 runtime. Checked via " +
                          "the runtime's registry record (version-named entries; the x64 runtime " +
                          "records under the 32-bit registry view, which the checker probes automatically).",
            Build = () => new Dependency
            {
                Id = "dotnet-10-desktop",
                Type = "system",
                Required = true,
                MinVersion = "10.0.0",
                Check = new DependencyCheck
                {
                    // Deliberately NO RegistryValue and NO view pin: the installed versions are
                    // the value NAMES under this key (highest wins vs MinVersion), and the x64
                    // runtime writes it under the 32-bit view — the default both-views probe is
                    // what finds it (audit finding 10; verified against a real install 2026-07-25).
                    RegistryKey = @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
                },
                Fix = new DependencyFix { DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0" }
            }
        }
    };
}
