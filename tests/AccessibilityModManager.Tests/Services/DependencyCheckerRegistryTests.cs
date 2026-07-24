using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;
using Microsoft.Win32;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// Audit finding 35: dependency registry checks were HKLM-only and read a single registry view.
/// They now honor an optional RegistryHive ("HKLM" default, "HKCU") and probe both the 64-bit and
/// 32-bit (WOW6432Node) views, best status winning. HKCU is used here because tests can write it
/// without elevation.
/// </summary>
public class DependencyCheckerRegistryTests : IDisposable
{
    private readonly string _keyName = @"Software\AMMTest_" + Guid.NewGuid().ToString("N");
    private readonly string _tempRoot;

    public DependencyCheckerRegistryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_depreg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        using var key = Registry.CurrentUser.CreateSubKey(_keyName);
        key.SetValue("Version", "2.5.0");
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_keyName, throwOnMissingSubKey: false); } catch { }
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private async Task<DependencyStatus> CheckAsync(Dependency dep)
    {
        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "game-1",
                DisplayName = "Game 1",
                Dependencies = new List<Dependency> { dep }
            },
            PluginId = "plug-a",
            InstallPath = _tempRoot,
            IsValid = true
        };
        var checker = new DependencyChecker(TestLogger.Create());
        return Assert.Single(await checker.CheckAsync(game));
    }

    private Dependency HkcuDep(string? minVersion = null, string? hive = "HKCU", string? valueName = "Version",
        string? view = null) => new()
    {
        Id = "dep-1",
        Type = "system",
        MinVersion = minVersion,
        Check = new DependencyCheck
        {
            RegistryKey = _keyName,
            RegistryValue = valueName,
            RegistryHive = hive,
            RegistryView = view
        }
    };

    [Fact]
    public async Task HkcuCheck_KeyPresent_Installed()
    {
        var status = await CheckAsync(HkcuDep());
        Assert.Equal(DependencyStatusKind.Installed, status.Status);
    }

    [Fact]
    public async Task HkcuCheck_MinVersionSatisfied_Installed()
    {
        var status = await CheckAsync(HkcuDep(minVersion: "2.0.0"));
        Assert.Equal(DependencyStatusKind.Installed, status.Status);
    }

    [Fact]
    public async Task HkcuCheck_VersionTooOld_Incompatible()
    {
        var status = await CheckAsync(HkcuDep(minVersion: "3.0.0"));
        Assert.Equal(DependencyStatusKind.Incompatible, status.Status);
        Assert.Contains("2.5.0", status.Details);
    }

    [Fact]
    public async Task DefaultHive_IsHklm_SoHkcuOnlyKeyIsMissing()
    {
        // Same key path, but hive left empty -> HKLM (old behavior preserved); the key only
        // exists under HKCU, so the check must NOT find it.
        var status = await CheckAsync(HkcuDep(hive: null));
        Assert.Equal(DependencyStatusKind.Missing, status.Status);
    }

    [Fact]
    public async Task UnknownHive_MissingWithClearMessage()
    {
        var status = await CheckAsync(HkcuDep(hive: "HKCR"));
        Assert.Equal(DependencyStatusKind.Missing, status.Status);
        Assert.Contains("Unknown registry hive", status.Details);
    }

    [Fact]
    public async Task MissingValue_Missing()
    {
        var status = await CheckAsync(HkcuDep(valueName: "NoSuchValue"));
        Assert.Equal(DependencyStatusKind.Missing, status.Status);
    }

    [Theory]
    [InlineData("64")]
    [InlineData("32")]
    [InlineData("both")]
    [InlineData("BOTH")]
    public async Task PinnedOrExplicitView_StillFindsTheKey(string view)
    {
        // End-to-end wiring smoke only: HKCU is shared between views, so a pinned view still
        // resolves here. EXCLUSION — the pin's actual point — is proven on ParseViews below,
        // since a real-hive exclusion test needs HKLM writes (elevation).
        var status = await CheckAsync(HkcuDep(view: view));
        Assert.Equal(DependencyStatusKind.Installed, status.Status);
    }

    [Fact]
    public void ParseViews_PinsExcludeTheOtherView()
    {
        // The masking defect the pin exists to fix: "64" must select ONLY the 64-bit view, so a
        // 32-bit entry can never satisfy a 64-bit requirement — and the reverse.
        Assert.Equal(new[] { Microsoft.Win32.RegistryView.Registry64 },
            DependencyChecker.ParseViews("64"));
        Assert.Equal(new[] { Microsoft.Win32.RegistryView.Registry64 },
            DependencyChecker.ParseViews("x64"));
        Assert.Equal(new[] { Microsoft.Win32.RegistryView.Registry32 },
            DependencyChecker.ParseViews("32"));
        Assert.Equal(new[] { Microsoft.Win32.RegistryView.Registry32 },
            DependencyChecker.ParseViews("X86"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("both")]
    [InlineData("Both")]
    public void ParseViews_DefaultAndBoth_Check64Then32(string? view)
    {
        Assert.Equal(
            new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 },
            DependencyChecker.ParseViews(view));
    }

    [Fact]
    public void ParseViews_UnknownValue_Null()
    {
        Assert.Null(DependencyChecker.ParseViews("ARM"));
    }

    [Fact]
    public async Task UnknownView_MissingWithClearMessage()
    {
        var status = await CheckAsync(HkcuDep(view: "ARM"));
        Assert.Equal(DependencyStatusKind.Missing, status.Status);
        Assert.Contains("Unknown registry view", status.Details);
    }

    [Fact]
    public async Task LegacyHivePrefixInsideKey_IsRecognized()
    {
        // Authors sometimes write the hive into the key itself (the repo's .NET preset does).
        // The old code opened that whole string relative to HKLM, which can never exist.
        var dep = new Dependency
        {
            Id = "dep-1",
            Type = "system",
            Check = new DependencyCheck
            {
                RegistryKey = @"HKEY_CURRENT_USER\" + _keyName,
                RegistryValue = "Version"
            }
        };
        var status = await CheckAsync(dep);
        Assert.Equal(DependencyStatusKind.Installed, status.Status);
    }

    [Fact]
    public async Task HivePrefixContradictingExplicitHive_MissingWithClearMessage()
    {
        var dep = new Dependency
        {
            Id = "dep-1",
            Type = "system",
            Check = new DependencyCheck
            {
                RegistryKey = @"HKEY_CURRENT_USER\" + _keyName,
                RegistryValue = "Version",
                RegistryHive = "HKLM"
            }
        };
        var status = await CheckAsync(dep);
        Assert.Equal(DependencyStatusKind.Missing, status.Status);
        Assert.Contains("contradicts", status.Details);
    }
}
