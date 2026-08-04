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
        },
        Net9Desktop(
            bits: 64,
            registryArch: "x64",
            installerUrl: Net9X64Url,
            sha256: Net9X64Sha256),
        Net9Desktop(
            bits: 32,
            registryArch: "x86",
            installerUrl: Net9X86Url,
            sha256: Net9X86Sha256)
    };

    // .NET 9.0.18, the current 9.0 patch as of 2026-08-04. Both URLs came from Microsoft's own
    // release metadata (release-metadata/9.0/releases.json) rather than being typed out, and each
    // file was downloaded and its SHA512 checked against the hash that metadata publishes — so the
    // SHA256 below is provably the hash of the genuine installer, not merely of whatever answered.
    //
    // Pinned to an exact patch on purpose: the manager's SHA256 gate is absolute, so an "always
    // latest" address would start failing the moment Microsoft ships 9.0.19. Bumping this preset is
    // a deliberate edit, and the hash has to be re-derived with it.
    private const string Net9X64Url =
        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/9.0.18/windowsdesktop-runtime-9.0.18-win-x64.exe";
    private const string Net9X64Sha256 =
        "12cd00688fc9f8f5187d25911bf656db61998c264f03eef4022ff2d9321d6982";

    private const string Net9X86Url =
        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/9.0.18/windowsdesktop-runtime-9.0.18-win-x86.exe";
    private const string Net9X86Sha256 =
        "a90bc401a7838f036a4d615ca7031099b4b950ed6a8f59f59c44150c6ad7d648";

    /// <summary>
    /// The .NET 9 Desktop Runtime, in one architecture, ready to install without the author filling
    /// anything in.
    ///
    /// <para><b>Which one a game needs is not a detail.</b> A 32-bit game loads the 32-bit runtime
    /// and a 64-bit game the 64-bit one; installing the wrong one leaves the mod unable to start
    /// with nothing obviously wrong. They install side by side, so a machine can want both — which
    /// is why these are two presets with two ids rather than one with a switch.</para>
    ///
    /// <para><b>How the check works.</b> Installed versions are the value NAMES under the key, and
    /// the checker takes the highest and compares it against MinVersion. The architecture is part of
    /// the KEY PATH (…\x64\… vs …\x86\…), not the registry view — both actually live under
    /// WOW6432Node, and the checker probes both views by default, which is what finds them. Verified
    /// against a real machine on 2026-08-04, where x64 held 6.0.5 through 10.0.8 and x86 held 5.0.17
    /// through 10.0.10.</para>
    ///
    /// <para><b>The one thing to know:</b> "highest wins" means a machine with only .NET 10 passes a
    /// MinVersion of 9.0.0, and a mod built for net9.0 will NOT run on 10 alone — .NET rolls forward
    /// across patches, not across major versions. Getting that exactly right needs the check to be
    /// able to say "some 9.x", which it currently cannot express.</para>
    /// </summary>
    private static DependencyPreset Net9Desktop(int bits, string registryArch, string installerUrl, string sha256) =>
        new()
        {
            DisplayName = $".NET 9 Desktop Runtime ({bits}-bit)",
            Description =
                $"The {bits}-bit .NET 9 Desktop Runtime (9.0.18), for a {bits}-bit game. The download " +
                "address and SHA256 are already filled in and verified, and the manager installs it " +
                "silently with the user's consent. Checked by the runtime's own registry record, so " +
                "any 9.x or newer counts — no exact patch to keep up to date. Pick the architecture " +
                "that matches the game: the wrong one leaves the mod unable to start.",
            Build = () => new Dependency
            {
                Id = $"dotnet-9-desktop-{registryArch}",
                Type = "system",
                Required = true,
                MinVersion = "9.0.0",
                Check = new DependencyCheck
                {
                    // No RegistryValue and no view pin, matching the .NET 10 preset: the versions are
                    // the value names, and the record lives under the 32-bit view that the default
                    // both-views probe reaches.
                    RegistryKey =
                        $@"SOFTWARE\dotnet\Setup\InstalledVersions\{registryArch}\sharedfx\Microsoft.WindowsDesktop.App"
                },
                Fix = new DependencyFix
                {
                    DownloadUrl = installerUrl,
                    AutoInstall = new RunInstallerAutoInstall
                    {
                        Sha256 = sha256,
                        // Microsoft's own switches. /norestart matters: the installer will otherwise
                        // reboot the machine out from under someone mid-install.
                        Args = ["/install", "/quiet", "/norestart"],
                        NeedsAdmin = true
                    }
                }
            }
        };
}
