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
            Description = "Required for managers/mods that need the .NET 10 runtime.",
            Build = () => new Dependency
            {
                Id = "dotnet-10-desktop",
                Type = "system",
                Required = true,
                MinVersion = "10.0.0",
                Check = new DependencyCheck
                {
                    RegistryKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
                },
                Fix = new DependencyFix { DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0" }
            }
        }
    };
}
