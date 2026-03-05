using System.Text.Json;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Services;

public class ManifestParserTests
{
    private readonly ManifestParser _parser = new(TestLogger.Create());

    [Fact]
    public void Parse_ValidManifest_Succeeds()
    {
        var json = """
        {
            "gameId": "test-game",
            "pluginId": "test-plugin",
            "modVersion": "1.0.0",
            "installActions": [
                { "type": "copyFile", "source": "mod.dll", "target": "mods/mod.dll" }
            ],
            "verify": [
                { "type": "fileExists", "path": "mods/mod.dll" }
            ]
        }
        """;

        var manifest = _parser.Parse(json);

        Assert.Equal("test-game", manifest.GameId);
        Assert.Equal("test-plugin", manifest.PluginId);
        Assert.Equal("1.0.0", manifest.ModVersion);
        Assert.Single(manifest.InstallActions);
        Assert.Single(manifest.Verify);
    }

    [Fact]
    public void Parse_MissingGameId_Throws()
    {
        var json = """
        {
            "pluginId": "test-plugin",
            "modVersion": "1.0.0"
        }
        """;

        Assert.ThrowsAny<Exception>(() => _parser.Parse(json));
    }

    [Fact]
    public void Parse_MissingPluginId_Throws()
    {
        var json = """
        {
            "gameId": "test-game",
            "modVersion": "1.0.0"
        }
        """;

        Assert.ThrowsAny<Exception>(() => _parser.Parse(json));
    }

    [Fact]
    public void Parse_MissingModVersion_Throws()
    {
        var json = """
        {
            "gameId": "test-game",
            "pluginId": "test-plugin"
        }
        """;

        Assert.ThrowsAny<Exception>(() => _parser.Parse(json));
    }

    [Fact]
    public void Parse_UnknownVerifyType_Throws()
    {
        var json = """
        {
            "gameId": "test-game",
            "pluginId": "test-plugin",
            "modVersion": "1.0.0",
            "verify": [
                { "type": "runExe", "path": "evil.exe" }
            ]
        }
        """;

        Assert.Throws<InvalidOperationException>(() => _parser.Parse(json));
    }

    [Fact]
    public void Parse_AllActionTypes_Succeeds()
    {
        var json = """
        {
            "gameId": "test-game",
            "pluginId": "test-plugin",
            "modVersion": "1.0.0",
            "installActions": [
                { "type": "copyFile", "source": "a.dll", "target": "a.dll" },
                { "type": "copyFolder", "sourceDir": "data", "targetDir": "data" },
                { "type": "replaceFile", "source": "b.cfg", "target": "b.cfg", "backup": true }
            ]
        }
        """;

        var manifest = _parser.Parse(json);
        Assert.Equal(3, manifest.InstallActions.Count);
    }
}
