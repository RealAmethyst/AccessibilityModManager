using System.Text.Json;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Strict manifest parser. Rejects unknown action types — no arbitrary code execution.
/// </summary>
public sealed class ManifestParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly HashSet<string> AllowedActionTypes = ["copyFile", "copyFolder", "replaceFile"];
    private static readonly HashSet<string> AllowedVerifyTypes = ["fileExists", "folderExists", "hashEquals"];

    private readonly ILogger _logger;

    public ManifestParser(ILogger logger)
    {
        _logger = logger;
    }

    public Manifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Manifest deserialized to null");

        if (string.IsNullOrWhiteSpace(manifest.GameId))
            throw new InvalidOperationException("Manifest missing required field: gameId");

        if (string.IsNullOrWhiteSpace(manifest.PluginId))
            throw new InvalidOperationException("Manifest missing required field: pluginId");

        if (string.IsNullOrWhiteSpace(manifest.ModVersion))
            throw new InvalidOperationException("Manifest missing required field: modVersion");

        ValidateActions(manifest);
        ValidateVerifyRules(manifest);

        _logger.Information("Parsed manifest: {PluginId}/{GameId} v{Version}",
            manifest.PluginId, manifest.GameId, manifest.ModVersion);

        return manifest;
    }

    public Manifest ParseFile(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return Parse(json);
    }

    private void ValidateActions(Manifest manifest)
    {
        foreach (var action in manifest.InstallActions)
        {
            var typeName = action switch
            {
                CopyFileAction => "copyFile",
                CopyFolderAction => "copyFolder",
                ReplaceFileAction => "replaceFile",
                _ => "unknown"
            };

            if (!AllowedActionTypes.Contains(typeName))
            {
                _logger.Error("Manifest contains disallowed action type: {Type}", typeName);
                throw new InvalidOperationException(
                    $"Manifest contains disallowed install action type: '{typeName}'. Only {string.Join(", ", AllowedActionTypes)} are allowed.");
            }
        }
    }

    private void ValidateVerifyRules(Manifest manifest)
    {
        foreach (var rule in manifest.Verify)
        {
            if (!AllowedVerifyTypes.Contains(rule.Type))
            {
                _logger.Error("Manifest contains disallowed verify type: {Type}", rule.Type);
                throw new InvalidOperationException(
                    $"Manifest contains disallowed verify rule type: '{rule.Type}'. Only {string.Join(", ", AllowedVerifyTypes)} are allowed.");
            }
        }
    }
}
