using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using Microsoft.Win32;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Detection;

/// <summary>
/// Resolves a non-Steam game's install path from a Windows registry value. The registry value
/// may be the game folder itself or a parent/publisher folder; in the latter case (and when
/// <see cref="RegistryProbe.ProbeSubfolders"/> is set) the immediate child directories are
/// probed for the one that actually verifies. See PTCGL_INSTALL_QUESTIONS.md §6.
/// </summary>
public sealed class RegistryGameDetector : IRegistryGameDetector
{
    private readonly IGameVerifier _verifier;
    private readonly ILogger _logger;

    public RegistryGameDetector(IGameVerifier verifier, ILogger logger)
    {
        _verifier = verifier;
        _logger = logger;
    }

    public string? ResolveInstallPath(GameDefinition game)
    {
        var probe = game.RegistryProbe;
        if (probe is null) return null;

        // A 32-bit installer's key lands in the WOW6432Node (32-bit) view, invisible from the
        // default 64-bit view unless the author spells the WOW path out — a Windows quirk authors
        // shouldn't need to know exists (audit finding 35). Both views are read; each distinct
        // value gets the full resolve attempt, 64-bit first.
        foreach (var raw in ReadRegistryValueCandidates(game, probe))
        {
            var resolved = TryResolveFrom(game, probe, raw);
            if (resolved is not null) return resolved;
        }

        _logger.Debug("Registry probe for {Game}: no candidate from either registry view verified",
            game.DisplayName);
        return null;
    }

    private string? TryResolveFrom(GameDefinition game, RegistryProbe probe, string raw)
    {
        // Normalize: strip surrounding quotes and trailing separators so child enumeration and
        // verification behave consistently.
        var basePath = raw.Trim().Trim('"').TrimEnd('\\', '/');

        // 1) The value might point straight at the game folder.
        if (_verifier.VerifyInstallPath(game, basePath))
        {
            _logger.Information("Detected {Game} via registry at {Path}", game.DisplayName, basePath);
            return basePath;
        }

        // 2) Or at a parent/publisher folder — probe immediate children for the game folder.
        if (probe.ProbeSubfolders && Directory.Exists(basePath))
        {
            try
            {
                foreach (var child in Directory.EnumerateDirectories(basePath))
                {
                    if (_verifier.VerifyInstallPath(game, child))
                    {
                        _logger.Information("Detected {Game} via registry subfolder probe at {Path}", game.DisplayName, child);
                        return child;
                    }
                }
            }
            catch (Exception ex)
            {
                // A broken/hostile path (ACL denial, path-too-long, bad reparse point, transient IO)
                // must only make THIS game undetected, never bubble up and fail the whole catalog
                // refresh for every other game.
                _logger.Debug(ex, "Subfolder probe under '{Base}' failed for {Game}", basePath, game.DisplayName);
                return null;
            }
        }

        _logger.Debug("Registry probe for {Game}: '{Base}' did not verify and no child matched", game.DisplayName, basePath);
        return null;
    }

    /// <summary>
    /// Reads the probe's value from the 64-bit view, then the 32-bit (WOW6432Node) view,
    /// returning each distinct non-empty string once, in that order. A failure in one view only
    /// skips that view. On 32-bit Windows both views are the same store; dedupe collapses them.
    /// </summary>
    private List<string> ReadRegistryValueCandidates(GameDefinition game, RegistryProbe probe)
    {
        var candidates = new List<string>();

        var hive = HiveOf(probe.Hive);
        if (hive is null) return candidates;

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive.Value, view);
                using var key = root.OpenSubKey(probe.Key);
                if (key?.GetValue(probe.Value) is string value &&
                    !string.IsNullOrWhiteSpace(value) &&
                    !candidates.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(value);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Registry probe failed for {Game} ({Hive}\\{Key}\\{Value}, {View})",
                    game.DisplayName, probe.Hive, probe.Key, probe.Value, view);
            }
        }

        if (candidates.Count == 0)
            _logger.Debug("Registry probe for {Game}: value '{Value}' missing or empty in both views",
                game.DisplayName, probe.Value);

        return candidates;
    }

    private static RegistryHive? HiveOf(string hive) => hive?.Trim().ToUpperInvariant() switch
    {
        "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
        "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
        _ => null
    };
}
