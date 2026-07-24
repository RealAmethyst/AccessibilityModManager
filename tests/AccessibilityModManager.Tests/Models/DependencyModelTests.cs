using System.Text.Json;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Tests.Models;

public class DependencyModelTests
{
    // Same naming policy the plugin index + manifests use (PluginRepoClient / ManifestParser).
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void IsGameInstaller_RoundTrips_CamelCase()
    {
        var dep = new Dependency { Id = "ptcgl-game", Type = "system", IsGameInstaller = true };

        var json = JsonSerializer.Serialize(dep, Camel);
        Assert.Contains("\"isGameInstaller\":true", json);

        var back = JsonSerializer.Deserialize<Dependency>(json, Camel)!;
        Assert.True(back.IsGameInstaller);
    }

    [Fact]
    public void IsGameInstaller_DefaultsFalse_WhenAbsent()
    {
        // An existing index that predates the flag must still deserialize, with the flag off.
        var back = JsonSerializer.Deserialize<Dependency>(
            "{\"id\":\"melonloader\",\"type\":\"framework\"}", Camel)!;

        Assert.False(back.IsGameInstaller);
    }

    [Fact]
    public void ExtractAppAutoInstall_RoundTrips_WithKindDiscriminator()
    {
        // The emulator's game-installer: kind "extractApp", SHA256 only.
        var dep = new Dependency
        {
            Id = "myemu",
            Type = "system",
            IsGameInstaller = true,
            Fix = new DependencyFix
            {
                DownloadUrl = "https://example.com/emu.zip",
                AutoInstall = new ExtractAppAutoInstall { Sha256 = "abc123" }
            }
        };

        var json = JsonSerializer.Serialize(dep, Camel);
        Assert.Contains("\"kind\":\"extractApp\"", json);

        var back = JsonSerializer.Deserialize<Dependency>(json, Camel)!;
        var auto = Assert.IsType<ExtractAppAutoInstall>(back.Fix!.AutoInstall);
        Assert.Equal("abc123", auto.Sha256);
    }

    [Fact]
    public void AppConfig_WithoutInstalledEmulators_DeserializesToEmpty()
    {
        // A config written before emulator support must still load, with an empty (not null) map.
        var back = JsonSerializer.Deserialize<AppConfig>("{\"defaultChannel\":\"stable\"}", Camel)!;

        Assert.NotNull(back.InstalledEmulators);
        Assert.Empty(back.InstalledEmulators);
    }
}
