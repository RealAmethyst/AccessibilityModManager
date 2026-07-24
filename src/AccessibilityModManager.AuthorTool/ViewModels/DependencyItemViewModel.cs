using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed partial class DependencyItemViewModel : ObservableObject
{
    private readonly GameItemViewModel _parent;

    [ObservableProperty]
    private string _id;

    [ObservableProperty]
    private string _type;

    [ObservableProperty]
    private bool _required;

    /// <summary>True when this dependency is the game itself (run its installer pre-detection).</summary>
    [ObservableProperty]
    private bool _isGameInstaller;

    [ObservableProperty]
    private string? _minVersion;

    [ObservableProperty]
    private string? _checkRegistryKey;

    [ObservableProperty]
    private string? _checkRegistryValue;

    /// <summary>"HKLM" (default when empty) or "HKCU". The manager probes both registry views
    /// (64-bit and WOW6432Node) of the hive automatically unless the view below pins one.</summary>
    [ObservableProperty]
    private string? _checkRegistryHive;

    /// <summary>"both" (default when empty), "64", or "32" — pins the check to one registry
    /// view for per-architecture components like the .NET runtimes.</summary>
    [ObservableProperty]
    private string? _checkRegistryView;

    [ObservableProperty]
    private string? _checkFilePath;

    [ObservableProperty]
    private string? _fixDownloadUrl;

    [ObservableProperty]
    private string? _fixBundledPath;

    // ----- AutoInstall fields -----

    /// <summary>
    /// True when the author wants the manager to download + apply the dep automatically.
    /// Drives the visibility of the AutoInstall sub-editor in the view.
    /// </summary>
    [ObservableProperty]
    private bool _autoInstallEnabled;

    /// <summary>"extractZip", "runInstaller", "copyFile", or "extractApp".</summary>
    [ObservableProperty]
    private string _autoInstallKind = "extractZip";

    [ObservableProperty]
    private string? _autoInstallSha256;

    [ObservableProperty]
    private string? _autoInstallTargetDir;

    /// <summary>Comma-separated for the editor; serialized as a list. Used by extractZip.</summary>
    [ObservableProperty]
    private string? _autoInstallBlocklistText;

    /// <summary>Used by copyFile only.</summary>
    [ObservableProperty]
    private string? _autoInstallTargetFileName;

    /// <summary>Space-separated args for the editor; serialized as a list. Used by runInstaller.</summary>
    [ObservableProperty]
    private string? _autoInstallArgsText;

    [ObservableProperty]
    private bool _autoInstallNeedsAdmin;

    public DependencyItemViewModel(Dependency dep, GameItemViewModel parent)
    {
        _parent = parent;
        _id = dep.Id;
        _type = dep.Type;
        _required = dep.Required;
        _isGameInstaller = dep.IsGameInstaller;
        _minVersion = dep.MinVersion;
        _checkRegistryKey = dep.Check?.RegistryKey;
        _checkRegistryValue = dep.Check?.RegistryValue;
        _checkRegistryHive = dep.Check?.RegistryHive;
        _checkRegistryView = dep.Check?.RegistryView;
        _checkFilePath = dep.Check?.FilePath;
        _fixDownloadUrl = dep.Fix?.DownloadUrl;
        _fixBundledPath = dep.Fix?.BundledPath;

        // Hydrate AutoInstall sub-editor from the existing model, if any.
        var auto = dep.Fix?.AutoInstall;
        _autoInstallEnabled = auto != null;
        _autoInstallSha256 = auto?.Sha256;
        switch (auto)
        {
            case ExtractZipAutoInstall ez:
                _autoInstallKind = "extractZip";
                _autoInstallTargetDir = ez.TargetDir;
                _autoInstallBlocklistText = ez.Blocklist.Count > 0 ? string.Join(", ", ez.Blocklist) : null;
                break;
            case RunInstallerAutoInstall ri:
                _autoInstallKind = "runInstaller";
                _autoInstallArgsText = ri.Args.Count > 0 ? string.Join(" ", ri.Args) : null;
                _autoInstallNeedsAdmin = ri.NeedsAdmin;
                break;
            case CopyFileAutoInstall cf:
                _autoInstallKind = "copyFile";
                _autoInstallTargetDir = cf.TargetDir;
                _autoInstallTargetFileName = cf.TargetFileName;
                break;
            case ExtractAppAutoInstall:
                // Portable app (emulator): URL + SHA256 only; no target dir/args.
                _autoInstallKind = "extractApp";
                break;
        }
    }

    partial void OnIdChanged(string value) => _parent.MarkParentDirty();
    partial void OnTypeChanged(string value) => _parent.MarkParentDirty();
    partial void OnRequiredChanged(bool value) => _parent.MarkParentDirty();
    partial void OnIsGameInstallerChanged(bool value) => _parent.MarkParentDirty();
    partial void OnMinVersionChanged(string? value) => _parent.MarkParentDirty();
    partial void OnCheckRegistryKeyChanged(string? value) => _parent.MarkParentDirty();
    partial void OnCheckRegistryValueChanged(string? value) => _parent.MarkParentDirty();
    partial void OnCheckRegistryHiveChanged(string? value) => _parent.MarkParentDirty();
    partial void OnCheckRegistryViewChanged(string? value) => _parent.MarkParentDirty();
    partial void OnCheckFilePathChanged(string? value) => _parent.MarkParentDirty();
    partial void OnFixDownloadUrlChanged(string? value) => _parent.MarkParentDirty();
    partial void OnFixBundledPathChanged(string? value) => _parent.MarkParentDirty();
    partial void OnAutoInstallEnabledChanged(bool value) => _parent.MarkParentDirty();
    partial void OnAutoInstallKindChanged(string value)
    {
        _parent.MarkParentDirty();
        // The conditional field rows bind to these — refresh them when the kind changes so the
        // editor shows/hides the right fields live.
        OnPropertyChanged(nameof(IsExtractZipKind));
        OnPropertyChanged(nameof(IsRunInstallerKind));
        OnPropertyChanged(nameof(IsCopyFileKind));
        OnPropertyChanged(nameof(IsExtractAppKind));
        OnPropertyChanged(nameof(ShowTargetDir));
    }
    partial void OnAutoInstallSha256Changed(string? value) => _parent.MarkParentDirty();
    partial void OnAutoInstallTargetDirChanged(string? value) => _parent.MarkParentDirty();
    partial void OnAutoInstallBlocklistTextChanged(string? value) => _parent.MarkParentDirty();
    partial void OnAutoInstallTargetFileNameChanged(string? value) => _parent.MarkParentDirty();
    partial void OnAutoInstallArgsTextChanged(string? value) => _parent.MarkParentDirty();
    partial void OnAutoInstallNeedsAdminChanged(bool value) => _parent.MarkParentDirty();

    public bool IsExtractZipKind => AutoInstallKind == "extractZip";
    public bool IsRunInstallerKind => AutoInstallKind == "runInstaller";
    public bool IsCopyFileKind => AutoInstallKind == "copyFile";
    public bool IsExtractAppKind => AutoInstallKind == "extractApp";

    /// <summary>Target dir applies to extractZip + copyFile only (not runInstaller / extractApp).</summary>
    public bool ShowTargetDir => IsExtractZipKind || IsCopyFileKind;

    public Dependency ToModel()
    {
        // Dependency ids become folder names on every user's machine — same shared rule the
        // manager enforces on fetch, applied here so a bad id can't be published.
        PathSafety.EnsureSafeId(Id, "Dependency id");

        var hasCheck = !string.IsNullOrWhiteSpace(CheckRegistryKey)
            || !string.IsNullOrWhiteSpace(CheckRegistryValue)
            || !string.IsNullOrWhiteSpace(CheckFilePath);
        var auto = BuildAutoInstall();
        var hasFix = !string.IsNullOrWhiteSpace(FixDownloadUrl)
            || !string.IsNullOrWhiteSpace(FixBundledPath)
            || auto != null;

        return new Dependency
        {
            Id = Id,
            Type = Type,
            Required = Required,
            IsGameInstaller = IsGameInstaller,
            MinVersion = string.IsNullOrWhiteSpace(MinVersion) ? null : MinVersion,
            Check = hasCheck ? new DependencyCheck
            {
                RegistryKey = string.IsNullOrWhiteSpace(CheckRegistryKey) ? null : CheckRegistryKey,
                RegistryValue = string.IsNullOrWhiteSpace(CheckRegistryValue) ? null : CheckRegistryValue,
                RegistryHive = string.IsNullOrWhiteSpace(CheckRegistryHive) ? null : CheckRegistryHive.Trim(),
                RegistryView = string.IsNullOrWhiteSpace(CheckRegistryView) ? null : CheckRegistryView.Trim(),
                FilePath = string.IsNullOrWhiteSpace(CheckFilePath) ? null : CheckFilePath
            } : null,
            Fix = hasFix ? new DependencyFix
            {
                DownloadUrl = string.IsNullOrWhiteSpace(FixDownloadUrl) ? null : FixDownloadUrl,
                BundledPath = string.IsNullOrWhiteSpace(FixBundledPath) ? null : FixBundledPath,
                AutoInstall = auto
            } : null
        };
    }

    private DependencyAutoInstall? BuildAutoInstall()
    {
        if (!AutoInstallEnabled) return null;
        if (string.IsNullOrWhiteSpace(AutoInstallSha256))
            throw new InvalidOperationException(
                $"Dependency '{Id}': SHA256 is required for AutoInstall. Either compute it from a local file or paste the upstream hash.");

        return AutoInstallKind switch
        {
            "extractZip" => new ExtractZipAutoInstall
            {
                Sha256 = AutoInstallSha256!.Trim(),
                TargetDir = NormalizedTargetDirOrThrow(),
                Blocklist = ParseList(AutoInstallBlocklistText, ',')
            },
            "runInstaller" => new RunInstallerAutoInstall
            {
                Sha256 = AutoInstallSha256!.Trim(),
                Args = ParseList(AutoInstallArgsText, ' '),
                NeedsAdmin = AutoInstallNeedsAdmin
            },
            "copyFile" => new CopyFileAutoInstall
            {
                Sha256 = AutoInstallSha256!.Trim(),
                TargetDir = NormalizedTargetDirOrThrow(),
                TargetFileName = NormalizedTargetFileNameOrThrow()
            },
            "extractApp" => new ExtractAppAutoInstall
            {
                Sha256 = AutoInstallSha256!.Trim()
            },
            _ => throw new InvalidOperationException($"Unknown AutoInstall kind: {AutoInstallKind}")
        };
    }

    /// <summary>
    /// The target dir as the manager will interpret it: leading/trailing slashes stripped (the
    /// "/Updater/1.5.0/" mistake published for Pokémon TCG Live gets healed right here at
    /// authoring time), absolute paths and ".." rejected with a save-blocking message so a value
    /// the manager would refuse can never be published.
    /// </summary>
    private string? NormalizedTargetDirOrThrow()
    {
        var normalized = PathSafety.NormalizeRelativeDir(
            AutoInstallTargetDir, $"Dependency '{Id}': AutoInstall target folder");
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// copyFile's target file name must be a bare file name — the manager rejects anything with
    /// folders, a root, or invalid filename characters in it, so the AuthorTool blocks it at save
    /// time with the exact same shared rule.
    /// </summary>
    private string? NormalizedTargetFileNameOrThrow()
    {
        if (string.IsNullOrWhiteSpace(AutoInstallTargetFileName)) return null;
        return PathSafety.EnsureLeafFileName(
            AutoInstallTargetFileName, $"Dependency '{Id}': target file name");
    }

    private static List<string> ParseList(string? text, char separator)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .ToList();
    }

    public void RemoveSelf()
    {
        _parent.RemoveDependency(this);
    }

    // Without this override the ListBox falls back to the type's full name when
    // a screen reader's container has no explicit AutomationProperties.Name.
    public override string ToString() => Id;
}
