using System.Diagnostics;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using Microsoft.Win32;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Checks dependencies declared on a game definition in the plugin's repo index.
/// Supports two dependency types:
///   - "system": checked via Windows registry keys (e.g., VC++ redistributables, .NET Desktop Runtime)
///   - "framework": checked via file/folder presence relative to the game install path (e.g., BepInEx)
/// </summary>
public sealed class DependencyChecker : IDependencyChecker
{
    private readonly ILogger _logger;

    public DependencyChecker(ILogger logger)
    {
        _logger = logger;
    }

    public Task<List<DependencyStatus>> CheckAsync(GameInstall game, CancellationToken ct = default)
    {
        var dependencies = game.Game.Dependencies;
        _logger.Information("Checking {Count} dependencies for {GameId}",
            dependencies.Count, game.Game.GameId);

        var results = new List<DependencyStatus>();

        foreach (var dep in dependencies)
        {
            ct.ThrowIfCancellationRequested();

            var status = dep.Type switch
            {
                "system" => CheckSystemDependency(dep),
                "framework" => CheckFrameworkDependency(dep, game.InstallPath),
                _ => new DependencyStatus
                {
                    Dependency = dep,
                    Status = DependencyStatusKind.Missing,
                    Details = $"Unknown dependency type: {dep.Type}"
                }
            };

            _logger.Debug("Dependency {DepId} ({Type}): {Status}", dep.Id, dep.Type, status.Status);
            results.Add(status);
        }

        return Task.FromResult(results);
    }

    public Task FixAsync(Dependency dep, CancellationToken ct = default)
    {
        _logger.Information("Fixing dependency {DepId}", dep.Id);

        if (!string.IsNullOrEmpty(dep.Fix?.DownloadUrl))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dep.Fix.DownloadUrl,
                UseShellExecute = true
            });
            _logger.Information("Opened download URL for {DepId}: {Url}", dep.Id, dep.Fix.DownloadUrl);
        }
        else
        {
            _logger.Warning("No fix URL available for dependency {DepId}", dep.Id);
        }

        return Task.CompletedTask;
    }

    private DependencyStatus CheckSystemDependency(Dependency dep)
    {
        if (dep.Check == null)
        {
            return new DependencyStatus
            {
                Dependency = dep,
                Status = DependencyStatusKind.Missing,
                Details = "No check rule defined"
            };
        }

        // Registry-based check
        if (!string.IsNullOrEmpty(dep.Check.RegistryKey))
        {
            return CheckRegistry(dep);
        }

        // File-based check (absolute path for system deps)
        if (!string.IsNullOrEmpty(dep.Check.FilePath))
        {
            return CheckAbsoluteFile(dep);
        }

        return new DependencyStatus
        {
            Dependency = dep,
            Status = DependencyStatusKind.Missing,
            Details = "No registry key or file path to check"
        };
    }

    private DependencyStatus CheckFrameworkDependency(Dependency dep, string gameInstallPath)
    {
        if (dep.Check == null)
        {
            return new DependencyStatus
            {
                Dependency = dep,
                Status = DependencyStatusKind.Missing,
                Details = "No check rule defined"
            };
        }

        // File/folder presence relative to game install
        if (!string.IsNullOrEmpty(dep.Check.FilePath))
        {
            var fullPath = Path.GetFullPath(Path.Combine(gameInstallPath, dep.Check.FilePath));

            // Path traversal protection
            if (!fullPath.StartsWith(gameInstallPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("Dependency check path escapes game directory: {Path}", dep.Check.FilePath);
                return new DependencyStatus
                {
                    Dependency = dep,
                    Status = DependencyStatusKind.Missing,
                    Details = "Invalid check path"
                };
            }

            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                return new DependencyStatus
                {
                    Dependency = dep,
                    Status = DependencyStatusKind.Installed
                };
            }

            return new DependencyStatus
            {
                Dependency = dep,
                Status = DependencyStatusKind.Missing,
                Details = $"Not found: {dep.Check.FilePath}"
            };
        }

        // Registry fallback for framework deps
        if (!string.IsNullOrEmpty(dep.Check.RegistryKey))
        {
            return CheckRegistry(dep);
        }

        return new DependencyStatus
        {
            Dependency = dep,
            Status = DependencyStatusKind.Missing,
            Details = "No file path or registry key to check"
        };
    }

    private DependencyStatus CheckRegistry(Dependency dep)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(dep.Check!.RegistryKey!);
            if (key == null)
            {
                return new DependencyStatus
                {
                    Dependency = dep,
                    Status = DependencyStatusKind.Missing,
                    Details = "Registry key not found"
                };
            }

            // If a specific value name is specified, check it
            if (!string.IsNullOrEmpty(dep.Check.RegistryValue))
            {
                var value = key.GetValue(dep.Check.RegistryValue);
                if (value == null)
                {
                    return new DependencyStatus
                    {
                        Dependency = dep,
                        Status = DependencyStatusKind.Missing,
                        Details = $"Registry value '{dep.Check.RegistryValue}' not found"
                    };
                }

                // Version comparison if MinVersion is specified
                if (!string.IsNullOrEmpty(dep.MinVersion))
                {
                    var installedStr = value.ToString();
                    if (Version.TryParse(installedStr, out var installed) &&
                        Version.TryParse(dep.MinVersion, out var minVersion))
                    {
                        if (installed < minVersion)
                        {
                            return new DependencyStatus
                            {
                                Dependency = dep,
                                Status = DependencyStatusKind.Incompatible,
                                Details = $"Installed: {installed}, required: >= {minVersion}"
                            };
                        }
                    }
                }
            }

            return new DependencyStatus
            {
                Dependency = dep,
                Status = DependencyStatusKind.Installed
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to check registry for dependency {DepId}", dep.Id);
            return new DependencyStatus
            {
                Dependency = dep,
                Status = DependencyStatusKind.Missing,
                Details = $"Registry check failed: {ex.Message}"
            };
        }
    }

    private DependencyStatus CheckAbsoluteFile(Dependency dep)
    {
        var path = dep.Check!.FilePath!;

        if (File.Exists(path) || Directory.Exists(path))
        {
            return new DependencyStatus
            {
                Dependency = dep,
                Status = DependencyStatusKind.Installed
            };
        }

        return new DependencyStatus
        {
            Dependency = dep,
            Status = DependencyStatusKind.Missing,
            Details = $"Not found: {path}"
        };
    }
}
