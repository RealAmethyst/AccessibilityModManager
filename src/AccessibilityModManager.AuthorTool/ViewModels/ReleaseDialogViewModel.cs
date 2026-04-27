using System.Collections.ObjectModel;
using System.IO;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Patreon;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed partial class ReleaseDialogViewModel : ObservableObject
{
    private readonly Sha256HashService _hashService;
    private readonly GitHubService _gitHubService;
    private readonly AuthorConfigService _configService;
    private readonly ILogger _logger;
    private readonly Action<string, string> _showInfoDialog;
    private readonly Func<string, string, string?, string?> _browseForFile;
    private readonly Func<string, string?> _showBuildPackageDialog;
    private readonly string _pluginId;
    private readonly string _projectPath;
    private readonly string _gameId;
    private string? _previousAutoTag;
    private string? _previousAutoChangelog;
    private string? _previousAutoPackageUrl;

    public string GameDisplayName { get; }
    public bool IsEditingExistingRelease { get; }
    public string DialogTitle => IsEditingExistingRelease
        ? $"Edit release for {GameDisplayName}"
        : $"Add release for {GameDisplayName}";

    /// <summary>
    /// User's GitHub repos so the dialog can show a dropdown rather than a free-text field.
    /// Shared with IndexEditorViewModel — the editor populates it once, the dialog observes.
    /// </summary>
    public ObservableCollection<string> AvailableGitHubRepos { get; }

    [ObservableProperty]
    private string _sourceRepo = "";

    [ObservableProperty]
    private string? _version;

    [ObservableProperty]
    private string? _channel = "stable";

    [ObservableProperty]
    private string? _tagName;

    [ObservableProperty]
    private string? _localZipPath;

    [ObservableProperty]
    private string? _assetFileName;

    [ObservableProperty]
    private string? _sha256;

    [ObservableProperty]
    private string? _packageUrl;

    [ObservableProperty]
    private string? _changelogUrl;

    [ObservableProperty]
    private string? _releaseNotes;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    // ----- Patreon gating -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPatreonSectionVisible))]
    [NotifyPropertyChangedFor(nameof(IsPublicRelease))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
    [NotifyPropertyChangedFor(nameof(IsPatreonPostUiVisible))]
    [NotifyPropertyChangedFor(nameof(IsServerUploadInfoVisible))]
    private bool _isPatreonGated;

    /// <summary>
    /// True when the global server-upload settings are filled in. Captured at dialog
    /// construction; toggling settings while the dialog is open isn't supported (rare,
    /// and reopening the dialog re-reads).
    /// </summary>
    public bool IsServerUploadConfigured { get; }

    /// <summary>
    /// Patreon post URL + attachment dropdown + setup instructions are only relevant when
    /// the file lives on a Patreon post (no server upload configured). When the author has
    /// a download server, the file lives there and the post UI is just noise.
    /// </summary>
    public bool IsPatreonPostUiVisible => IsPatreonGated && !IsServerUploadConfigured;

    /// <summary>
    /// Friendly inline note that replaces the Patreon-post instructions when the author
    /// has a server configured — confirms what's about to happen on save.
    /// </summary>
    public bool IsServerUploadInfoVisible => IsPatreonGated && IsServerUploadConfigured;

    /// <summary>
    /// Subtitle the dialog renders right under the title. Public mode mentions GitHub
    /// because that's the standard upload path; Patreon mode replaces the wording so the
    /// author isn't told to do something that doesn't apply.
    /// </summary>
    public string SubtitleText => IsPatreonGated
        ? (IsServerUploadConfigured
            ? "Build the wrapped ZIP and pick the tiers that get access. The AuthorTool uploads to your download server on Save; the manager fetches from there for entitled patrons."
            : "Build the wrapped ZIP, then upload it manually to a tier-locked Patreon post. The manager fetches it from Patreon for entitled patrons.")
        : "Fill in the version, pick the wrapped ZIP, then upload to GitHub or save the URL directly.";

    /// <summary>
    /// Bumped whenever the bound <see cref="PatreonAuthorService"/> raises StateChanged.
    /// Drives [NotifyPropertyChangedFor] so the in-dialog sign-in button text + visibility
    /// react when the user signs in / out (either from the toolbar or from the dialog itself).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedInToPatreon))]
    [NotifyPropertyChangedFor(nameof(IsNotSignedInToPatreon))]
    [NotifyPropertyChangedFor(nameof(PatreonSignedInAsText))]
    private bool _patreonStateBumper;

    public bool IsSignedInToPatreon => _patreonAuthor.IsSignedIn;
    public bool IsNotSignedInToPatreon => !_patreonAuthor.IsSignedIn;
    public string PatreonSignedInAsText
    {
        get
        {
            if (!_patreonAuthor.IsSignedIn) return "";
            var name = _patreonAuthor.CurrentAccount?.FullName ?? _patreonAuthor.CurrentAccount?.Email ?? "your Patreon account";
            return $"Signed in as {name}.";
        }
    }

    /// <summary>
    /// Inverse of <see cref="IsPatreonGated"/>. Bound by GitHub-side fields' Visibility so
    /// they hide when the author flips the release into Patreon-gated mode — the "GitHub
    /// repo / Public URL / Asset filename / Tag" inputs are meaningless when the asset is
    /// hosted on Patreon.
    /// </summary>
    public bool IsPublicRelease => !IsPatreonGated;

    /// <summary>
    /// "Save" for Patreon-gated releases (no GitHub upload happens), "Save without upload"
    /// otherwise (still distinct from the "Upload and save" option which uses gh CLI).
    /// </summary>
    public string SaveButtonText => IsPatreonGated ? "Save" : "Save without upload";

    [ObservableProperty]
    private string? _patreonPostUrl;

    [ObservableProperty]
    private string? _patreonStatusText;

    public bool IsPatreonSectionVisible => IsPatreonGated;

    /// <summary>
    /// Tier checkboxes for the signed-in author's campaign. Empty if the author hasn't
    /// signed in yet — the view shows a "Sign in to Patreon to load tiers" hint instead.
    /// </summary>
    public ObservableCollection<PatreonTierSelection> PatreonTierSelections { get; } = [];

    /// <summary>
    /// Filenames of attachments the most recent Validate call discovered on the Patreon
    /// post. Populated by <see cref="ValidatePatreonPostAsync"/>. Empty until Validate is
    /// run successfully.
    /// </summary>
    public ObservableCollection<string> PatreonAttachmentFileNames { get; } = [];

    /// <summary>
    /// The filename in <see cref="PatreonAttachmentFileNames"/> the author picked for this
    /// release. Manager uses it at install time to fetch the right file out of a post that
    /// has multiple attachments. Null/empty when the post only has one attachment (manager
    /// falls back to the first).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPatreonAttachmentDropdownVisible))]
    private string? _selectedPatreonAttachmentFileName;

    /// <summary>
    /// Show the dropdown only after Validate has populated at least one attachment. Avoids
    /// the empty ComboBox showing up before the author has clicked Validate.
    /// </summary>
    public bool IsPatreonAttachmentDropdownVisible => PatreonAttachmentFileNames.Count > 0;

    private readonly PatreonAuthorService _patreonAuthor;
    private string? _resolvedCampaignId;

    public Action? CloseDialog { get; set; }
    public ModRelease? Result { get; private set; }

    private readonly ServerUploadService _serverUpload;

    public ReleaseDialogViewModel(
        string gameId,
        string gameDisplayName,
        string pluginId,
        string projectPath,
        string? initialSourceRepo,
        ObservableCollection<string> availableGitHubRepos,
        Sha256HashService hashService,
        GitHubService gitHubService,
        AuthorConfigService configService,
        PatreonAuthorService patreonAuthor,
        ServerUploadService serverUpload,
        ILogger logger,
        Action<string, string> showInfoDialog,
        Func<string, string, string?, string?> browseForFile,
        Func<string, string?> showBuildPackageDialog,
        ModRelease? existingRelease = null)
    {
        _gameId = gameId;
        GameDisplayName = gameDisplayName;
        _pluginId = pluginId;
        _projectPath = projectPath;
        _sourceRepo = initialSourceRepo ?? "";
        AvailableGitHubRepos = availableGitHubRepos;
        _hashService = hashService;
        _gitHubService = gitHubService;
        _configService = configService;
        _patreonAuthor = patreonAuthor;
        _serverUpload = serverUpload;
        _logger = logger;
        _showInfoDialog = showInfoDialog;
        _browseForFile = browseForFile;
        _showBuildPackageDialog = showBuildPackageDialog;

        IsServerUploadConfigured = _configService.GetServerUploadConfig() != null;

        if (existingRelease != null)
        {
            IsEditingExistingRelease = true;
            _version = existingRelease.Version;
            _channel = existingRelease.Channel;
            _sha256 = existingRelease.Sha256;
            _packageUrl = existingRelease.PackageUrl?.AbsoluteUri;
            _changelogUrl = existingRelease.ChangelogUrl;
            _releaseNotes = existingRelease.Notes;
            _previousAutoTag = $"v{existingRelease.Version}";
            _tagName = _previousAutoTag;

            if (existingRelease.Patreon is { } gate)
            {
                _isPatreonGated = true;
                _resolvedCampaignId = gate.CampaignId;
                _patreonPostUrl = $"https://www.patreon.com/posts/{gate.PostId}";

                // Seed the dropdown so the editor isn't empty before Validate runs again.
                // The actual attachment list refreshes if the user clicks Validate.
                if (!string.IsNullOrEmpty(gate.AttachmentFileName))
                {
                    PatreonAttachmentFileNames.Add(gate.AttachmentFileName);
                    _selectedPatreonAttachmentFileName = gate.AttachmentFileName;
                }
            }
        }

        // Re-render Patreon UI when the user signs in / out from elsewhere (toolbar button
        // in the main editor, etc.) while this dialog is open.
        _patreonAuthor.StateChanged += OnPatreonStateChanged;

        RefreshPatreonTiers(existingRelease?.Patreon?.TierIds);
    }

    private void OnPatreonStateChanged()
    {
        PatreonStateBumper = !PatreonStateBumper;
        RefreshPatreonTiers();
    }

    /// <summary>
    /// In-dialog sign-in path: the user can sign in to Patreon without leaving the release
    /// dialog. Same OAuth flow as the toolbar button — they go to the same Patreon account.
    /// </summary>
    [RelayCommand]
    private async Task SignInToPatreonInDialogAsync()
    {
        try
        {
            await _patreonAuthor.SignInAsync(CancellationToken.None);
            // OnPatreonStateChanged fires via the StateChanged event and re-renders.
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Patreon sign-in from release dialog failed");
            _showInfoDialog("Patreon sign-in failed", ex.Message);
        }
    }

    /// <summary>
    /// Build the tier-checkbox list from whatever the AuthorTool currently knows about the
    /// signed-in author's campaign. Re-runs after sign-in / sign-out so the dialog updates.
    /// Pre-checks any tiers the existing release was already gated to.
    /// </summary>
    public void RefreshPatreonTiers(IReadOnlyList<string>? preselected = null)
    {
        PatreonTierSelections.Clear();
        var camp = _patreonAuthor.OwnCampaign;
        if (camp == null)
        {
            PatreonStatusText = _patreonAuthor.IsSignedIn
                ? "Couldn't load your campaign — try refreshing."
                : "Sign in to Patreon (in Settings) to load your tier list.";
            return;
        }
        _resolvedCampaignId = camp.CampaignId;
        PatreonStatusText = $"Showing tiers from {camp.DisplayName}.";
        var pre = new HashSet<string>(preselected ?? Array.Empty<string>());
        foreach (var tier in camp.Tiers)
        {
            PatreonTierSelections.Add(new PatreonTierSelection(tier.Id, tier.DisplayLabel, pre.Contains(tier.Id)));
        }
    }

    [RelayCommand]
    private async Task ValidatePatreonPostAsync()
    {
        if (string.IsNullOrWhiteSpace(PatreonPostUrl))
        {
            _showInfoDialog("Paste a Patreon post URL", "Paste the URL of the Patreon post the wrapped ZIP is attached to.");
            return;
        }
        try
        {
            var (attachments, debugFile) = await _patreonAuthor.ValidatePostUrlAsync(PatreonPostUrl, CancellationToken.None);
            if (attachments.Count == 0)
            {
                PatreonStatusText = debugFile != null
                    ? $"Read the post but found no attachments to pick from. The raw API response was saved to {debugFile} so we can debug — open it in a text editor and share its contents."
                    : "Couldn't read that Patreon post, or it has no attachments yet. Check the URL, your sign-in, or upload the wrapped ZIP to the post first.";
                return;
            }

            // Refresh the dropdown with the post's actual attachment list. Author always
            // picks the right file themselves — no auto-matching by version / asset name /
            // anything else. The only thing we preserve is an existing selection (saved
            // release or earlier pick this session) if it still exists in the new list,
            // since clearing it on re-validate would be lossy.
            var previous = SelectedPatreonAttachmentFileName;
            PatreonAttachmentFileNames.Clear();
            foreach (var a in attachments)
            {
                if (!string.IsNullOrEmpty(a.FileName))
                    PatreonAttachmentFileNames.Add(a.FileName);
            }
            OnPropertyChanged(nameof(IsPatreonAttachmentDropdownVisible));

            SelectedPatreonAttachmentFileName =
                !string.IsNullOrEmpty(previous) && PatreonAttachmentFileNames.Contains(previous)
                    ? previous
                    : null;

            // Q3=C with Ola's tweak: validation reports success quietly when the asset is
            // present, only surfaces wording when something looks off. Phrase status as
            // factual count + first-tier-count rather than a warning.
            var firstAttachmentTierCount = attachments[0].RequiredTierIds.Count;
            PatreonStatusText = $"Validated. Post has {attachments.Count} attachment(s), locked to {firstAttachmentTierCount} tier(s). Pick the file for this release in the dropdown below.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Patreon post validation failed");
            PatreonStatusText = $"Validation failed: {ex.Message}";
        }
    }

    partial void OnVersionChanged(string? value)
    {
        // Auto-fill the tag from version unless the user has manually overridden it.
        if (string.IsNullOrWhiteSpace(TagName) || TagName == _previousAutoTag)
        {
            _previousAutoTag = string.IsNullOrWhiteSpace(value) ? null : $"v{value}";
            TagName = _previousAutoTag;
        }
    }

    partial void OnTagNameChanged(string? value)
    {
        RecomputeAutoChangelog();
        RecomputeAutoPackageUrl();
    }

    partial void OnSourceRepoChanged(string value)
    {
        RecomputeAutoChangelog();
        RecomputeAutoPackageUrl();
    }

    partial void OnAssetFileNameChanged(string? value) => RecomputeAutoPackageUrl();

    /// <summary>
    /// The changelog URL points to the release notes the user reads when deciding to update.
    /// The natural source is the GitHub release page itself — once the user has picked a repo
    /// and the tag is filled in, we can build it deterministically. If the user manually edits
    /// the changelog field, we stop overwriting it.
    /// </summary>
    private void RecomputeAutoChangelog()
    {
        var current = ChangelogUrl;
        if (!string.IsNullOrEmpty(current) && current != _previousAutoChangelog)
            return; // user-edited

        if (string.IsNullOrWhiteSpace(SourceRepo) || string.IsNullOrWhiteSpace(TagName))
        {
            _previousAutoChangelog = null;
            ChangelogUrl = null;
            return;
        }

        var url = $"https://github.com/{SourceRepo}/releases/tag/{TagName}";
        _previousAutoChangelog = url;
        ChangelogUrl = url;
    }

    /// <summary>
    /// GitHub release-asset URLs are fully deterministic — once the user has picked a repo, a
    /// tag, and an asset filename, we know exactly where the asset will live after upload.
    /// Pre-filling the field saves the user from copy-pasting it after the upload completes,
    /// and lets "Save without upload" work for an already-uploaded asset without retyping. We
    /// stop overwriting if the user edits the value manually.
    /// </summary>
    private void RecomputeAutoPackageUrl()
    {
        var current = PackageUrl;
        if (!string.IsNullOrEmpty(current) && current != _previousAutoPackageUrl)
            return; // user-edited

        if (string.IsNullOrWhiteSpace(SourceRepo) ||
            string.IsNullOrWhiteSpace(TagName) ||
            string.IsNullOrWhiteSpace(AssetFileName))
        {
            _previousAutoPackageUrl = null;
            PackageUrl = null;
            return;
        }

        var url = GitHubService.BuildAssetUrl(SourceRepo, TagName!, AssetFileName!).AbsoluteUri;
        _previousAutoPackageUrl = url;
        PackageUrl = url;
    }

    [RelayCommand]
    private async Task PickZipAsync()
    {
        var path = _browseForFile("Select wrapped mod ZIP", "Mod packages (*.zip)|*.zip|All files (*.*)|*.*", null);
        if (string.IsNullOrEmpty(path)) return;
        await AdoptBuiltZipAsync(path);
    }

    [RelayCommand]
    private async Task BuildPackageAsync()
    {
        var version = string.IsNullOrWhiteSpace(Version) ? "" : Version!.Trim();
        var resultPath = _showBuildPackageDialog(version);
        if (string.IsNullOrEmpty(resultPath)) return;
        await AdoptBuiltZipAsync(resultPath);
    }

    private async Task AdoptBuiltZipAsync(string path)
    {
        IsBusy = true;
        StatusMessage = $"Computing SHA256 of {Path.GetFileName(path)}...";
        try
        {
            Sha256 = await _hashService.ComputeAsync(path);
            LocalZipPath = path;
            // Always overwrite the asset filename when a fresh build comes in — we want the
            // new versioned filename to take effect rather than keeping a stale one.
            AssetFileName = Path.GetFileName(path);
            StatusMessage = $"SHA256: {Sha256}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to compute SHA256 of {Path}", path);
            _showInfoDialog("Hash failed", ex.Message);
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UploadAndSaveAsync()
    {
        var error = ValidateForUpload();
        if (error != null)
        {
            _showInfoDialog("Cannot save release", error);
            return;
        }

        IsBusy = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(LocalZipPath))
            {
                if (string.IsNullOrWhiteSpace(SourceRepo))
                {
                    _showInfoDialog("GitHub repo missing",
                        $"Set the GitHub repo for '{GameDisplayName}' first (e.g. RealAmethyst/DigimonNOAccess) so we know where to upload.");
                    return;
                }

                if (!await _gitHubService.IsAvailableAsync() || !await _gitHubService.IsAuthenticatedAsync())
                {
                    _showInfoDialog("GitHub CLI required",
                        "Uploading a release requires the 'gh' CLI to be installed and signed in. " +
                        "Install from https://cli.github.com/ then run 'gh auth login'.");
                    return;
                }

                var tag = string.IsNullOrWhiteSpace(TagName) ? $"v{Version}" : TagName!;
                var assetFileName = string.IsNullOrWhiteSpace(AssetFileName)
                    ? Path.GetFileName(LocalZipPath)
                    : AssetFileName!;

                var stagingPath = LocalZipPath!;
                if (!string.Equals(Path.GetFileName(stagingPath), assetFileName, StringComparison.OrdinalIgnoreCase))
                {
                    var dir = Path.GetDirectoryName(stagingPath) ?? Path.GetTempPath();
                    var renamed = Path.Combine(dir, assetFileName);
                    if (File.Exists(renamed)) File.Delete(renamed);
                    File.Copy(stagingPath, renamed);
                    stagingPath = renamed;
                }

                StatusMessage = $"Uploading {assetFileName} to {SourceRepo} {tag}...";
                var existingReleases = await _gitHubService.ListReleasesAsync(SourceRepo);
                var hasTag = existingReleases.Any(r => r.TagName == tag);

                var notes = string.IsNullOrWhiteSpace(ReleaseNotes)
                    ? $"Release {tag} for the Accessibility Mod Manager."
                    : ReleaseNotes!;

                ProcessResult result;
                if (hasTag)
                {
                    result = await _gitHubService.UploadReleaseAssetAsync(SourceRepo, tag, stagingPath, clobber: true);
                    if (result.Success && !string.IsNullOrWhiteSpace(ReleaseNotes))
                    {
                        // Tag already existed; refresh its release notes too.
                        var editResult = await _gitHubService.EditReleaseNotesAsync(SourceRepo, tag, notes);
                        if (!editResult.Success)
                            _logger.Warning("Asset uploaded but release notes edit failed: {Output}", editResult.Combined);
                    }
                }
                else
                {
                    result = await _gitHubService.CreateReleaseAsync(
                        SourceRepo, tag,
                        title: tag,
                        notes: notes,
                        new[] { stagingPath });
                }

                if (!result.Success)
                {
                    _showInfoDialog("Upload failed",
                        $"GitHub release upload failed:\n\n{result.Combined}");
                    return;
                }

                PackageUrl = GitHubService.BuildAssetUrl(SourceRepo, tag, assetFileName).AbsoluteUri;
                _configService.SetGameSourceRepo(_projectPath, _gameId, SourceRepo);
                StatusMessage = $"Uploaded. URL: {PackageUrl}";
            }

            BuildResult();
            CloseDialog?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Upload failed for {Game}", _gameId);
            _showInfoDialog("Upload error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveWithoutUploadAsync()
    {
        var error = ValidateForUrlOnly();
        if (error != null)
        {
            _showInfoDialog("Cannot save release", error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(SourceRepo))
            _configService.SetGameSourceRepo(_projectPath, _gameId, SourceRepo);

        BuildResult();

        // Patreon-gated releases get auto-uploaded to the author's download server when one
        // is configured (see AUTOMATED_RELEASE_UPLOAD.md). The upload writes the wrapped ZIP
        // and a fresh gate.json to /var/www/mod-server/releases/<gameId>/<version>/ on the
        // VPS over SFTP, then sets Patreon.ServerUrl on the release so the manager knows
        // where to fetch from. If no server is configured, the release saves with no
        // ServerUrl and the manager falls back to the file-picker flow as before.
        if (Result?.Patreon != null && !string.IsNullOrEmpty(LocalZipPath))
        {
            var serverCfg = _configService.GetServerUploadConfig();
            if (serverCfg != null)
            {
                IsBusy = true;
                StatusMessage = $"Uploading to {serverCfg.Host}...";
                try
                {
                    await _serverUpload.UploadReleaseAsync(
                        serverCfg, _gameId, Result.Version, LocalZipPath, Result.Patreon, CancellationToken.None);

                    var fileName = Path.GetFileName(LocalZipPath);
                    var url = ServerUploadService.BuildPublicUrl(serverCfg, _gameId, Result.Version, fileName);
                    Result = new ModRelease
                    {
                        GameId = Result.GameId,
                        PluginId = Result.PluginId,
                        Version = Result.Version,
                        Channel = Result.Channel,
                        PackageUrl = Result.PackageUrl,
                        Sha256 = Result.Sha256,
                        ChangelogUrl = Result.ChangelogUrl,
                        Notes = Result.Notes,
                        Compatibility = Result.Compatibility,
                        Patreon = new PatreonGate
                        {
                            CampaignId = Result.Patreon.CampaignId,
                            TierIds = Result.Patreon.TierIds,
                            PostId = Result.Patreon.PostId,
                            AttachmentFileName = Result.Patreon.AttachmentFileName,
                            ServerUrl = url
                        }
                    };
                    StatusMessage = $"Uploaded to {url}";
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Server upload failed for {Game} v{Version}", _gameId, Result.Version);
                    _showInfoDialog("Server upload failed",
                        $"Couldn't upload the wrapped ZIP to {serverCfg.Host}:\n\n{ex.Message}\n\n" +
                        "The release is not yet saved. Fix the issue and try Save again, or cancel " +
                        "to discard the release.");
                    StatusMessage = "Upload failed.";
                    return;
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        CloseDialog?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseDialog?.Invoke();
    }

    private void BuildResult()
    {
        PatreonGate? gate = null;
        if (IsPatreonGated)
        {
            var selectedTierIds = PatreonTierSelections.Where(t => t.IsSelected).Select(t => t.TierId).ToList();
            if (selectedTierIds.Count == 0)
                throw new InvalidOperationException(
                    "Pick at least one Patreon tier that grants access to this release.");
            if (string.IsNullOrEmpty(_resolvedCampaignId))
                throw new InvalidOperationException(
                    "Couldn't resolve your Patreon campaign id. Sign in to Patreon and refresh tiers, then try again.");

            string? postId = null;
            string? attachmentFileName = null;
            if (!IsServerUploadConfigured)
            {
                // Patreon-post-as-CDN flow — author manually attached the ZIP to a tier-locked
                // post and the manager will open the post in the patron's browser as the
                // file-picker fallback path.
                postId = PatreonAuthorService.ExtractPostId(PatreonPostUrl ?? "");
                if (string.IsNullOrEmpty(postId))
                    throw new InvalidOperationException(
                        "Patreon post URL is missing or invalid. Paste the URL of the post your wrapped ZIP is attached to.");
                attachmentFileName = string.IsNullOrWhiteSpace(SelectedPatreonAttachmentFileName)
                    ? null
                    : SelectedPatreonAttachmentFileName!.Trim();
            }

            gate = new PatreonGate
            {
                CampaignId = _resolvedCampaignId,
                TierIds = selectedTierIds,
                PostId = postId,
                AttachmentFileName = attachmentFileName
            };
        }

        Result = new ModRelease
        {
            GameId = _gameId,
            PluginId = _pluginId,
            Version = Version!,
            Channel = Channel ?? "stable",
            // Patreon-gated releases don't carry a public URL — the manager resolves the
            // attachment URL via the Patreon API at install time.
            PackageUrl = gate is null ? new Uri(PackageUrl!) : null,
            Sha256 = Sha256!,
            ChangelogUrl = string.IsNullOrWhiteSpace(ChangelogUrl) ? null : ChangelogUrl,
            Notes = string.IsNullOrWhiteSpace(ReleaseNotes) ? null : ReleaseNotes,
            Patreon = gate
        };
    }

    private string? ValidateForUpload()
    {
        if (string.IsNullOrWhiteSpace(Version)) return "Version is required.";
        if (string.IsNullOrWhiteSpace(Channel)) return "Channel is required.";
        if (string.IsNullOrWhiteSpace(LocalZipPath) && string.IsNullOrWhiteSpace(PackageUrl))
            return "Pick a wrapped ZIP to upload, or paste a public URL and use 'Save without upload'.";
        if (!string.IsNullOrWhiteSpace(PackageUrl) && string.IsNullOrWhiteSpace(Sha256))
            return "If you supply a URL directly, you must also fill in the SHA256.";
        if (!string.IsNullOrWhiteSpace(PackageUrl) && !PackageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "Package URL must use https://.";
        return null;
    }

    private string? ValidateForUrlOnly()
    {
        if (string.IsNullOrWhiteSpace(Version)) return "Version is required.";
        if (string.IsNullOrWhiteSpace(Channel)) return "Channel is required.";

        // Patreon-gated releases don't need a public URL — the manager resolves the asset
        // via the Patreon API or the author's download server. SHA256 is still required
        // since the manager verifies the downloaded bytes against it. The post-pick step
        // only matters when we're using the Patreon-post-as-CDN fallback.
        if (IsPatreonGated)
        {
            if (string.IsNullOrWhiteSpace(Sha256))
                return "SHA256 is required (the manager verifies the served file against it).";
            if (!IsServerUploadConfigured && string.IsNullOrWhiteSpace(SelectedPatreonAttachmentFileName))
                return "Click Validate on the Patreon post URL, then pick which attachment belongs to this release in the dropdown.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(PackageUrl))
            return "Public URL is required for save-without-upload.";
        if (!PackageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "Public URL must use https://.";
        if (string.IsNullOrWhiteSpace(Sha256))
            return "SHA256 is required.";
        return null;
    }
}

/// <summary>One row in the per-release Patreon tier checkbox list.</summary>
public sealed partial class PatreonTierSelection : ObservableObject
{
    public string TierId { get; }
    public string DisplayLabel { get; }

    [ObservableProperty]
    private bool _isSelected;

    public PatreonTierSelection(string tierId, string displayLabel, bool isSelected)
    {
        TierId = tierId;
        DisplayLabel = displayLabel;
        _isSelected = isSelected;
    }

    // Without this override, screen readers fall back to "AccessibilityModManager.AuthorTool
    // .ViewModels.PatreonTierSelection" when they encounter the data item before the
    // CheckBox inside the template gets focus. The label is what the user actually wants to
    // hear.
    public override string ToString() => DisplayLabel;
}
