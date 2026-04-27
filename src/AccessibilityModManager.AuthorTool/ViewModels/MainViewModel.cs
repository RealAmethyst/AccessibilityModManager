using AccessibilityModManager.AuthorTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AuthorConfigService _configService;
    private readonly ILogger _logger;
    private readonly Func<ProjectPickerViewModel> _createPicker;
    private readonly Func<string, IndexEditorViewModel> _createEditor;
    private readonly Func<RegistryAdminViewModel> _createRegistryAdmin;
    private readonly Action<string, string> _showInfoDialog;
    private readonly Func<string, string, bool> _confirmDialog;
    private readonly Func<string?, string?> _browseForFolder;

    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private string _windowTitle = "Plugin Index Author";

    public MainViewModel(
        AuthorConfigService configService,
        ILogger logger,
        Func<ProjectPickerViewModel> createPicker,
        Func<string, IndexEditorViewModel> createEditor,
        Func<RegistryAdminViewModel> createRegistryAdmin,
        Action<string, string> showInfoDialog,
        Func<string, string, bool> confirmDialog,
        Func<string?, string?> browseForFolder)
    {
        _configService = configService;
        _logger = logger;
        _createPicker = createPicker;
        _createEditor = createEditor;
        _createRegistryAdmin = createRegistryAdmin;
        _showInfoDialog = showInfoDialog;
        _confirmDialog = confirmDialog;
        _browseForFolder = browseForFolder;
    }

    public void ShowPicker()
    {
        WindowTitle = "Plugin Index Author";
        CurrentView = _createPicker();
    }

    public void OpenProject(string projectPath)
    {
        try
        {
            var editor = _createEditor(projectPath);
            var recent = _configService.GetRecent(projectPath);
            var displayName = recent?.DisplayName ?? new System.IO.DirectoryInfo(projectPath).Name;
            WindowTitle = $"Plugin Index Author — {displayName}";
            CurrentView = editor;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to open project at {Path}", projectPath);
            _showInfoDialog("Could not open project", $"Could not open the project at:\n{projectPath}\n\n{ex.Message}");
        }
    }

    public void CloseProject()
    {
        ShowPicker();
    }

    public void OpenRegistryAdmin()
    {
        WindowTitle = "Plugin Index Author — Registry admin";
        CurrentView = _createRegistryAdmin();
    }
}
