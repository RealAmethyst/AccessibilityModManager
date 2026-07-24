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

        string? raw;
        try
        {
            raw = ReadRegistryValue(probe);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Registry probe failed for {Game} ({Hive}\\{Key}\\{Value})",
                game.DisplayName, probe.Hive, probe.Key, probe.Value);
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.Debug("Registry probe for {Game}: value '{Value}' missing or empty", game.DisplayName, probe.Value);
            return null;
        }

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

    private static string? ReadRegistryValue(RegistryProbe probe)
    {
        var root = OpenHive(probe.Hive);
        if (root is null) return null;

        // Predefined hive keys (Registry.CurrentUser etc.) are process-wide singletons and must
        // not be disposed — only dispose the subkey we open.
        using var key = root.OpenSubKey(probe.Key);
        return key?.GetValue(probe.Value) as string;
    }

    private static RegistryKey? OpenHive(string hive) => hive?.Trim().ToUpperInvariant() switch
    {
        "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
        "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
        _ => null
    };
}
