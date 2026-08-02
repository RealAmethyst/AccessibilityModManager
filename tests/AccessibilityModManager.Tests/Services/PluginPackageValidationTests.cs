using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// The AuthorTool's pre-publish package check (audit finding 38). Every case here is a package
/// the manager would reject at install time — the point of the check is that the author finds
/// out before the ZIP and its hash are published, not after a user downloads it.
/// </summary>
public class PluginPackageValidationTests
{
    private const string PluginId = "amethyst";
    private const string GameId = "digimonsurvive";
    private const string Version = "1.2.0";

    [Fact]
    public void Validate_WellFormedPackage_IsValid()
    {
        using var zip = BuildPackage();

        var report = Validate(zip);

        Assert.True(report.IsValid, string.Join(" | ", report.Errors));
    }

    [Fact]
    public void Validate_NotAZip_ReportsUnreadableArchive()
    {
        using var garbage = new MemoryStream("this is not a zip"u8.ToArray());

        var report = Validate(garbage);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("readable ZIP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_NoManifest_SaysToBuildThePackage()
    {
        using var zip = BuildZip(archive => AddEntry(archive, "files/mod.dll", "bytes"));

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("no manifest.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ManifestNestedInAFolder_SaysWhereItIs()
    {
        // The classic hand-zipped mistake: the whole build folder got zipped, so every path
        // gained a prefix and the manager finds nothing at the root.
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "MyMod/manifest.json", ManifestJson());
            AddEntry(archive, "MyMod/files/mod.dll", "bytes");
        });

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("MyMod/manifest.json") && e.Contains("root"));
    }

    [Fact]
    public void Validate_UnparseableManifest_ReportsTheParserMessage()
    {
        using var zip = BuildZip(archive => AddEntry(archive, "manifest.json", "{ not json"));

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("doesn't parse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_DisallowedActionType_IsRejected()
    {
        // No code execution outside the consented script system: the manager's parser refuses
        // unknown action types, so the author must not be able to publish one.
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson(
                actions: [new { type = "runCommand", source = "files/evil.exe", target = "evil.exe" }]));
            AddEntry(archive, "files/evil.exe", "bytes");
        });

        var report = Validate(zip);

        Assert.False(report.IsValid);
    }

    [Theory]
    [InlineData("wrongplugin", GameId, Version)]
    [InlineData(PluginId, "wronggame", Version)]
    [InlineData(PluginId, GameId, "1.1.0")]
    public void Validate_IdentityMismatch_IsRejected(string manifestPlugin, string manifestGame, string manifestVersion)
    {
        using var zip = BuildPackage(pluginId: manifestPlugin, gameId: manifestGame, version: manifestVersion);

        var report = Validate(zip);

        Assert.False(report.IsValid);
    }

    [Fact]
    public void Validate_VersionMismatch_NamesBothVersions()
    {
        // The wrong-ZIP-for-this-release case: it would download, hash-verify, and only then
        // fail on the user's machine.
        using var zip = BuildPackage(version: "1.1.0");

        var report = Validate(zip);

        Assert.Contains(report.Errors, e => e.Contains("1.1.0") && e.Contains(Version));
    }

    [Fact]
    public void Validate_VersionDifferingOnlyByWhitespace_IsAccepted()
    {
        using var zip = BuildPackage(version: " 1.2.0 ");

        var report = Validate(zip);

        Assert.True(report.IsValid, string.Join(" | ", report.Errors));
    }

    [Fact]
    public void Validate_ActionSourceMissingFromZip_IsRejected()
    {
        // Would fail mid-install, after the backup is taken.
        using var zip = BuildZip(archive =>
            AddEntry(archive, "manifest.json", ManifestJson(
                actions: [new { type = "copyFile", source = "mod.dll", target = "mod.dll" }])));

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("mod.dll") && e.Contains("no such file"));
    }

    [Fact]
    public void Validate_CopyFolderSourceMissingFromZip_IsRejected()
    {
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson(
                actions: [new { type = "copyFolder", sourceDir = "plugins", targetDir = "plugins" }]));
            AddEntry(archive, "files/other/thing.dll", "bytes");
        });

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("plugins"));
    }

    [Fact]
    public void Validate_CopyFolderWithContents_IsAccepted()
    {
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson(
                actions: [new { type = "copyFolder", sourceDir = "plugins", targetDir = "plugins" }]));
            AddEntry(archive, "files/plugins/thing.dll", "bytes");
        });

        var report = Validate(zip);

        Assert.True(report.IsValid, string.Join(" | ", report.Errors));
    }

    [Fact]
    public void Validate_BackslashSourceMatchesForwardSlashEntry()
    {
        // Authors on Windows write either separator; the manager resolves both against the
        // extracted folder, so neither may be reported as missing.
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson(
                actions: [new { type = "copyFile", source = @"sub\mod.dll", target = "mod.dll" }]));
            AddEntry(archive, "files/sub/mod.dll", "bytes");
        });

        var report = Validate(zip);

        Assert.True(report.IsValid, string.Join(" | ", report.Errors));
    }

    [Fact]
    public void Validate_ZipSlipEntry_IsRejected()
    {
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson());
            AddEntry(archive, "files/mod.dll", "bytes");
            AddEntry(archive, "../escape.dll", "bytes");
        });

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("unsafe entry path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_DeclaredScriptMissingFromZip_IsRejected()
    {
        using var zip = BuildZip(archive => AddEntry(archive, "manifest.json", ManifestJson(
            postInstall: new
            {
                executable = "setup.cmd",
                what = "Registers the mod",
                why = "The game needs it",
                modifies = "Nothing outside the game folder"
            })));

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("setup.cmd") && e.Contains("isn't in the ZIP"));
    }

    [Fact]
    public void Validate_ScriptWithDisallowedExtension_IsRejected()
    {
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson(
                postInstall: new
                {
                    executable = "setup.vbs",
                    what = "Registers the mod",
                    why = "The game needs it",
                    modifies = "Nothing outside the game folder"
                }));
            AddEntry(archive, "setup.vbs", "payload");
        });

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains(".vbs"));
    }

    [Fact]
    public void Validate_ScriptPresentAndAllowed_IsAccepted()
    {
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson(
                postInstall: new
                {
                    executable = "setup.cmd",
                    what = "Registers the mod",
                    why = "The game needs it",
                    modifies = "Nothing outside the game folder"
                }));
            AddEntry(archive, "setup.cmd", "@echo off");
            AddEntry(archive, "files/mod.dll", "bytes");
        });

        var report = Validate(zip);

        Assert.True(report.IsValid, string.Join(" | ", report.Errors));
    }

    [Fact]
    public void Validate_ActionTargetEscapingTheGameFolder_IsRejected()
    {
        // The manager aborts on this — but only once the install is under way and the backup
        // has been taken. The author should never be able to ship it.
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson(
                actions: [new { type = "copyFile", source = "mod.dll", target = "../../escape.dll" }]));
            AddEntry(archive, "files/mod.dll", "bytes");
        });

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("outside the game folder"));
    }

    [Fact]
    public void Validate_AbsoluteActionTarget_IsRejected()
    {
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson(
                actions: [new { type = "copyFile", source = "mod.dll", target = @"C:\Windows\System32\evil.dll" }]));
            AddEntry(archive, "files/mod.dll", "bytes");
        });

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("outside the game folder"));
    }

    [Fact]
    public void Validate_DuplicateManifestEntries_IsRejected()
    {
        // Extraction writes entries in order, so the LAST manifest is what the installer reads,
        // while a reader picks the first — the checked file and the installed file differ.
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson());
            AddEntry(archive, "files/mod.dll", "bytes");
            AddEntry(archive, "manifest.json", ManifestJson(pluginId: "someone-else"));
        });

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("more than once"));
    }

    [Fact]
    public void Validate_HashEqualsRuleWithoutHash_IsRejected()
    {
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson(
                verify: [new { type = "hashEquals", path = "mod.dll" }]));
            AddEntry(archive, "files/mod.dll", "bytes");
        });

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("no sha256"));
    }

    [Fact]
    public void Validate_VerifyRuleEscapingTheGameFolder_IsRejected()
    {
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson(
                verify: [new { type = "fileExists", path = "../../elsewhere.dll" }]));
            AddEntry(archive, "files/mod.dll", "bytes");
        });

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("isn't a path inside"));
    }

    [Fact]
    public void Validate_ScriptWithBlankConsentText_IsRejected()
    {
        // Those three lines are the whole basis on which a user agrees to run author code.
        using var zip = BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson(
                postInstall: new { executable = "setup.cmd", what = "", why = "  ", modifies = "" }));
            AddEntry(archive, "setup.cmd", "@echo off");
            AddEntry(archive, "files/mod.dll", "bytes");
        });

        var report = Validate(zip);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Contains("what it does"));
    }

    [Fact]
    public void Validate_LeavesTheStreamOpenAndRewindable()
    {
        // The caller hashes and uploads from this same handle after validating — closing it or
        // leaving it mid-stream would break the publish.
        using var zip = BuildPackage();

        PluginPackageValidation.Validate(zip, PluginId, GameId, Version, TestLogger.Create());

        Assert.True(zip.CanRead);
        zip.Position = 0;
        Assert.Equal(0, zip.Position);
    }

    private static PackageValidationReport Validate(Stream zip) =>
        PluginPackageValidation.Validate(zip, PluginId, GameId, Version, TestLogger.Create());

    private static MemoryStream BuildPackage(
        string pluginId = PluginId, string gameId = GameId, string version = Version) =>
        BuildZip(archive =>
        {
            AddEntry(archive, "manifest.json", ManifestJson(pluginId, gameId, version));
            AddEntry(archive, "files/mod.dll", "bytes");
        });

    private static MemoryStream BuildZip(Action<ZipArchive> fill)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            fill(archive);
        stream.Position = 0;
        return stream;
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static string ManifestJson(
        string pluginId = PluginId,
        string gameId = GameId,
        string version = Version,
        object[]? actions = null,
        object? postInstall = null,
        object[]? verify = null)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["pluginId"] = pluginId,
            ["gameId"] = gameId,
            ["modVersion"] = version,
            ["installActions"] = actions ??
                [new { type = "copyFile", source = "mod.dll", target = "mod.dll" }],
            ["verify"] = verify ?? Array.Empty<object>()
        };
        if (postInstall != null) manifest["postInstall"] = postInstall;
        return JsonSerializer.Serialize(manifest);
    }
}
