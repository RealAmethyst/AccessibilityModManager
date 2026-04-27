using System.Collections.ObjectModel;
using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed partial class AddGameDialogViewModel : ObservableObject
{
    private readonly ISet<string> _existingGameIds;
    private readonly Action<string, string> _showInfoDialog;
    private string? _previousAutoGameId;

    [ObservableProperty]
    private string _gameId = "";

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string? _modName;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private string? _steamAppId;

    [ObservableProperty]
    private string? _exeName;

    [ObservableProperty]
    private string? _gitHubRepo;

    public ObservableCollection<string> AvailableGitHubRepos { get; }
    public ObservableCollection<TagSelection> TagSelections { get; } = [];
    public ObservableCollection<LanguageSelection> LanguageSelections { get; } = [];

    public bool Confirmed { get; private set; }
    public Action? CloseDialog { get; set; }

    public AddGameDialogViewModel(
        ISet<string> existingGameIds,
        ObservableCollection<string> availableGitHubRepos,
        Action<string, string> showInfoDialog)
    {
        _existingGameIds = existingGameIds;
        AvailableGitHubRepos = availableGitHubRepos;
        _showInfoDialog = showInfoDialog;

        foreach (var tag in TagCatalog.Core)
            TagSelections.Add(new TagSelection(tag.Id, tag.Label, tag.Category, false, false, () => { }));
        foreach (var lang in LanguageCatalog.All)
            LanguageSelections.Add(new LanguageSelection(lang.Code, lang.Label, false, () => { }));
    }

    public void AddCustomTag(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        if (TagSelections.Any(t => t.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase))) return;
        TagSelections.Add(new TagSelection(trimmed, trimmed, "Custom", true, true, () => { }));
    }

    public void RemoveCustomTag(TagSelection tag)
    {
        if (!tag.IsCustom) return;
        TagSelections.Remove(tag);
    }

    partial void OnDisplayNameChanged(string value)
    {
        // Auto-fill the gameId from the display name unless the user has manually overridden it.
        if (string.IsNullOrEmpty(GameId) || GameId == _previousAutoGameId)
        {
            var suggested = SanitizeGameId(value);
            _previousAutoGameId = suggested;
            GameId = suggested;
        }
    }

    private static string SanitizeGameId(string input)
        => new(input.Where(char.IsLetterOrDigit).ToArray());

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            _showInfoDialog("Display name required",
                "Type the game's display name as users see it (e.g. 'Yu-Gi-Oh! Master Duel').");
            return;
        }
        if (string.IsNullOrWhiteSpace(GameId))
        {
            _showInfoDialog("Game ID required",
                "The game ID is the lowercase identifier used inside index.json. " +
                "Pick something short and unique (e.g. masterduel, digimonworldnextorder).");
            return;
        }

        var id = GameId.Trim();
        if (_existingGameIds.Contains(id))
        {
            _showInfoDialog("Game ID taken",
                $"A game with ID '{id}' already exists in this index. Pick a unique one.");
            return;
        }

        Confirmed = true;
        CloseDialog?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        CloseDialog?.Invoke();
    }

    public GameDefinition ToGame() => new()
    {
        GameId = GameId.Trim(),
        DisplayName = DisplayName.Trim(),
        ModName = string.IsNullOrWhiteSpace(ModName) ? null : ModName!.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description!.Trim(),
        SteamAppId = string.IsNullOrWhiteSpace(SteamAppId) ? null : SteamAppId!.Trim(),
        ExeName = string.IsNullOrWhiteSpace(ExeName) ? null : ExeName!.Trim(),
        ProbeRules = [],
        Dependencies = [],
        Tags = TagSelections.Where(t => t.IsSelected).Select(t => t.Id).ToList(),
        Languages = LanguageSelections.Where(l => l.IsSelected).Select(l => l.Code).ToList()
    };
}
