using System.Security.Cryptography;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Installer;

/// <summary>
/// Runs post-install verification rules from the manifest.
/// </summary>
public sealed class InstallVerifier
{
    private readonly ILogger _logger;

    public InstallVerifier(ILogger logger)
    {
        _logger = logger;
    }

    public bool Verify(List<VerifyRule> rules, string gameInstallPath)
    {
        if (rules.Count == 0)
        {
            _logger.Information("No verification rules to check");
            return true;
        }

        var allPassed = true;

        foreach (var rule in rules)
        {
            var passed = rule.Type switch
            {
                "fileExists" => VerifyFileExists(rule, gameInstallPath),
                "folderExists" => VerifyFolderExists(rule, gameInstallPath),
                "hashEquals" => VerifyHashEquals(rule, gameInstallPath),
                _ => throw new InvalidOperationException($"Unknown verify rule type: {rule.Type}")
            };

            if (!passed)
            {
                allPassed = false;
                _logger.Error("Verification FAILED: {Type} for {Path}", rule.Type, rule.Path);
            }
        }

        if (allPassed)
            _logger.Information("All {Count} verification rules passed", rules.Count);

        return allPassed;
    }

    private bool VerifyFileExists(VerifyRule rule, string gameDir)
    {
        var fullPath = Path.GetFullPath(Path.Combine(gameDir, rule.Path));
        var exists = File.Exists(fullPath);
        _logger.Debug("VerifyFileExists: {Path} = {Result}", rule.Path, exists);
        return exists;
    }

    private bool VerifyFolderExists(VerifyRule rule, string gameDir)
    {
        var fullPath = Path.GetFullPath(Path.Combine(gameDir, rule.Path));
        var exists = Directory.Exists(fullPath);
        _logger.Debug("VerifyFolderExists: {Path} = {Result}", rule.Path, exists);
        return exists;
    }

    private bool VerifyHashEquals(VerifyRule rule, string gameDir)
    {
        if (string.IsNullOrEmpty(rule.Sha256))
        {
            _logger.Warning("hashEquals rule for {Path} has no sha256 value", rule.Path);
            return false;
        }

        var fullPath = Path.GetFullPath(Path.Combine(gameDir, rule.Path));
        if (!File.Exists(fullPath))
        {
            _logger.Debug("VerifyHashEquals: file not found: {Path}", rule.Path);
            return false;
        }

        using var stream = File.OpenRead(fullPath);
        var hashBytes = SHA256.HashData(stream);
        var actual = Convert.ToHexStringLower(hashBytes);
        var expected = rule.Sha256.ToLowerInvariant();
        var match = actual == expected;

        _logger.Debug("VerifyHashEquals: {Path} expected={Expected} actual={Actual} match={Match}",
            rule.Path, expected, actual, match);

        return match;
    }
}
