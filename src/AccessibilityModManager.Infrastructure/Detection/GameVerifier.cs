using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
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

        // Check executable presence if specified
        if (!string.IsNullOrEmpty(game.ExeName))
        {
            var exePath = Path.Combine(path, game.ExeName);
            if (!File.Exists(exePath))
            {
                _logger.Debug("{Game}: exe not found at {ExePath}", game.DisplayName, exePath);
                return false;
            }
        }

        // Check probe rules
        foreach (var rule in game.ProbeRules)
        {
            var passed = rule.Type switch
            {
                "fileExists" => File.Exists(Path.Combine(path, rule.RelativePath)),
                "folderExists" => Directory.Exists(Path.Combine(path, rule.RelativePath)),
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
}
