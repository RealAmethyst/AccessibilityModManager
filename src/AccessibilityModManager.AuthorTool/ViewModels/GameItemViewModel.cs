using System.Collections.ObjectModel;
using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed partial class GameItemViewModel : ObservableObject
{
    private readonly IndexEditorViewModel _parent;

    [ObservableProperty]
    private string _gameId;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string? _modName;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private string? _steamAppId;

    [ObservableProperty]
    private string? _exeName;

    [ObservableProperty]
    private string? _perGameSourceRepo;

    // Registry detection (non-Steam). All three must be filled for the probe to be emitted.
    [ObservableProperty]
    private string? _registryHive;

    [ObservableProperty]
    private string? _registryKey;

    [ObservableProperty]
    private string? _registryValue;

    [ObservableProperty]
    private bool _registryProbeSubfolders = true;

    // ASCII path shim (junction). Both fields must be filled for the shim to be emitted.
    [ObservableProperty]
    private string? _junctionName;

    [ObservableProperty]
    private string? _junctionReason;

    [ObservableProperty]
    private ModRelease? _selectedRelease;

    [ObservableProperty]
    private DependencyItemViewModel? _selectedDependency;

    public ObservableCollection<ModRelease> Releases { get; } = [];
    public ObservableCollection<DependencyItemViewModel> Dependencies { get; } = [];
    public ObservableCollection<TagSelection> TagSelections { get; } = [];
    public ObservableCollection<LanguageSelection> LanguageSelections { get; } = [];

    public LifecycleScriptEditorViewModel PreInstallScript { get; }
    public LifecycleScriptEditorViewModel PostInstallScript { get; }
    public LifecycleScriptEditorViewModel PostUninstallScript { get; }

    public GameItemViewModel(GameDefinition def, IList<ModRelease> releases, IndexEditorViewModel parent)
    {
        _parent = parent;
        _originalGameId = def.GameId;
        _gameId = def.GameId;
        _displayName = def.DisplayName;
        _modName = def.ModName;
        _description = def.Description;
        _steamAppId = def.SteamAppId;
        _exeName = def.ExeName;
        _registryHive = def.RegistryProbe?.Hive;
        _registryKey = def.RegistryProbe?.Key;
        _registryValue = def.RegistryProbe?.Value;
        _registryProbeSubfolders = def.RegistryProbe?.ProbeSubfolders ?? true;
        _junctionName = def.AsciiPathShim?.JunctionName;
        _junctionReason = def.AsciiPathShim?.Reason;

        foreach (var r in releases) Releases.Add(r);
        foreach (var d in def.Dependencies)
            Dependencies.Add(new DependencyItemViewModel(d, this));

        BuildTagSelections(def.Tags);
        BuildLanguageSelections(def.Languages);

        PreInstallScript = new LifecycleScriptEditorViewModel("Pre-install", def.DefaultPreInstall, this);
        PostInstallScript = new LifecycleScriptEditorViewModel("Post-install", def.DefaultPostInstall, this);
        PostUninstallScript = new LifecycleScriptEditorViewModel("Post-uninstall", def.DefaultPostUninstall, this);

        SelectedRelease = Releases.FirstOrDefault();
    }

    private void BuildTagSelections(IList<string> existingTags)
    {
        TagSelections.Clear();
        var existing = new HashSet<string>(existingTags, StringComparer.OrdinalIgnoreCase);

        foreach (var tag in TagCatalog.Core)
        {
            TagSelections.Add(new TagSelection(
                tag.Id, tag.Label, tag.Category,
                isSelected: existing.Contains(tag.Id),
                isCustom: false,
                onToggle: () => _parent.MarkDirty()));
        }

        foreach (var customId in existingTags
                     .Where(id => TagCatalog.FindById(id) == null)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TagSelections.Add(new TagSelection(
                customId, customId, "Custom",
                isSelected: true,
                isCustom: true,
                onToggle: () => _parent.MarkDirty()));
        }
    }

    private void BuildLanguageSelections(IList<string> existingLanguages)
    {
        LanguageSelections.Clear();
        var existing = new HashSet<string>(existingLanguages, StringComparer.OrdinalIgnoreCase);

        foreach (var lang in LanguageCatalog.All)
        {
            LanguageSelections.Add(new LanguageSelection(
                lang.Code, lang.Label,
                isSelected: existing.Contains(lang.Code),
                onToggle: () => _parent.MarkDirty()));
        }
    }

    public bool HasAnyFilters =>
        TagSelections.Any(t => t.IsSelected) || LanguageSelections.Any(l => l.IsSelected);

    public void AddCustomTag(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        if (TagSelections.Any(t => t.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase))) return;

        TagSelections.Add(new TagSelection(
            trimmed, trimmed, "Custom",
            isSelected: true,
            isCustom: true,
            onToggle: () => _parent.MarkDirty()));
        _parent.MarkDirty();
    }

    public void RemoveCustomTag(TagSelection tag)
    {
        if (!tag.IsCustom) return;
        TagSelections.Remove(tag);
        _parent.MarkDirty();
    }

    partial void OnGameIdChanged(string value) => _parent.MarkDirty();
    partial void OnDisplayNameChanged(string value) => _parent.MarkDirty();
    partial void OnModNameChanged(string? value) => _parent.MarkDirty();
    partial void OnDescriptionChanged(string? value) => _parent.MarkDirty();
    partial void OnSteamAppIdChanged(string? value) => _parent.MarkDirty();
    partial void OnExeNameChanged(string? value) => _parent.MarkDirty();
    partial void OnPerGameSourceRepoChanged(string? value) => _parent.MarkDirty();
    partial void OnRegistryHiveChanged(string? value) => _parent.MarkDirty();
    partial void OnRegistryKeyChanged(string? value) => _parent.MarkDirty();
    partial void OnRegistryValueChanged(string? value) => _parent.MarkDirty();
    partial void OnRegistryProbeSubfoldersChanged(bool value) => _parent.MarkDirty();
    partial void OnJunctionNameChanged(string? value) => _parent.MarkDirty();
    partial void OnJunctionReasonChanged(string? value) => _parent.MarkDirty();

    public void RefreshReleases(IList<ModRelease> latest)
    {
        var prev = SelectedRelease;
        Releases.Clear();
        foreach (var r in latest.OrderBy(r => r.Channel).ThenBy(r => r.Version))
            Releases.Add(r);
        SelectedRelease = Releases.FirstOrDefault(r => prev != null && r.Version == prev.Version && r.Channel == prev.Channel)
            ?? Releases.FirstOrDefault();
    }

    internal void MarkParentDirty() => _parent.MarkDirty();

    /// <summary>
    /// The id this game had when the editor loaded it (or last saved it). The save-back looks
    /// the game up by THIS id, not the editable one — looking up by the edited id found
    /// nothing and silently dropped every change on the game (audit finding 9).
    /// </summary>
    private string _originalGameId;

    public void WriteBackTo(PluginRepoIndex index)
    {
        // Game ids become folder names on every user's machine — block unsafe ones at save time
        // with the same shared rule the manager enforces on fetch.
        AccessibilityModManager.Infrastructure.Security.PathSafety.EnsureSafeId(
            GameId, $"Game id for \"{DisplayName}\"");

        var def = index.Games.FirstOrDefault(g => g.GameId == _originalGameId);
        if (def == null) return;

        // Collision detection is case-INSENSITIVE even though identity comparisons elsewhere are
        // ordinal: game ids become folder names and receipt keys on Windows, where "Game" and
        // "game" are the same place. An ordinal-only check would wave through a rename that
        // silently shares another game's install records.
        var idChanged = !string.Equals(_originalGameId, GameId, StringComparison.Ordinal);
        if (idChanged &&
            (index.Games.Any(g => !ReferenceEquals(g, def) &&
                                  string.Equals(g.GameId, GameId, StringComparison.OrdinalIgnoreCase)) ||
             index.ReleasesByGameId.Keys.Any(k =>
                 !string.Equals(k, _originalGameId, StringComparison.Ordinal) &&
                 string.Equals(k, GameId, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException(
                $"Can't rename \"{_originalGameId}\" to \"{GameId}\" — another game already uses that id " +
                "(ids that differ only in capitalisation count as the same id on Windows).");
        }
        // The releases survive a rename, but the packages they point at DON'T: each one is already
        // published and carries a manifest that still names the old game id, and the manager
        // refuses to install a package whose id doesn't match the game. So a rename that looks
        // entirely successful quietly stops every existing release from installing. Declining puts
        // the id back and lets the author's other edits save as normal.
        if (idChanged)
        {
            var affected = index.ReleasesByGameId.TryGetValue(_originalGameId, out var published)
                ? published.Count
                : 0;
            if (affected > 0 &&
                !_parent.ConfirmDuringSave("Renaming this game breaks its published releases",
                    $"\"{_originalGameId}\" has {affected} published release(s). Their packages were built " +
                    "with the old id inside them, and the manager refuses to install a package whose id " +
                    "doesn't match the game — so after this rename, none of them would install.\n\n" +
                    $"To fix that you'd rebuild each package for \"{GameId}\" and publish it under a new " +
                    "version number.\n\nRename it anyway?"))
            {
                GameId = _originalGameId;
                idChanged = false;
            }
        }

        var newDef = new GameDefinition
        {
            GameId = GameId,
            DisplayName = DisplayName,
            ModName = string.IsNullOrWhiteSpace(ModName) ? null : ModName,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description,
            SteamAppId = string.IsNullOrWhiteSpace(SteamAppId) ? null : SteamAppId,
            ExeName = string.IsNullOrWhiteSpace(ExeName) ? null : ExeName,
            ProbeRules = def.ProbeRules,
            RegistryProbe = BuildRegistryProbe(),
            AsciiPathShim = BuildAsciiPathShim(),
            Dependencies = Dependencies.Select(d => d.ToModel()).ToList(),
            Tags = TagSelections.Where(t => t.IsSelected).Select(t => t.Id).ToList(),
            Languages = LanguageSelections.Where(l => l.IsSelected).Select(l => l.Code).ToList(),
            DefaultPreInstall = PreInstallScript.ToModel(),
            DefaultPostInstall = PostInstallScript.ToModel(),
            DefaultPostUninstall = PostUninstallScript.ToModel()
        };
        var idx = index.Games.IndexOf(def);
        if (idx >= 0) index.Games[idx] = newDef;

        // Explicit rekey on rename: the releases dictionary is keyed by game id, and every
        // release inside carries its own GameId that the manager requires to MATCH the key —
        // moving the list without rewriting the releases would make the whole index refuse to
        // load (finding 9's second half).
        if (idChanged)
        {
            if (index.ReleasesByGameId.TryGetValue(_originalGameId, out var releases))
            {
                index.ReleasesByGameId.Remove(_originalGameId);
                index.ReleasesByGameId[GameId] = releases
                    .Select(r => new ModRelease
                    {
                        GameId = GameId,
                        PluginId = r.PluginId,
                        Version = r.Version,
                        Channel = r.Channel,
                        PackageUrl = r.PackageUrl,
                        Sha256 = r.Sha256,
                        ChangelogUrl = r.ChangelogUrl,
                        Notes = r.Notes,
                        Compatibility = r.Compatibility,
                        Patreon = r.Patreon
                    })
                    .ToList();
            }
            _originalGameId = GameId;
        }
    }

    /// <summary>
    /// Builds the registry probe, or null when not fully configured. All three of hive/key/value
    /// are required by the model, so a partially-filled form emits nothing rather than a broken probe.
    /// </summary>
    private RegistryProbe? BuildRegistryProbe()
    {
        if (string.IsNullOrWhiteSpace(RegistryHive) ||
            string.IsNullOrWhiteSpace(RegistryKey) ||
            string.IsNullOrWhiteSpace(RegistryValue))
            return null;

        return new RegistryProbe
        {
            Hive = RegistryHive!.Trim(),
            Key = RegistryKey!.Trim(),
            Value = RegistryValue!.Trim(),
            ProbeSubfolders = RegistryProbeSubfolders
        };
    }

    /// <summary>Builds the ASCII path shim, or null when name/reason aren't both filled in.</summary>
    private AsciiPathShim? BuildAsciiPathShim()
    {
        if (string.IsNullOrWhiteSpace(JunctionName) || string.IsNullOrWhiteSpace(JunctionReason))
            return null;

        // Same rule the manager enforces at junction-creation time: a single ASCII folder name,
        // nothing that could aim the junction at another path. Caught at save so it can never
        // be published.
        var name = AccessibilityModManager.Infrastructure.Security.PathSafety.EnsureLeafFileName(
            JunctionName, $"Folder link name for \"{DisplayName}\"");
        if (name.Any(c => c > 127))
            throw new InvalidOperationException(
                $"Folder link name for \"{DisplayName}\" must contain only ASCII characters — " +
                "that's the whole point of the link.");

        return new AsciiPathShim
        {
            JunctionName = name,
            Reason = JunctionReason!.Trim()
        };
    }

    public void AddDependencyFromPreset(DependencyPreset preset)
    {
        var dep = new DependencyItemViewModel(preset.ToModel(), this);
        Dependencies.Add(dep);
        SelectedDependency = dep;
        MarkParentDirty();
    }

    public void AddCustomDependency()
    {
        var dep = new DependencyItemViewModel(new Dependency
        {
            Id = "custom-dependency",
            Type = "framework",
            Required = true,
            Check = new DependencyCheck { FilePath = "" },
            Fix = new DependencyFix { DownloadUrl = "" }
        }, this);
        Dependencies.Add(dep);
        SelectedDependency = dep;
        MarkParentDirty();
    }

    public void RemoveDependency(DependencyItemViewModel dep)
    {
        var wasSelected = SelectedDependency == dep;
        Dependencies.Remove(dep);
        if (wasSelected)
            SelectedDependency = Dependencies.FirstOrDefault();
        MarkParentDirty();
    }

    // Falls back to this when AutomationProperties.Name isn't set on the container.
    // Without it, screen readers read the type's full name.
    public override string ToString() => DisplayName;
}
