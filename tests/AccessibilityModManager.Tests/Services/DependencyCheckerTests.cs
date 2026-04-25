using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Services;

public class DependencyCheckerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DependencyChecker _checker;

    public DependencyCheckerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ammtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _checker = new DependencyChecker(TestLogger.Create());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task CheckAsync_FrameworkDep_FileExists_ReturnsInstalled()
    {
        File.WriteAllText(Path.Combine(_tempDir, "BepInEx.dll"), "framework");
        var game = MakeGameInstall(new Dependency
        {
            Id = "bepinex",
            Type = "framework",
            Check = new DependencyCheck { FilePath = "BepInEx.dll" }
        });

        var results = await _checker.CheckAsync(game);

        Assert.Single(results);
        Assert.Equal(DependencyStatusKind.Installed, results[0].Status);
    }

    [Fact]
    public async Task CheckAsync_FrameworkDep_ForwardSlashGameInstallPath_StillDetectsExistingFile()
    {
        // Regression: a gameInstallPath with forward slashes (from VDF parsing or a folder
        // picker) used to fail the path-traversal check spuriously, returning Missing even
        // when the dependency was present.
        var melonLoaderDir = Path.Combine(_tempDir, "MelonLoader");
        Directory.CreateDirectory(melonLoaderDir);

        // Use forward slashes deliberately. The dep checker must normalize before comparing.
        var forwardSlashPath = _tempDir.Replace('\\', '/');

        var game = new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "test-game",
                DisplayName = "Test Game",
                Dependencies = new List<Dependency>
                {
                    new()
                    {
                        Id = "melonloader",
                        Type = "framework",
                        Check = new DependencyCheck { FilePath = "MelonLoader" }
                    }
                }
            },
            PluginId = "test-plugin",
            InstallPath = forwardSlashPath,
            IsValid = true
        };

        var results = await _checker.CheckAsync(game);

        Assert.Single(results);
        Assert.Equal(DependencyStatusKind.Installed, results[0].Status);
    }

    [Fact]
    public async Task CheckAsync_FrameworkDep_FileMissing_ReturnsMissing()
    {
        var game = MakeGameInstall(new Dependency
        {
            Id = "bepinex",
            Type = "framework",
            Check = new DependencyCheck { FilePath = "BepInEx.dll" }
        });

        var results = await _checker.CheckAsync(game);

        Assert.Single(results);
        Assert.Equal(DependencyStatusKind.Missing, results[0].Status);
    }

    [Fact]
    public async Task CheckAsync_FrameworkDep_PathTraversal_ReturnsMissing()
    {
        var game = MakeGameInstall(new Dependency
        {
            Id = "evil",
            Type = "framework",
            Check = new DependencyCheck { FilePath = "../../etc/passwd" }
        });

        var results = await _checker.CheckAsync(game);

        Assert.Single(results);
        Assert.Equal(DependencyStatusKind.Missing, results[0].Status);
        Assert.Contains("Invalid", results[0].Details);
    }

    [Fact]
    public async Task CheckAsync_SystemDep_RegistryKey_Exists()
    {
        // Use a registry key known to exist on all Windows machines
        var game = MakeGameInstall(new Dependency
        {
            Id = "windows",
            Type = "system",
            Check = new DependencyCheck
            {
                RegistryKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                RegistryValue = "ProductName"
            }
        });

        var results = await _checker.CheckAsync(game);

        Assert.Single(results);
        Assert.Equal(DependencyStatusKind.Installed, results[0].Status);
    }

    [Fact]
    public async Task CheckAsync_SystemDep_RegistryKey_Missing()
    {
        var game = MakeGameInstall(new Dependency
        {
            Id = "fake",
            Type = "system",
            Check = new DependencyCheck
            {
                RegistryKey = @"SOFTWARE\NonExistent\FakeApp\12345"
            }
        });

        var results = await _checker.CheckAsync(game);

        Assert.Single(results);
        Assert.Equal(DependencyStatusKind.Missing, results[0].Status);
    }

    [Fact]
    public async Task CheckAsync_SystemDep_VersionTooLow_ReturnsIncompatible()
    {
        // Use a registry value we know exists with a string value, with an absurdly high MinVersion.
        var game = MakeGameInstall(new Dependency
        {
            Id = "windows-version",
            Type = "system",
            MinVersion = "999.0.0",
            Check = new DependencyCheck
            {
                RegistryKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                RegistryValue = "CurrentVersion"
            }
        });

        var results = await _checker.CheckAsync(game);

        Assert.Single(results);
        // CurrentVersion is something like "6.3" — definitely less than 999.0.0.
        Assert.Equal(DependencyStatusKind.Incompatible, results[0].Status);
    }

    [Fact]
    public async Task CheckAsync_SystemDep_VersionMet_ReturnsInstalled()
    {
        var game = MakeGameInstall(new Dependency
        {
            Id = "windows-version",
            Type = "system",
            MinVersion = "1.0",
            Check = new DependencyCheck
            {
                RegistryKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                RegistryValue = "CurrentVersion"
            }
        });

        var results = await _checker.CheckAsync(game);

        Assert.Single(results);
        Assert.Equal(DependencyStatusKind.Installed, results[0].Status);
    }

    [Fact]
    public async Task CheckAsync_NoDeps_ReturnsEmpty()
    {
        var game = MakeGameInstall();
        var results = await _checker.CheckAsync(game);
        Assert.Empty(results);
    }

    private GameInstall MakeGameInstall(params Dependency[] deps)
    {
        return new GameInstall
        {
            Game = new GameDefinition
            {
                GameId = "test-game",
                DisplayName = "Test Game",
                Dependencies = deps.ToList()
            },
            PluginId = "test-plugin",
            InstallPath = _tempDir,
            IsValid = true
        };
    }
}
