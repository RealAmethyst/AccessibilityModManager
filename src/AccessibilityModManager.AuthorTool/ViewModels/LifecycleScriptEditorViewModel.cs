using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessibilityModManager.AuthorTool.ViewModels;

/// <summary>
/// Edits one lifecycle script slot (pre-install, post-install, or post-uninstall) for a game.
/// Author toggles <see cref="IsEnabled"/> to opt the slot in; when disabled, the manifest is
/// built without that hook. <see cref="IsEnabled"/> + the six description fields are persisted
/// into <c>GameDefinition.DefaultPreInstall</c>/<c>DefaultPostInstall</c>/
/// <c>DefaultPostUninstall</c> so the next release inherits them.
/// </summary>
public sealed partial class LifecycleScriptEditorViewModel : ObservableObject
{
    private readonly GameItemViewModel _parent;

    /// <summary>"Pre-install", "Post-install", or "Post-uninstall" — drives the section heading
    /// and screen-reader announcements.</summary>
    public string HookLabel { get; }

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickedFileDisplay))]
    private string? _executable;

    [ObservableProperty]
    private bool _needsAdmin;

    [ObservableProperty]
    private bool _failureFatal = true;

    /// <summary>
    /// When on, the manifest builder also emits a copyFile install action that puts the
    /// script in the game folder permanently. Useful when the script doubles as a launcher
    /// the user can re-run, or when other game files reference it relatively.
    /// </summary>
    [ObservableProperty]
    private bool _installToGameFolder;

    /// <summary>
    /// When on, the script also runs every time the user updates this mod, not just on the
    /// first install. Default off — most lifecycle scripts only need to apply once (e.g. a
    /// registry-key write). Toggle on for scripts that operate on the mod's installed files
    /// and need to re-apply when those files change. Has no effect on post-uninstall hooks.
    /// </summary>
    [ObservableProperty]
    private bool _runOnUpdate;

    /// <summary>
    /// When on, the manager copies the script into the game folder before running and
    /// invokes it from there. Required for scripts that resolve paths via their own location
    /// (e.g. patchers that use <c>Assembly.Location</c>) — those scripts ignore the
    /// <c>--gameFolder</c> argument and the working directory we set, so the script's own
    /// folder has to be the game folder. Cleanup is automatic unless
    /// <see cref="InstallToGameFolder"/> is also on.
    /// </summary>
    [ObservableProperty]
    private bool _runFromGameFolder;

    [ObservableProperty]
    private string? _what;

    [ObservableProperty]
    private string? _why;

    [ObservableProperty]
    private string? _modifies;

    /// <summary>
    /// Absolute path to the script file on the author's machine, set by the Browse button.
    /// At build time the manifest builder reads bytes from here and writes them into the
    /// wrapped ZIP at <see cref="Executable"/>, regardless of whether the file lives inside
    /// the source folder. UI-only — never serialized into index.json or manifest.json;
    /// persisted via <c>AuthorConfig.GameScriptSources</c> so it survives restarts.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourcePathDisplay))]
    [NotifyPropertyChangedFor(nameof(HasAbsoluteSourcePath))]
    [NotifyPropertyChangedFor(nameof(PickedFileDisplay))]
    private string? _absoluteSourcePath;

    /// <summary>True when the author picked a file via Browse — drives the cleared state below.</summary>
    public bool HasAbsoluteSourcePath => !string.IsNullOrEmpty(AbsoluteSourcePath);

    /// <summary>
    /// What the read-only "Script:" line shows to the author. Lifts the picked basename so the
    /// in-package path (<c>files/scripts/foo.exe</c>) — which is internal plumbing — never
    /// reaches the UI. Falls back to a "no file picked" hint when the slot is empty.
    /// </summary>
    public string PickedFileDisplay
    {
        get
        {
            if (!string.IsNullOrEmpty(AbsoluteSourcePath))
                return System.IO.Path.GetFileName(AbsoluteSourcePath);
            if (!string.IsNullOrEmpty(Executable))
                return System.IO.Path.GetFileName(Executable.Replace('\\', '/'));
            return "(no file picked — click Browse)";
        }
    }

    /// <summary>
    /// Optional second line under the script display: the absolute path the file was picked
    /// from. Empty when nothing has been picked, so the row collapses gracefully.
    /// </summary>
    public string SourcePathDisplay => string.IsNullOrEmpty(AbsoluteSourcePath)
        ? ""
        : $"from {AbsoluteSourcePath}";

    public LifecycleScriptEditorViewModel(string hookLabel, LifecycleScript? source, GameItemViewModel parent)
    {
        HookLabel = hookLabel;
        _parent = parent;
        if (source != null)
        {
            _isEnabled = true;
            _executable = source.Executable;
            _needsAdmin = source.NeedsAdmin;
            _failureFatal = source.FailureFatal;
            _installToGameFolder = source.InstallToGameFolder;
            _runOnUpdate = source.RunOnUpdate;
            _runFromGameFolder = source.RunFromGameFolder;
            _what = source.What;
            _why = source.Why;
            _modifies = source.Modifies;
        }
    }

    /// <summary>
    /// Builds a <see cref="LifecycleScript"/> from the current fields, or returns null when the
    /// slot is disabled. Throws when the slot is enabled but a required field is empty so the
    /// AuthorTool's save flow can surface the problem.
    /// </summary>
    public LifecycleScript? ToModel()
    {
        if (!IsEnabled) return null;

        if (string.IsNullOrWhiteSpace(Executable))
            throw new InvalidOperationException(
                $"{HookLabel}: pick a script file with Browse — the slot is enabled but no file is set.");
        if (string.IsNullOrWhiteSpace(What) || string.IsNullOrWhiteSpace(Why) || string.IsNullOrWhiteSpace(Modifies))
            throw new InvalidOperationException(
                $"{HookLabel}: please fill in What, Why, and What it modifies — users see these on the warning dialog.");

        return new LifecycleScript
        {
            Executable = Executable!.Trim(),
            NeedsAdmin = NeedsAdmin,
            FailureFatal = FailureFatal,
            InstallToGameFolder = InstallToGameFolder,
            RunOnUpdate = RunOnUpdate,
            RunFromGameFolder = RunFromGameFolder,
            What = What!.Trim(),
            Why = Why!.Trim(),
            Modifies = Modifies!.Trim()
        };
    }

    /// <summary>
    /// Sets <see cref="AbsoluteSourcePath"/> and rewrites <see cref="Executable"/> to the
    /// canonical <c>files/scripts/&lt;filename&gt;</c> form. Called by the view code-behind
    /// after the file picker confirms. Returns the in-package path so the view can reflect
    /// it back to the user without re-querying the property.
    /// </summary>
    public string ApplyPickedFile(string absolutePath)
    {
        AbsoluteSourcePath = absolutePath;
        var inPackagePath = "files/scripts/" + System.IO.Path.GetFileName(absolutePath);
        Executable = inPackagePath;
        if (!IsEnabled) IsEnabled = true;
        return inPackagePath;
    }

    partial void OnIsEnabledChanged(bool value) => _parent.MarkParentDirty();
    partial void OnExecutableChanged(string? value) => _parent.MarkParentDirty();
    partial void OnNeedsAdminChanged(bool value) => _parent.MarkParentDirty();
    partial void OnFailureFatalChanged(bool value) => _parent.MarkParentDirty();
    partial void OnInstallToGameFolderChanged(bool value) => _parent.MarkParentDirty();
    partial void OnRunOnUpdateChanged(bool value) => _parent.MarkParentDirty();
    partial void OnRunFromGameFolderChanged(bool value) => _parent.MarkParentDirty();
    partial void OnWhatChanged(string? value) => _parent.MarkParentDirty();
    partial void OnWhyChanged(string? value) => _parent.MarkParentDirty();
    partial void OnModifiesChanged(string? value) => _parent.MarkParentDirty();
    partial void OnAbsoluteSourcePathChanged(string? value) => _parent.MarkParentDirty();
}
