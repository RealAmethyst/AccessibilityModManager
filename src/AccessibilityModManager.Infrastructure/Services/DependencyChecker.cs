using System.Diagnostics;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
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

            _logger.Information("Dependency {DepId} ({Type}, check={CheckPath}): {Status} ({Details})",
                dep.Id, dep.Type,
                dep.Check?.FilePath ?? dep.Check?.RegistryKey ?? "(none)",
                status.Status, status.Details ?? "ok");
            results.Add(status);
        }

        return Task.FromResult(results);
    }

    public Task<bool> FixAsync(Dependency dep, CancellationToken ct = default)
    {
        _logger.Information("Fixing dependency {DepId}", dep.Id);

        if (string.IsNullOrEmpty(dep.Fix?.DownloadUrl))
        {
            _logger.Warning("No fix URL available for dependency {DepId}", dep.Id);
            return Task.FromResult(false);
        }

        // The URL comes from the unsigned plugin index — same rule as every other author link:
        // only ever hand https URLs to the shell, never file:/custom-scheme values that could
        // trigger local actions (the one spot the 1.12.1 link sweep missed).
        var opened = ExternalLink.TryOpen(dep.Fix.DownloadUrl, _logger);
        return Task.FromResult(opened);
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
            var normalizedBase = Path.GetFullPath(gameInstallPath);
            var fullPath = Path.GetFullPath(Path.Combine(normalizedBase, dep.Check.FilePath));

            // Path traversal protection — PathSafety canonicalizes both sides (separators, case,
            // trailing separators) so a VDF/folder-picker style base can't foil the check.
            if (!PathSafety.IsContained(normalizedBase, fullPath))
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

                // Version comparison if MinVersion is specified.
                // Uses VersionComparer (SemVer-leaning) so BepInEx/MelonLoader-style versions
                // like "5.4.21" or "6.0.0-pre.1" compare correctly, plus dotted runtime versions
                // like "10.0.1234.0".
                if (!string.IsNullOrEmpty(dep.MinVersion))
                {
                    var installedStr = value.ToString();
                    if (LooksLikeVersion(installedStr) && LooksLikeVersion(dep.MinVersion) &&
                        VersionComparer.Instance.Compare(installedStr, dep.MinVersion) < 0)
                    {
                        return new DependencyStatus
                        {
                            Dependency = dep,
                            Status = DependencyStatusKind.Incompatible,
                            Details = $"Installed: {installedStr}, required: >= {dep.MinVersion}"
                        };
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

    /// <summary>
    /// Conservative "is this string a version?" check — first character is a digit, rest is
    /// digits/letters/dots/dashes. Avoids comparing "Some Display Name" as a version, which would
    /// give nonsense ordering.
    /// </summary>
    private static bool LooksLikeVersion(string? s) =>
        !string.IsNullOrEmpty(s) &&
        char.IsDigit(s[0]) &&
        s.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-');

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
