using System.IO;
using System.Text.Json;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

public sealed class AuthorConfigService
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager-Author");

    private static readonly string ConfigFile = Path.Combine(ConfigDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;
    private AuthorConfig? _cached;

    public AuthorConfigService(ILogger logger)
    {
        _logger = logger;
    }

    public static string GetReposDirectory() => Path.Combine(ConfigDirectory, "repos");

    public AuthorConfig Load()
    {
        if (_cached != null) return _cached;

        try
        {
            if (!File.Exists(ConfigFile))
            {
                _cached = new AuthorConfig();
                return _cached;
            }

            var json = File.ReadAllText(ConfigFile);
            _cached = JsonSerializer.Deserialize<AuthorConfig>(json, JsonOptions) ?? new AuthorConfig();
            return _cached;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load author config from {Path} — starting fresh", ConfigFile);
            _cached = new AuthorConfig();
            return _cached;
        }
    }

    public void Save(AuthorConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigFile, json);
        _cached = config;
    }

    public void RecordRecent(string projectPath, string? displayName = null, string? gitHubRepo = null)
    {
        var config = Load();
        var existing = config.RecentProjects.FirstOrDefault(p =>
            string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.LastOpenedAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(displayName)) existing.DisplayName = displayName;
            if (!string.IsNullOrEmpty(gitHubRepo)) existing.GitHubRepo = gitHubRepo;
        }
        else
        {
            config.RecentProjects.Add(new RecentProject
            {
                Path = projectPath,
                DisplayName = displayName,
                GitHubRepo = gitHubRepo,
                LastOpenedAt = DateTime.UtcNow
            });
        }

        config.LastOpenedProjectPath = projectPath;
        config.RecentProjects = config.RecentProjects
            .OrderByDescending(p => p.LastOpenedAt)
            .Take(20)
            .ToList();

        Save(config);
    }

    public void RemoveRecent(string projectPath)
    {
        var config = Load();
        config.RecentProjects.RemoveAll(p =>
            string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(config.LastOpenedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
            config.LastOpenedProjectPath = null;
        Save(config);
    }

    public RecentProject? GetRecent(string projectPath)
    {
        var config = Load();
        return config.RecentProjects.FirstOrDefault(p =>
            string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));
    }

    public void SetGameSourceRepo(string projectPath, string gameId, string sourceRepo)
    {
        var config = Load();
        var project = config.RecentProjects.FirstOrDefault(p =>
            string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        if (project == null) return;

        project.GameSourceRepos[gameId] = sourceRepo;
        Save(config);
    }

    public string? GetGameSourceRepo(string projectPath, string gameId)
    {
        var project = GetRecent(projectPath);
        if (project == null) return null;
        return project.GameSourceRepos.TryGetValue(gameId, out var repo) ? repo : null;
    }

    public ServerUploadConfig? GetServerUploadConfig() => Load().ServerUpload;

    public void SaveServerUploadConfig(ServerUploadConfig? config)
    {
        var c = Load();
        c.ServerUpload = config;
        Save(c);
    }

    public GameScriptSources? GetGameScriptSources(string projectPath, string gameId)
    {
        var project = GetRecent(projectPath);
        if (project == null) return null;
        return project.GameScriptSources.TryGetValue(gameId, out var sources) ? sources : null;
    }

    public void SetGameScriptSources(string projectPath, string gameId, GameScriptSources sources)
    {
        var config = Load();
        var project = config.RecentProjects.FirstOrDefault(p =>
            string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        if (project == null) return;

        // Strip the entry entirely when all three slots are empty so the config doesn't
        // accumulate noise for games that never touched the Scripts tab.
        if (string.IsNullOrEmpty(sources.PreInstall) &&
            string.IsNullOrEmpty(sources.PostInstall) &&
            string.IsNullOrEmpty(sources.PostUninstall))
        {
            if (!project.GameScriptSources.Remove(gameId)) return;
        }
        else
        {
            project.GameScriptSources[gameId] = sources;
        }

        Save(config);
    }
}
