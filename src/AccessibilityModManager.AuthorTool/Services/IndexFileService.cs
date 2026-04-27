using System.IO;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// Reads and writes the per-plugin <c>index.json</c> file. Output formatting matches
/// what add-release.sh produced (2-space indent, camelCase, trailing newline) so a
/// hand-edited file and a tool-edited file produce minimal diffs.
/// </summary>
public sealed class IndexFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private readonly ILogger _logger;

    public IndexFileService(ILogger logger)
    {
        _logger = logger;
    }

    public static string GetIndexPath(string projectFolder) =>
        Path.Combine(projectFolder, "index.json");

    public bool Exists(string projectFolder) => File.Exists(GetIndexPath(projectFolder));

    public PluginRepoIndex Load(string projectFolder)
    {
        var path = GetIndexPath(projectFolder);
        if (!File.Exists(path))
            throw new FileNotFoundException($"index.json not found at {path}");

        var json = File.ReadAllText(path);
        var index = JsonSerializer.Deserialize<PluginRepoIndex>(json, JsonOptions)
            ?? throw new InvalidOperationException("index.json deserialized to null");

        _logger.Information("Loaded index.json with {GameCount} games from {Path}",
            index.Games.Count, path);
        return index;
    }

    public void Save(string projectFolder, PluginRepoIndex index)
    {
        var path = GetIndexPath(projectFolder);
        Directory.CreateDirectory(projectFolder);

        var json = JsonSerializer.Serialize(index, JsonOptions);
        File.WriteAllText(path, json + Environment.NewLine);

        _logger.Information("Saved index.json with {GameCount} games to {Path}",
            index.Games.Count, path);
    }

    public PluginRepoIndex CreateStarter(string pluginId)
    {
        return new PluginRepoIndex
        {
            PluginId = pluginId,
            RepoVersion = "1",
            GeneratedAt = DateTime.UtcNow,
            Games = [],
            ReleasesByGameId = new Dictionary<string, List<ModRelease>>()
        };
    }
}
