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

    /// <summary>
    /// Registry check across BOTH registry views — 64-bit first, then the 32-bit WOW6432Node view
    /// a 32-bit installer writes to — and the hive the check names (HKLM default, HKCU supported,
    /// matching the game probes). Best status wins: present-and-good in either view is Installed;
    /// a version that's only too old is Incompatible; otherwise Missing (audit finding 35).
    /// </summary>
    private DependencyStatus CheckRegistry(Dependency dep)
    {
        // Authors sometimes write the hive INTO the key ("HKEY_LOCAL_MACHINE\SOFTWARE\...") —
        // the old code silently opened that whole string relative to HKLM, which can never
        // exist. Recognize the prefix instead of failing on it.
        var keyPath = dep.Check!.RegistryKey!;
        RegistryHive? prefixHive = null;
        var firstSep = keyPath.IndexOf('\\');
        if (firstSep > 0)
        {
            prefixHive = ParseHive(keyPath[..firstSep]);
            if (prefixHive is not null)
                keyPath = keyPath[(firstSep + 1)..];
        }

        var declaredHive = string.IsNullOrWhiteSpace(dep.Check.RegistryHive)
            ? (RegistryHive?)null
            : ParseHive(dep.Check.RegistryHive);
        if (!string.IsNullOrWhiteSpace(dep.Check.RegistryHive) && declaredHive is null)
        {
            return new DependencyStatus
            {
                Dependency = dep,
                Status = DependencyStatusKind.Missing,
                Details = $"Unknown registry hive '{dep.Check.RegistryHive}' (use HKLM or HKCU)"
            };
        }
        if (declaredHive is not null && prefixHive is not null && declaredHive != prefixHive)
        {
            return new DependencyStatus
            {
                Dependency = dep,
                Status = DependencyStatusKind.Missing,
                Details = $"Registry hive '{dep.Check.RegistryHive}' contradicts the key's own '{dep.Check.RegistryKey}' prefix"
            };
        }

        var hive = declaredHive ?? prefixHive ?? RegistryHive.LocalMachine;

        var views = ParseViews(dep.Check.RegistryView);
        if (views is null)
        {
            return new DependencyStatus
            {
                Dependency = dep,
                Status = DependencyStatusKind.Missing,
                Details = $"Unknown registry view '{dep.Check.RegistryView}' (use both, 64, or 32)"
            };
        }

        DependencyStatus? best = null;
        foreach (var view in views)
        {
            var status = CheckRegistryView(dep, hive, view, keyPath);
            if (status.Status == DependencyStatusKind.Installed)
                return status;
            if (best is null ||
                (status.Status == DependencyStatusKind.Incompatible &&
                 best.Status == DependencyStatusKind.Missing))
            {
                best = status;
            }
        }
        return best!;
    }

    private static RegistryHive? ParseHive(string value) => value.Trim().ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
        "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
        _ => null
    };

    /// <summary>
    /// The registry view(s) a check's optional architecture pin selects, in probe order. "both"
    /// (or absent) checks 64-bit then 32-bit; "64"/"x64" and "32"/"x86" check exactly that view —
    /// needed when a component installs per-architecture (with .NET, x86 and x64 runtimes
    /// coexist, and a mod needing the 64-bit one must not pass because the 32-bit view satisfied
    /// the version rule). Null for an unrecognized value — callers fail closed with a message.
    /// Public because the pin's whole point is EXCLUSION, and tests can't prove exclusion
    /// against a real hive without elevation (HKCU is shared between views) — they prove it here.
    /// </summary>
    public static RegistryView[]? ParseViews(string? registryView) =>
        registryView?.Trim().ToUpperInvariant() switch
        {
            null or "" or "BOTH" => new[] { RegistryView.Registry64, RegistryView.Registry32 },
            "64" or "X64" => new[] { RegistryView.Registry64 },
            "32" or "X86" => new[] { RegistryView.Registry32 },
            _ => null
        };

    private DependencyStatus CheckRegistryView(Dependency dep, RegistryHive hive, RegistryView view, string keyPath)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(keyPath);
            if (key == null)
            {
                return new DependencyStatus
                {
                    Dependency = dep,
                    Status = DependencyStatusKind.Missing,
                    Details = "Registry key not found"
                };
            }

            // No value name but a minimum version: the key's VALUE NAMES are the versions.
            // That's how .NET records installed runtimes — e.g. the x64 desktop runtime's key
            // (under the 32-bit view!) holds values literally named "10.0.8", "9.0.8", ... —
            // and it's why the old preset's version rule was dead (audit finding 10). The
            // highest version-shaped name decides.
            if (string.IsNullOrEmpty(dep.Check!.RegistryValue) && !string.IsNullOrEmpty(dep.MinVersion))
            {
                var best = key.GetValueNames()
                    .Where(LooksLikeVersion)
                    .OrderByDescending(v => v, VersionComparer.Instance)
                    .FirstOrDefault();
                if (best is null)
                {
                    return new DependencyStatus
                    {
                        Dependency = dep,
                        Status = DependencyStatusKind.Missing,
                        Details = "Key exists but holds no version entries"
                    };
                }
                if (VersionComparer.Instance.Compare(best, dep.MinVersion) < 0)
                {
                    return new DependencyStatus
                    {
                        Dependency = dep,
                        Status = DependencyStatusKind.Incompatible,
                        Details = $"Installed: {best}, required: >= {dep.MinVersion}"
                    };
                }
                return new DependencyStatus
                {
                    Dependency = dep,
                    Status = DependencyStatusKind.Installed
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
            _logger.Warning(ex, "Failed to check registry for dependency {DepId} ({View})", dep.Id, view);
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
