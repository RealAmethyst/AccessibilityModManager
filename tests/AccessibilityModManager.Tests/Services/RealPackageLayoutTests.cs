using System.IO.Compression;
using System.Text;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// The package layout the AuthorTool actually builds, and the engine actually installs.
///
/// <para>Every other test in <see cref="PluginPackageValidationTests"/> constructs its own archive,
/// and for a while they all constructed the WRONG one: they wrote <c>sourceDir: "files/plugins"</c>
/// into their manifests, so the pre-publish check was written to match them and refused every real
/// package — including ones already published and installing correctly on users' machines. The
/// layout below is copied from two real builds (digimonsurvive beta2, which is live, and beta3,
/// which this check refused), so the contract is pinned by what ships rather than by what a test
/// found convenient.</para>
/// </summary>
public sealed class RealPackageLayoutTests
{
    /// <summary>
    /// Manifest sources are relative to <see cref="Manifest.PackageFilesFolder"/> and never spell
    /// it. `copyFolder BepInEx` reads `files/BepInEx/` out of the ZIP.
    /// </summary>
    [Fact]
    public void ARealPackage_PassesThePrePublishCheck()
    {
        using var zip = Build(
            manifest: """
                {
                  "gameId": "digimonsurvive",
                  "pluginId": "amethyst",
                  "modVersion": "1.0-beta3",
                  "installActions": [
                    { "type": "copyFolder", "sourceDir": "BepInEx", "targetDir": "BepInEx" },
                    { "type": "copyFile", "source": "Tolk.dll", "target": "Tolk.dll" }
                  ]
                }
                """,
            entries:
            [
                "files/BepInEx/plugins/SurviveAccess.dll",
                "files/BepInEx/plugins/prism.dll",
                "files/Tolk.dll"
            ]);

        var report = PluginPackageValidation.Validate(
            zip, "amethyst", "digimonsurvive", "1.0-beta3", TestLogger.Create());

        Assert.True(report.IsValid, string.Join(" | ", report.Errors));
    }

    /// <summary>
    /// The prefix belongs to the archive, not to the manifest. An author who writes it themselves is
    /// naming `files/files/...`, which is not in the package and would fail mid-install.
    /// </summary>
    [Fact]
    public void AManifestThatSpellsTheFilesPrefixItself_IsRejected()
    {
        using var zip = Build(
            manifest: """
                {
                  "gameId": "digimonsurvive",
                  "pluginId": "amethyst",
                  "modVersion": "1.0-beta3",
                  "installActions": [
                    { "type": "copyFolder", "sourceDir": "files/BepInEx", "targetDir": "BepInEx" }
                  ]
                }
                """,
            entries: ["files/BepInEx/plugins/SurviveAccess.dll"]);

        var report = PluginPackageValidation.Validate(
            zip, "amethyst", "digimonsurvive", "1.0-beta3", TestLogger.Create());

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("nothing under that folder"));
    }

    /// <summary>
    /// Lifecycle scripts are the exception: they run from the staging directory, which is the ZIP
    /// ROOT, so their paths do include the folder they sit in. The two rules living side by side is
    /// why <see cref="Manifest.PackageFilesFolder"/> is a named constant rather than a literal in
    /// each place.
    /// </summary>
    [Fact]
    public void ALifecycleScriptPath_IsRelativeToTheZipRoot_NotTheFilesFolder()
    {
        using var zip = Build(
            manifest: """
                {
                  "gameId": "digimonsurvive",
                  "pluginId": "amethyst",
                  "modVersion": "1.0-beta3",
                  "installActions": [ { "type": "copyFolder", "sourceDir": "BepInEx", "targetDir": "BepInEx" } ],
                  "postInstall": {
                    "executable": "scripts/setup.cmd",
                    "what": "Registers the speech bridge.",
                    "why": "The game loads it from a fixed path.",
                    "modifies": "Writes one registry value."
                  }
                }
                """,
            entries: ["files/BepInEx/plugins/SurviveAccess.dll", "scripts/setup.cmd"]);

        var report = PluginPackageValidation.Validate(
            zip, "amethyst", "digimonsurvive", "1.0-beta3", TestLogger.Create());

        Assert.True(report.IsValid, string.Join(" | ", report.Errors));
    }

    private static MemoryStream Build(string manifest, IEnumerable<string> entries)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "manifest.json", manifest);
            foreach (var entry in entries) Write(archive, entry, "bytes");
        }
        buffer.Position = 0;
        return buffer;
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
