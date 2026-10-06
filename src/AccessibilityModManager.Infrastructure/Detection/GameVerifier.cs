using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Detection;

public sealed class GameVerifier : IGameVerifier
{
    private readonly ILogger _logger;

    public GameVerifier(ILogger logger)
    {
        _logger = logger;
    }

    public bool VerifyInstallPath(GameDefinition game, string path)
    {
        if (!Directory.Exists(path))
        {
            _logger.Debug("Path does not exist: {Path}", path);
            return false;
        }

        // A Steam game may have both Windows and native Linux depots. Either executable can
        // establish the install directory; the chosen release is checked again before install.
        var executableNames = OperatingSystem.IsLinux()
            ? new[] { game.ExeName, game.LinuxExeName }
            : new[] { game.ExeName };
        var candidates = executableNames.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        if (candidates.Length > 0 && !candidates.Any(name => SafeFileExists(path, name!)))
        {
            _logger.Debug("{Game}: no declared executable found at {Path}", game.DisplayName, path);
            return false;
        }

        // Check probe rules
        var probeRules = OperatingSystem.IsLinux() && game.LinuxProbeRules.Count > 0
            ? game.LinuxProbeRules : game.ProbeRules;
        foreach (var rule in probeRules)
        {
            var passed = rule.Type switch
            {
                "fileExists" => SafeFileExists(path, rule.RelativePath),
                "folderExists" => SafeFolderExists(path, rule.RelativePath),
                _ => true // Unknown rule types pass by default (don't block detection)
            };

            if (!passed)
            {
                _logger.Debug("{Game}: probe rule failed — {Type} '{RelPath}'",
                    game.DisplayName, rule.Type, rule.RelativePath);
                return false;
            }
        }

        _logger.Debug("{Game}: install path verified at {Path}", game.DisplayName, path);
        return true;
    }

    private static string LinuxRelativePath(string path) =>
        OperatingSystem.IsLinux() ? path.Replace('\\', '/') : path;

    private static bool SafeFileExists(string root, string relative)
    {
        try { return File.Exists(PathSafety.CombineContained(root, LinuxRelativePath(relative))); }
        catch (InvalidOperationException) { return false; }
    }

    private static bool SafeFolderExists(string root, string relative)
    {
        try { return Directory.Exists(PathSafety.CombineContained(root, LinuxRelativePath(relative))); }
        catch (InvalidOperationException) { return false; }
    }
}
