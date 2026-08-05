using System.Collections.ObjectModel;
using System.IO;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed partial class BuildPackageDialogViewModel : ObservableObject
{
    private readonly AuthoringWorkflowFacade _workflows;
    private readonly string _gameId;
    private readonly string _pluginId;
    private readonly IList<Dependency> _dependencies;
    private readonly LifecycleScriptInputs _scripts;
    private readonly Func<string?, string?> _browseForFolder;
    private readonly Action<string, string> _showInfoDialog;
    private readonly ILogger _logger;

    public string GameDisplayName { get; }

    [ObservableProperty]
    private string? _sourceFolder;

    [ObservableProperty]
    private string? _version;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<DetectedEntry> DetectedEntries { get; } = [];

    public Action? CloseDialog { get; set; }
    public string? ResultZipPath { get; private set; }

    public BuildPackageDialogViewModel(
        string gameId,
        string gameDisplayName,
        string pluginId,
        string suggestedVersion,
        IList<Dependency> dependencies,
        AuthoringWorkflowFacade workflows,
        Func<string?, string?> browseForFolder,
        Action<string, string> showInfoDialog,
        ILogger logger,
        LifecycleScriptInputs? scripts = null)
    {
        _gameId = gameId;
        GameDisplayName = gameDisplayName;
        _pluginId = pluginId;
        _version = suggestedVersion;
        _dependencies = dependencies;
        _workflows = workflows;
        _browseForFolder = browseForFolder;
        _showInfoDialog = showInfoDialog;
        _logger = logger;
        _scripts = scripts ?? new LifecycleScriptInputs();
    }

    [RelayCommand]
    private void PickSource()
    {
        var path = _browseForFolder(SourceFolder);
        if (string.IsNullOrEmpty(path)) return;
        SourceFolder = path;
        RebuildPreview();
    }

    partial void OnSourceFolderChanged(string? value) => RebuildPreview();

    private void RebuildPreview()
    {
        DetectedEntries.Clear();
        if (string.IsNullOrEmpty(SourceFolder) || !Directory.Exists(SourceFolder)) return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(SourceFolder, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(entry);
            var isFolder = Directory.Exists(entry);
            DetectedEntries.Add(new DetectedEntry
            {
                Name = name,
                IsFolder = isFolder,
                Description = isFolder
                    ? $"folder copies to: game-folder/{name}/"
                    : $"file copies to: game-folder/{name}"
            });
        }
    }

    [RelayCommand]
    private async Task BuildAsync()
    {
        if (string.IsNullOrEmpty(SourceFolder))
        {
            _showInfoDialog("Pick a folder",
                "Pick the folder containing your mod's files first. The folder layout is preserved as-is — top-level files copy to the game folder, top-level folders copy to the game folder.");
            return;
        }
        if (string.IsNullOrWhiteSpace(Version))
        {
            _showInfoDialog("Version required", "Type the version number, e.g. 1.0.0 or 1.8.0.");
            return;
        }

        IsBusy = true;
        StatusMessage = "Building wrapped package...";
        try
        {
            var sanitizedVersion = Version!.Trim();
            var fileName = $"{_gameId}-v{sanitizedVersion}-amm.zip";
            var outputPath = Path.Combine(ManifestBuilderService.GetBuildsDirectory(), fileName);

            var result = await _workflows.BuildPackageAsync(
                new PackageBuildRequest(
                    SourceFolder,
                    outputPath,
                    _pluginId,
                    _gameId,
                    sanitizedVersion,
                    _dependencies.ToList(),
                    _scripts),
                CancellationToken.None);

            ResultZipPath = result.ZipPath;
            StatusMessage = $"Built {result.FileCount} files. Returning to release dialog.";
            CloseDialog?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Build failed");
            _showInfoDialog("Build failed", ex.Message);
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        ResultZipPath = null;
        CloseDialog?.Invoke();
    }
}

public sealed class DetectedEntry
{
    public required string Name { get; init; }
    public required bool IsFolder { get; init; }
    public required string Description { get; init; }
    public override string ToString() => Name;
}
