using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Patreon;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.AuthorTool.ViewModels;

/// <summary>
/// A change to what the download server enforces for an ALREADY-PUBLISHED release: new tiers, or
/// (when <paramref name="Gate"/> is null) no tier lock at all.
/// <para>
/// It is carried out of the release dialog rather than applied there because entitlement changes
/// have to be sequenced with the catalog. Loosening enforcement before the index says so exposes
/// patrons-only bytes; tightening it before the index says so turns away people the live catalog
/// still tells to download. Applied once the public index matches, both directions are consistent
/// at every moment, and a declined publish simply leaves the server as the live catalog describes
/// it. (A gate that ships WITH new bytes is different and stays inline — it must be in force
/// before the bytes it protects exist.)
/// </para>
/// </summary>
/// <param name="PublicUrl">
/// Where the release will be downloadable once the lock is off — so the editor can confirm the
/// address actually serves it, which is the one check a still-gated release can't do for itself.
/// Null when the change doesn't make anything public.
/// </param>
public sealed record PendingGateChange(
    string GameId, string Version, PatreonGate? Gate, string? PublicUrl = null);

/// <summary>
/// What the release dialog hands back: the release to file in the index, plus any server-side
/// work that must wait until the catalog agrees with it.
/// </summary>
public sealed record ReleaseDialogResult(ModRelease Release, PendingGateChange? GateChange);

public sealed partial class ReleaseDialogViewModel : ObservableObject
{
    /// <summary>Reads back what's already published, to compare it with what's about to be.</summary>
    private static readonly System.Net.Http.HttpClient PublishedAssetHttp = new();

    private readonly Sha256HashService _hashService;
    private readonly GitHubService _gitHubService;
    private readonly AuthorConfigService _configService;
    private readonly ILogger _logger;
    private readonly Action<string, string> _showInfoDialog;
    private readonly Func<string, string, bool> _confirmDialog;
    private readonly Func<string, string, string?, string?> _browseForFile;
    private readonly Func<string, string?> _showBuildPackageDialog;
    private readonly string _pluginId;
    private readonly string _projectPath;
    private readonly string _gameId;
    private string? _previousAutoTag;
    private string? _previousAutoChangelog;
    private string? _previousAutoPackageUrl;

    /// <summary>
    /// Download-server URL produced by a successful publish in this dialog session. Takes
    /// precedence over <see cref="_existingServerUrl"/> when the release record is built.
    /// </summary>
    private string? _freshServerUrl;

    /// <summary>
    /// ServerUrl carried over from the release being edited. The download-server link lives
    /// only on the existing <see cref="PatreonGate.ServerUrl"/> — it isn't surfaced as an
    /// editable field — so we stash it here at construction and re-stamp it onto the rebuilt
    /// gate in <see cref="BuildResult"/>. That keeps the link intact when the author edits
    /// metadata (e.g. just the changelog) without re-uploading. A fresh upload in
    /// <see cref="SaveWithoutUploadAsync"/> recomputes and overwrites it. Null for new releases.
    /// </summary>
    private string? _existingServerUrl;

    /// <summary>
    /// Version this dialog opened on, for an edit. A package's manifest declares its own version
    /// and the manager refuses an install where the two disagree, so changing the version while
    /// keeping the old package is not a metadata edit — it's a broken release.
    /// </summary>
    private readonly string? _existingVersion;

    /// <summary>The gate this release opened with, so a tier-only change can be detected.</summary>
    private readonly PatreonGate? _existingGate;

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
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    /// <summary>
    /// Drives IsEnabled on the form and the save buttons. While a publish is in flight the
    /// author must not be able to edit the version or fingerprint the upload was based on, or
    /// start a second save on top of the first.
    /// </summary>
    public bool IsNotBusy => !IsBusy;

    /// <summary>
    /// What was actually published, captured before the upload started. The saved release is
    /// built from this, never from form fields that could have changed underneath it.
    /// </summary>
    private sealed record PublishedIdentity(string Version, string Sha256);

    private PublishedIdentity? _published;

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
    [NotifyPropertyChangedFor(nameof(IsHostingSelectorVisible))]
    [NotifyPropertyChangedFor(nameof(IsHostingHintVisible))]
    [NotifyPropertyChangedFor(nameof(IsHostingOnGitHub))]
    [NotifyPropertyChangedFor(nameof(IsHostingOnServer))]
    [NotifyPropertyChangedFor(nameof(IsPublicServerInfoVisible))]
    [NotifyPropertyChangedFor(nameof(UploadButtonAutomationName))]
    private bool _isPatreonGated;

    /// <summary>
    /// True when the global server-upload settings are filled in. Captured at dialog
    /// construction; toggling settings while the dialog is open isn't supported (rare,
    /// and reopening the dialog re-reads).
    /// </summary>
    public bool IsServerUploadConfigured { get; }

    // ----- Public-release hosting destination -----

    /// <summary>
    /// Selected hosting destination for public (non-Patreon) releases. Bound to a ComboBox
    /// with the two known labels; persisted only for the lifetime of the dialog. Defaults
    /// to "GitHub" so existing authors see the same form they always have when they open
    /// the dialog. Only meaningful when <see cref="IsPublicRelease"/> AND
    /// <see cref="IsServerUploadConfigured"/> — otherwise the selector is hidden and GitHub
    /// is the implicit destination.
    /// </summary>
    public const string HostingGitHub = "GitHub";
    public const string HostingMyServer = "My server";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHostingOnGitHub))]
    [NotifyPropertyChangedFor(nameof(IsHostingOnServer))]
    [NotifyPropertyChangedFor(nameof(IsPublicServerInfoVisible))]
    [NotifyPropertyChangedFor(nameof(SubtitleText))]
    [NotifyPropertyChangedFor(nameof(UploadButtonAutomationName))]
    private string _hostingDestination = HostingGitHub;

    /// <summary>
    /// The "Host on:" ComboBox only appears when a server is configured AND the release is
    /// public — there's nothing to pick otherwise (Patreon releases use the server when
    /// configured, the Patreon post otherwise; public releases without a server have only
    /// GitHub as an option).
    /// </summary>
    public bool IsHostingSelectorVisible => IsPublicRelease && IsServerUploadConfigured;

    /// <summary>
    /// Shown in the selector's place when there's no server configured. A control that simply
    /// isn't rendered is invisible to a screen reader — there's no way to discover that the
    /// choice exists, or what to do to get it — so the absence explains itself instead.
    /// </summary>
    public bool IsHostingHintVisible => IsPublicRelease && !IsServerUploadConfigured;

    public string HostingHintText =>
        "Hosting on GitHub. To host releases on your own server instead, close this dialog and " +
        "fill in Server settings, then add the release again.";

    /// <summary>
    /// True when this public release will be uploaded to / served from GitHub. Used to
    /// drive visibility of GitHub-specific form fields (repo, tag).
    /// </summary>
    public bool IsHostingOnGitHub => IsPublicRelease && HostingDestination == HostingGitHub;

    /// <summary>
    /// True when this public release will be uploaded to / served from the author's
    /// download server instead of GitHub. Drives the inline info note and the auto-computed
    /// URL path.
    /// </summary>
    public bool IsHostingOnServer => IsPublicRelease && HostingDestination == HostingMyServer;

    /// <summary>
    /// Friendly inline note explaining what happens on Save / Upload when the author has
    /// picked their own server as the destination for a public release.
    /// </summary>
    public bool IsPublicServerInfoVisible => IsHostingOnServer;

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
            : "Build the wrapped ZIP, then upload it manually to a tier-locked Patreon post. Entitled patrons download it from the post in their browser and pick the file in the manager (SHA256-checked).")
        : (IsHostingOnServer
            ? "Build the wrapped ZIP. The AuthorTool uploads it to your download server on Save; the manager fetches from there over plain HTTPS (no Patreon involved)."
            : "Fill in the version, pick the wrapped ZIP, then upload to GitHub or save the URL directly.");

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

    /// <summary>
    /// Accessibility label for the "Upload and save" button. Adapts to the destination so a
    /// screen reader user hears where the upload is going. Falls back to the generic GitHub
    /// wording for legacy behaviour when no server is configured.
    /// </summary>
    public string UploadButtonAutomationName => IsHostingOnServer
        ? "Upload to your download server and save release"
        : "Upload to GitHub and save release";

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

    /// <summary>
    /// Set when this save changes what the server enforces for a release whose package is already
    /// published — new tiers, or no lock at all. The index editor applies it after the public
    /// catalog has switched. See <see cref="PendingGateChange"/> for why it can't happen here.
    /// </summary>
    public PendingGateChange? GateChange { get; private set; }

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
        Func<string, string, bool> confirmDialog,
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
        _confirmDialog = confirmDialog;
        _browseForFile = browseForFile;
        _showBuildPackageDialog = showBuildPackageDialog;

        IsServerUploadConfigured = _configService.GetServerUploadConfig() != null;

        if (existingRelease != null)
        {
            IsEditingExistingRelease = true;
            _existingVersion = existingRelease.Version;
            _existingGate = existingRelease.Patreon;
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
                _existingServerUrl = gate.ServerUrl;

                // Seed the dropdown so the editor isn't empty before Validate runs again.
                // The actual attachment list refreshes if the user clicks Validate.
                if (!string.IsNullOrEmpty(gate.AttachmentFileName))
                {
                    PatreonAttachmentFileNames.Add(gate.AttachmentFileName);
                    _selectedPatreonAttachmentFileName = gate.AttachmentFileName;
                }
            }
            else if (IsServerUploadConfigured && _packageUrl != null)
            {
                // Re-opening a public release that was previously uploaded to the author's
                // server: the URL should start with the configured PublicBaseUrl. Detect that
                // so the dialog reopens with "My server" preselected and the GitHub-specific
                // fields stay hidden. Pure heuristic — if the author moved their server or
                // edited the URL by hand the detection silently falls back to GitHub mode,
                // which only affects the *default* selector value (everything else is still
                // editable).
                var cfg = _configService.GetServerUploadConfig();
                if (cfg != null && !string.IsNullOrEmpty(cfg.PublicBaseUrl) &&
                    _packageUrl.StartsWith(cfg.PublicBaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _hostingDestination = HostingMyServer;
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
        // Server-hosted public URLs embed the version directly (no tag indirection), so a
        // bare version change has to retrigger the URL recompute too. In GitHub mode the
        // cascade through OnTagNameChanged already covers this.
        RecomputeAutoPackageUrl();
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

    partial void OnHostingDestinationChanged(string value)
    {
        // Flipping between GitHub and server hosting changes which URL pattern is "the auto
        // value", so re-run both so the URL field tracks the new destination. The auto-fill
        // helpers preserve user-overrides — switching destinations after manually editing
        // the URL won't clobber the manual value.
        RecomputeAutoChangelog();
        RecomputeAutoPackageUrl();
    }

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

        // Only the GitHub destination has a deterministic public changelog URL we can derive
        // (the release page). Server-hosted public releases and Patreon-gated releases don't,
        // so clear any previous auto value and leave the field empty for the author to fill
        // in manually if they want.
        if (!IsHostingOnGitHub ||
            string.IsNullOrWhiteSpace(SourceRepo) ||
            string.IsNullOrWhiteSpace(TagName))
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

        string? url = null;
        if (IsHostingOnServer)
        {
            // Server-hosted public release: URL is fully determined by the configured server,
            // the gameId, the version, and the asset filename. Same pattern the manager hits
            // for Patreon-gated server uploads — minus the gate-check on the server side.
            var serverCfg = _configService.GetServerUploadConfig();
            if (serverCfg != null &&
                !string.IsNullOrWhiteSpace(Version) &&
                !string.IsNullOrWhiteSpace(AssetFileName))
            {
                url = ServerUploadService.BuildPublicUrl(serverCfg, _gameId, Version!.Trim(), AssetFileName!.Trim());
            }
        }
        else if (IsHostingOnGitHub)
        {
            if (!string.IsNullOrWhiteSpace(SourceRepo) &&
                !string.IsNullOrWhiteSpace(TagName) &&
                !string.IsNullOrWhiteSpace(AssetFileName))
            {
                url = GitHubService.BuildAssetUrl(SourceRepo, TagName!, AssetFileName!).AbsoluteUri;
            }
        }

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

    /// <summary>
    /// Shows the author the hash of the file they just picked or built. It's a preview, not the
    /// published value — <see cref="StageVerifyAndPublishAsync"/> re-hashes the exact bytes it
    /// publishes and overwrites <see cref="Sha256"/> with that, so a ZIP rebuilt after being
    /// picked can't leave a stale hash in the index.
    /// </summary>
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

        // Anything that can refuse the release has to refuse it BEFORE bytes go anywhere —
        // a public URL that 404s is better than a published file no index points at.
        if (!TryBuildGate(out var gate, out var gateError))
        {
            _showInfoDialog("Cannot save release", gateError!);
            return;
        }

        IsBusy = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(LocalZipPath) &&
                !await StageVerifyAndPublishAsync(gate, ChooseDestination(gate, uploadRequested: true)))
            {
                return;
            }

            BuildResult(gate);
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

        if (!TryBuildGate(out var gate, out var gateError))
        {
            _showInfoDialog("Cannot save release", gateError!);
            return;
        }

        if (!string.IsNullOrWhiteSpace(SourceRepo))
            _configService.SetGameSourceRepo(_projectPath, _gameId, SourceRepo);

        IsBusy = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(LocalZipPath))
            {
                if (!await StageVerifyAndPublishAsync(gate, ChooseDestination(gate, uploadRequested: false)))
                    return;
            }
            else if (!await QueueServerGateChangeAsync(gate))
            {
                return;
            }

            BuildResult(gate);
            CloseDialog?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Save failed for {Game}", _gameId);
            _showInfoDialog("Save error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Where the wrapped ZIP is going, if anywhere, on this save.</summary>
    private enum PublishDestination
    {
        /// <summary>Nothing is uploaded — the file is still opened, hashed and checked.</summary>
        None,
        Server,
        GitHub
    }

    /// <summary>
    /// Decides where the ZIP goes, and it is the ONLY place that decides. A Patreon-gated
    /// release can only ever go to the author's own download server, which enforces the tier
    /// check — never to a public GitHub release. The "Upload and save" button is already hidden
    /// for gated releases, but a hidden button is a UI detail, not a boundary, and the mistake
    /// it would prevent is publishing patrons-only files in the open.
    /// <para>
    /// Gated releases with no server configured, and public releases saved with the "without
    /// upload" button, publish nothing here: the author uploads the file themselves (to a
    /// tier-locked Patreon post, or wherever the URL they typed points).
    /// </para>
    /// </summary>
    private PublishDestination ChooseDestination(PatreonGate? gate, bool uploadRequested)
    {
        if (gate != null)
        {
            // See AUTOMATED_RELEASE_UPLOAD.md: the wrapped ZIP and a fresh gate.json go to
            // /var/www/mod-server/releases/<gameId>/<version>/ over SFTP, and the resulting URL
            // is stamped onto Patreon.ServerUrl so the manager knows where to fetch from.
            return IsServerUploadConfigured ? PublishDestination.Server : PublishDestination.None;
        }

        if (!uploadRequested) return PublishDestination.None;

        return IsHostingOnServer ? PublishDestination.Server : PublishDestination.GitHub;
    }

    /// <summary>
    /// The single path every release takes to the outside world (audit finding 37). It stages a
    /// private copy of the wrapped ZIP under the asset's published filename, holds that copy
    /// open for the whole operation, and from that one handle: hashes it, opens it as a ZIP and
    /// checks its manifest the way the manager will, and streams it to the destination. The
    /// hash published in the index is therefore the hash of the exact bytes that were
    /// published — not of whatever the file happened to contain when it was picked.
    /// Returns false when the author should stay in the dialog; every such path has already
    /// shown them a dialog explaining why.
    /// </summary>
    private async Task<bool> StageVerifyAndPublishAsync(PatreonGate? gate, PublishDestination destination)
    {
        var version = Version!.Trim();
        var sourceName = Path.GetFileName(LocalZipPath!);

        // Copying and hashing a wrapped ZIP is real I/O — off the UI thread, or the window
        // freezes and takes the screen reader's feedback with it.
        StagedPackage staged;
        try
        {
            StatusMessage = $"Preparing {sourceName}...";
            staged = await Task.Run(() => StagedPackage.Create(LocalZipPath!, AssetFileName));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Staging the wrapped ZIP failed for {Game} v{Version}", _gameId, version);
            _showInfoDialog("Can't read the wrapped ZIP", ex.Message);
            return false;
        }

        using (staged)
        {
            StatusMessage = $"Checking {staged.FileName}...";
            var report = await Task.Run(() => PluginPackageValidation.Validate(
                staged.Stream, _pluginId, _gameId, version, _logger));
            if (!report.IsValid)
            {
                _showInfoDialog("The package won't install",
                    "The manager would refuse this ZIP on the user's machine:\n\n" +
                    string.Join("\n\n", report.Errors));
                return false;
            }

            // The authoritative identity: the hash comes off the held handle, after the manifest
            // check, right before the bytes are published — and it is recorded here so the saved
            // release describes THIS package even if the form is edited later.
            Sha256 = staged.Sha256;
            AssetFileName = staged.FileName;
            _published = new PublishedIdentity(version, staged.Sha256);

            var published = destination switch
            {
                PublishDestination.Server => await PublishToServerAsync(staged, version, gate),
                PublishDestination.GitHub => await PublishToGitHubAsync(staged, version),
                _ => true
            };
            if (!published) return false;

            if (destination == PublishDestination.None)
                StatusMessage = $"Checked {staged.FileName}. SHA256: {staged.Sha256}";

            return true;
        }
    }

    /// <summary>
    /// Publishes the staged ZIP to the author's own download server — the path both gated and
    /// public server-hosted releases take. Refuses, before uploading anything, to replace a
    /// version that is already live with different bytes, and asks first when saving a release
    /// as public would strip a Patreon gate that's currently in force.
    /// </summary>
    private async Task<bool> PublishToServerAsync(StagedPackage staged, string version, PatreonGate? gate)
    {
        var serverCfg = _configService.GetServerUploadConfig();
        if (serverCfg == null)
        {
            _showInfoDialog("Server upload not configured",
                "You don't have a download server configured. Open Settings → Server upload to add one, " +
                "or pick GitHub as the destination.");
            return false;
        }

        ServerUploadService.RemoteReleaseState state;
        try
        {
            StatusMessage = $"Checking what's already published on {serverCfg.Host}...";
            state = await _serverUpload.ProbeReleaseAsync(
                serverCfg, _gameId, version, staged.FileName, staged.Stream, staged.Sha256,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Couldn't read the published state for {Game} v{Version}", _gameId, version);
            _showInfoDialog("Couldn't reach your server",
                $"Nothing was uploaded — reading what's already published on {serverCfg.Host} failed:\n\n{ex.Message}");
            return false;
        }

        if (state.OtherAssets.Count > 0)
        {
            _showInfoDialog("That version folder already holds another file",
                $"Version {version} on {serverCfg.Host} already contains {string.Join(", ", state.OtherAssets)}, " +
                $"which isn't the file you're publishing ({staged.FileName}).\n\n" +
                "A version folder holds exactly one package, because the Patreon tier lock applies to the " +
                "whole folder — a second file there would ride this release's lock, or lose its own when " +
                "this one goes public.\n\n" +
                "Bump the version number and publish that instead. Nothing was uploaded.");
            return false;
        }

        if (state.PackageExists && !state.PackageMatches)
        {
            _showInfoDialog("That version is already published",
                $"Version {version} is already live on {serverCfg.Host}, and this ZIP is a different file. " +
                "Published versions are never overwritten — anyone who already downloaded it would get a " +
                "hash mismatch on their next check.\n\n" +
                "Bump the version number and publish that instead. Nothing was uploaded.");
            return false;
        }

        if (gate == null && state.GateExists)
        {
            if (!_confirmDialog("Make this release public?",
                    $"Version {version} is currently locked to your Patreon tiers on the server. Saving it " +
                    "as a public release removes that lock, so anyone with the link can download it.\n\n" +
                    "The lock stays on until the updated catalog is live, so nothing is exposed before " +
                    "your index says the release is public.\n\n" +
                    "Remove the tier lock and publish it publicly?"))
            {
                StatusMessage = "Save cancelled — the release is still patrons-only on your server.";
                return false;
            }
        }

        try
        {
            StatusMessage = state.PackageMatches
                ? $"Confirming {staged.FileName} on {serverCfg.Host}..."
                : $"Uploading {staged.FileName} to {serverCfg.Host}...";

            var outcome = await _serverUpload.PublishReleaseAsync(
                serverCfg, _gameId, version, staged.FileName, staged.Stream, staged.Sha256,
                gate, CancellationToken.None);

            if (gate != null)
                _freshServerUrl = outcome.PublicUrl;
            else
                PackageUrl = outcome.PublicUrl;

            // Handed to the index editor, which removes the lock only after the public catalog
            // has switched to the version that says this release is open.
            if (outcome.GateRemovalPending)
                GateChange = new PendingGateChange(_gameId, version, null, outcome.PublicUrl);
            else if (outcome.GateChangePending)
                GateChange = new PendingGateChange(_gameId, version, gate);

            // For a public release, prove the address in the index actually serves these bytes.
            // Uploading over SFTP and building a URL from settings are two different things: a
            // web root that doesn't correspond to the upload path produces a confident publish
            // and a 404 for every user. (A gated release can't be checked this way — the server
            // would rightly turn us away — so its first real test is a patron's download. Nor can
            // one whose tier lock is still standing while the catalog catches up: it is public in
            // intent but correctly still gated on the wire.)
            if (gate == null && !outcome.GateRemovalPending)
            {
                StatusMessage = "Checking the public address serves it...";
                var (status, servedSha) = await TryHashPublishedAsync(new Uri(outcome.PublicUrl));
                if (status != PublishedProbe.Found)
                {
                    _showInfoDialog("Uploaded, but the public address doesn't serve it",
                        $"The file went up, but {outcome.PublicUrl} " +
                        (status == PublishedProbe.Absent ? "returned nothing." : "couldn't be read.") +
                        "\n\nIf it returned nothing, the public base URL and the remote releases path in " +
                        "Server upload settings most likely point at different places. The release hasn't " +
                        "been saved — check that and save again.");
                    StatusMessage = "Published, but not reachable at its public address.";
                    return false;
                }

                if (!string.Equals(servedSha, staged.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    _showInfoDialog("The public address serves different bytes",
                        $"{outcome.PublicUrl} returned a file that isn't the one just published. Something " +
                        "between the upload and the web server is serving stale or unrelated content.\n\n" +
                        "The release hasn't been saved — every user would fail the fingerprint check.");
                    StatusMessage = "Published, but the public address serves something else.";
                    return false;
                }
            }

            StatusMessage = DescribeOutcome(outcome);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Server publish failed for {Game} v{Version}", _gameId, version);
            _showInfoDialog("Publishing to your server failed",
                $"{ex.Message}\n\nThe release hasn't been saved. Fix the problem and try again, or cancel " +
                "to discard it.");
            StatusMessage = "Publish failed.";
            return false;
        }
    }

    private static string DescribeOutcome(ServerUploadService.ReleasePublishOutcome outcome)
    {
        var what = outcome.PackageUploaded
            ? "Uploaded"
            : "Already published with these exact bytes — nothing re-uploaded";
        if (outcome.GateRemovalPending) what += "; the tier lock comes off once the catalog is live";
        else if (outcome.GateChangePending) what += "; the new tiers apply once the catalog is live";
        else if (outcome.GateWritten) what += ", tier lock in place";
        return $"{what}. URL: {outcome.PublicUrl}";
    }

    /// <summary>
    /// Handles the saves where no new package is involved but what the server ENFORCES changes:
    /// the author edits which tiers unlock a release (the common case — it needs no rebuild), or
    /// makes a patrons-only release public without touching the file. Neither used to reach the
    /// server at all, so the index would promise one thing while the download server enforced
    /// another, permanently, with no amount of re-saving reconciling them.
    /// <para>
    /// The change is queued rather than applied, so it lands after the catalog agrees with it.
    /// Returns false only when the author declines making a release public.
    /// </para>
    /// </summary>
    private async Task<bool> QueueServerGateChangeAsync(PatreonGate? gate)
    {
        // Only meaningful for a release whose package is already on the author's own server.
        if (!IsServerUploadConfigured || string.IsNullOrEmpty(_existingServerUrl)) return true;

        var version = Version!.Trim();
        var serverCfg = _configService.GetServerUploadConfig();
        if (serverCfg == null) return true;

        if (gate == null)
        {
            // Ask the SERVER, not the local index. Once a "make it public" save has written the
            // gateless release to disk, the index no longer remembers there was a lock — so a
            // transition interrupted before the catalog went live would otherwise be invisible
            // for ever, leaving a public release that turns everyone away.
            bool lockStanding;
            try
            {
                lockStanding = await _serverUpload.GateExistsAsync(
                    serverCfg, _gameId, version, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Couldn't check the tier lock for {Game} v{Version}", _gameId, version);
                _showInfoDialog("Couldn't check your server",
                    $"Reading whether {version} still has a tier lock on {serverCfg.Host} failed:\n\n" +
                    $"{ex.Message}\n\nThe release hasn't been saved — otherwise your catalog could say it's " +
                    "public while your server keeps turning people away.");
                return false;
            }

            if (!lockStanding) return true;

            if (!_confirmDialog("Make this release public?",
                    $"Version {version} is currently locked to your Patreon tiers on the server. Saving it " +
                    "as a public release removes that lock, so anyone with the link can download it.\n\n" +
                    "The lock comes off once your updated index is live, so nothing is exposed before the " +
                    "catalog says the release is public.\n\n" +
                    "Remove the tier lock and publish it publicly?"))
            {
                StatusMessage = "Save cancelled — the release is still patrons-only on your server.";
                return false;
            }

            // The address the index will carry for this release — the one worth proving works.
            GateChange = new PendingGateChange(_gameId, version, null, PackageUrl);
            return true;
        }

        if (GateDiffersFromPublished(gate))
            GateChange = new PendingGateChange(_gameId, version, gate);

        return true;
    }

    /// <summary>
    /// True when the tiers or campaign differ from what this release opened with. Tier order
    /// isn't meaningful, so the comparison ignores it.
    /// </summary>
    private bool GateDiffersFromPublished(PatreonGate gate) =>
        _existingGate is null ||
        !string.Equals(_existingGate.CampaignId, gate.CampaignId, StringComparison.Ordinal) ||
        !_existingGate.TierIds.OrderBy(t => t, StringComparer.Ordinal)
            .SequenceEqual(gate.TierIds.OrderBy(t => t, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// GitHub upload path for the "Upload and save" button: requires the gh CLI, creates the
    /// release when the tag is new, or replaces the asset on an existing tag. Uploads the
    /// staged copy, so what lands on the release is the file that was hashed and checked.
    /// </summary>
    private async Task<bool> PublishToGitHubAsync(StagedPackage staged, string version)
    {
        if (string.IsNullOrWhiteSpace(SourceRepo))
        {
            _showInfoDialog("GitHub repo missing",
                $"Set the GitHub repo for '{GameDisplayName}' first (e.g. RealAmethyst/DigimonNOAccess) so we know where to upload.");
            return false;
        }

        if (!await _gitHubService.IsAvailableAsync() || !await _gitHubService.IsAuthenticatedAsync())
        {
            _showInfoDialog("GitHub CLI required",
                "Uploading a release requires the 'gh' CLI to be installed and signed in. " +
                "Install from https://cli.github.com/ then run 'gh auth login'.");
            return false;
        }

        var tag = string.IsNullOrWhiteSpace(TagName) ? $"v{version}" : TagName!.Trim();
        var assetUrl = GitHubService.BuildAssetUrl(SourceRepo, tag, staged.FileName);

        StatusMessage = $"Checking what's already published at {SourceRepo} {tag}...";
        var existingReleases = await _gitHubService.ListReleasesAsync(SourceRepo);
        var hasTag = existingReleases.Any(r => r.TagName == tag);

        // A published asset URL is as immutable here as it is on the author's own server. The
        // GitHub leg used to clobber it, which put new bytes behind the live index's old
        // fingerprint — every download then failed the manager's hash gate until a new index
        // went out, and forever if it didn't.
        if (hasTag)
        {
            var (status, publishedSha) = await TryHashPublishedAsync(assetUrl);

            if (status == PublishedProbe.Unreadable)
            {
                // The upload below replaces whatever is at that address. Doing that without
                // knowing what's there is how live bytes get overwritten behind a published
                // fingerprint, so an unclear answer stops the publish rather than risking it.
                _showInfoDialog("Couldn't check what's already published",
                    $"{SourceRepo} {tag} exists, but reading the file already published there failed, so " +
                    "there's no way to tell whether uploading would replace someone's working download.\n\n" +
                    "Nothing was uploaded. Try again in a moment.");
                return false;
            }

            if (status == PublishedProbe.Found)
            {
                if (!string.Equals(publishedSha, staged.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    _showInfoDialog("That release already has this file",
                        $"{SourceRepo} {tag} already publishes {staged.FileName}, and this ZIP is a different " +
                        "file. Replacing it would break the download for anyone whose catalog still lists the " +
                        "old fingerprint.\n\nBump the version and publish that instead. Nothing was uploaded.");
                    return false;
                }

                PackageUrl = assetUrl.AbsoluteUri;
                _configService.SetGameSourceRepo(_projectPath, _gameId, SourceRepo);
                StatusMessage = $"Already published with these exact bytes — nothing re-uploaded. URL: {PackageUrl}";
                return true;
            }
        }

        StatusMessage = $"Uploading {staged.FileName} to {SourceRepo} {tag}...";

        var notes = string.IsNullOrWhiteSpace(ReleaseNotes)
            ? $"Release {tag} for the Accessibility Mod Manager."
            : ReleaseNotes!;

        ProcessResult result;
        if (hasTag)
        {
            result = await _gitHubService.UploadReleaseAssetAsync(SourceRepo, tag, staged.Path, clobber: true);
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
                new[] { staged.Path });
        }

        if (!result.Success)
        {
            _showInfoDialog("Upload failed",
                $"GitHub release upload failed:\n\n{result.Combined}");
            return false;
        }

        PackageUrl = assetUrl.AbsoluteUri;
        _configService.SetGameSourceRepo(_projectPath, _gameId, SourceRepo);
        StatusMessage = $"Uploaded. URL: {PackageUrl}";
        return true;
    }

    /// <summary>What reading a published asset told us. The three cases must not be conflated.</summary>
    private enum PublishedProbe
    {
        /// <summary>It's there and we hashed it.</summary>
        Found,
        /// <summary>The server says there's nothing at that address.</summary>
        Absent,
        /// <summary>We couldn't tell — a network blip, a proxy error, anything but a clean 404.</summary>
        Unreadable
    }

    /// <summary>
    /// Hashes whatever is published at <paramref name="url"/>, streaming it rather than holding
    /// it in memory — these are mod packages, and buffering one to compare a fingerprint is
    /// needless pressure. "Couldn't read it" is deliberately distinct from "it isn't there":
    /// treating a transient failure as absence is exactly how an overwrite gets waved through.
    /// </summary>
    private async Task<(PublishedProbe Status, string? Sha256)> TryHashPublishedAsync(Uri url)
    {
        try
        {
            var separator = string.IsNullOrEmpty(url.Query) ? "?" : "&";
            var busted = new Uri(url.AbsoluteUri + separator + "_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var response = await PublishedAssetHttp.GetAsync(
                busted, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);

            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                return (PublishedProbe.Absent, null);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning("Reading {Url} returned {Status}", url, response.StatusCode);
                return (PublishedProbe.Unreadable, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            return (PublishedProbe.Found, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't read the published asset at {Url}", url);
            return (PublishedProbe.Unreadable, null);
        }
    }

    /// <summary>
    /// A private copy of the wrapped ZIP, named the way it will be published, held open for as
    /// long as the publish takes. The copy lives in our own temp folder rather than beside the
    /// author's build output: renaming in place used to overwrite whatever file already had
    /// that name in their folder, and a build tool writing to the original mid-publish would
    /// have made the published hash a lie. Deleting the folder is what disposal is for.
    /// </summary>
    private sealed class StagedPackage : IDisposable
    {
        private readonly string _tempDir;

        public FileStream Stream { get; }
        public string Path { get; }
        public string FileName { get; }
        public string Sha256 { get; }

        private StagedPackage(string tempDir, string path, string fileName, FileStream stream, string sha256)
        {
            _tempDir = tempDir;
            Path = path;
            FileName = fileName;
            Stream = stream;
            Sha256 = sha256;
        }

        public static StagedPackage Create(string sourcePath, string? assetFileName)
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"The wrapped ZIP isn't there any more: {sourcePath}", sourcePath);

            // The published filename becomes a path segment locally and remotely, so it has to
            // be a plain file name (audit finding 38c).
            var fileName = PathSafety.EnsureLeafFileName(
                string.IsNullOrWhiteSpace(assetFileName) ? System.IO.Path.GetFileName(sourcePath) : assetFileName,
                "Asset filename");

            var tempDir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "AccessibilityModManager.AuthorTool", "publish", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var stagedPath = System.IO.Path.Combine(tempDir, fileName);
                File.Copy(sourcePath, stagedPath);

                // FileShare.Read: readers (the gh CLI) are fine, writers are locked out for the
                // lifetime of the publish.
                var stream = new FileStream(
                    stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
                    stream.Position = 0;
                    return new StagedPackage(tempDir, stagedPath, fileName, stream, sha);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch
            {
                TryDelete(tempDir);
                throw;
            }
        }

        public void Dispose()
        {
            Stream.Dispose();
            TryDelete(_tempDir);
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* temp folder; the OS cleans up */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseDialog?.Invoke();
    }

    /// <summary>
    /// Builds the release's Patreon block, or reports what's still missing. Runs BEFORE any
    /// upload: a release that can't be saved must not leave files on the server first. The
    /// <c>ServerUrl</c> it carries is filled in later — a fresh publish stamps the new URL,
    /// an edit that doesn't re-upload keeps the link the author already published.
    /// </summary>
    private bool TryBuildGate(out PatreonGate? gate, out string? error)
    {
        gate = null;
        error = null;
        if (!IsPatreonGated) return true;

        var selectedTierIds = PatreonTierSelections.Where(t => t.IsSelected).Select(t => t.TierId).ToList();
        if (selectedTierIds.Count == 0)
        {
            error = "Pick at least one Patreon tier that grants access to this release.";
            return false;
        }
        if (string.IsNullOrEmpty(_resolvedCampaignId))
        {
            error = "Couldn't resolve your Patreon campaign id. Sign in to Patreon and refresh tiers, then try again.";
            return false;
        }

        string? postId = null;
        string? attachmentFileName = null;
        if (!IsServerUploadConfigured)
        {
            // Patreon-post-as-CDN flow — author manually attached the ZIP to a tier-locked
            // post and the manager will open the post in the patron's browser as the
            // file-picker fallback path.
            postId = PatreonAuthorService.ExtractPostId(PatreonPostUrl ?? "");
            if (string.IsNullOrEmpty(postId))
            {
                error = "Patreon post URL is missing or invalid. Paste the URL of the post your wrapped ZIP is attached to.";
                return false;
            }
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
        return true;
    }

    private void BuildResult(PatreonGate? gate)
    {
        if (gate != null)
        {
            gate = new PatreonGate
            {
                CampaignId = gate.CampaignId,
                TierIds = gate.TierIds,
                PostId = gate.PostId,
                AttachmentFileName = gate.AttachmentFileName,
                ServerUrl = _freshServerUrl ?? _existingServerUrl
            };
        }

        // When a package was published in this session, IT decides the version and fingerprint —
        // the form fields are only a fallback for metadata-only saves. Anything the author typed
        // into those two fields mid-publish is written back so the dialog stops showing a value
        // that was never published.
        if (_published is { } published)
        {
            Version = published.Version;
            Sha256 = published.Sha256;
        }

        Result = new ModRelease
        {
            GameId = _gameId,
            PluginId = _pluginId,
            Version = _published?.Version ?? Version!,
            Channel = Channel ?? "stable",
            // Patreon-gated releases don't carry a public URL — the manager resolves the
            // attachment URL via the Patreon API at install time.
            PackageUrl = gate is null ? new Uri(PackageUrl!) : null,
            Sha256 = _published?.Sha256 ?? Sha256!,
            ChangelogUrl = string.IsNullOrWhiteSpace(ChangelogUrl) ? null : ChangelogUrl,
            Notes = string.IsNullOrWhiteSpace(ReleaseNotes) ? null : ReleaseNotes,
            Patreon = gate
        };
    }

    /// <summary>
    /// The version and the asset filename both become folder and file names — locally in the
    /// staging copy, remotely as URL segments on the download server. Checking them here means
    /// the author is told in the form, not by an exception from deep inside a publish (audit
    /// finding 38c).
    /// </summary>
    /// <summary>
    /// A release's version is baked into its package: the manifest declares it, and the manager
    /// aborts the install when the two disagree. So changing the version of an existing release
    /// without producing the package for that version doesn't edit metadata — it publishes a
    /// release nobody can install. Rebuilding or re-picking the ZIP is the fix, and it also puts
    /// the file back through the pre-publish checks.
    /// </summary>
    private string? ValidateVersionMatchesPackage()
    {
        if (!IsEditingExistingRelease || string.IsNullOrEmpty(_existingVersion)) return null;
        if (!string.IsNullOrWhiteSpace(LocalZipPath)) return null;
        if (string.Equals(_existingVersion.Trim(), Version?.Trim(), StringComparison.Ordinal)) return null;

        return $"This release's package was built for version {_existingVersion}, and its manifest still " +
               $"says so — the manager refuses an install where the package and the release disagree.\n\n" +
               $"Build or pick the wrapped ZIP for {Version?.Trim()} before saving, or set the version back " +
               $"to {_existingVersion}.";
    }

    private string? ValidatePublishedNames()
    {
        var version = Version?.Trim();
        if (!string.IsNullOrEmpty(version) &&
            (version.Contains('/') || version.Contains('\\') || version == "." ||
             version.Contains("..", StringComparison.Ordinal) || version.Any(char.IsControl)))
        {
            return $"Version '{Version}' can't be used as a folder name on your download server. " +
                   "Use a plain version like 1.2.0 — no slashes or '..'.";
        }

        if (!string.IsNullOrWhiteSpace(AssetFileName))
        {
            try
            {
                PathSafety.EnsureLeafFileName(AssetFileName, "Asset filename");
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }

        return null;
    }

    private string? ValidateForUpload()
    {
        if (string.IsNullOrWhiteSpace(Version)) return "Version is required.";
        if (string.IsNullOrWhiteSpace(Channel)) return "Channel is required.";
        if (ValidatePublishedNames() is { } nameError) return nameError;
        if (ValidateVersionMatchesPackage() is { } versionError) return versionError;
        if (string.IsNullOrWhiteSpace(LocalZipPath) && string.IsNullOrWhiteSpace(PackageUrl))
            return "Pick a wrapped ZIP to upload, or paste a public URL and use 'Save without upload'.";
        if (!string.IsNullOrWhiteSpace(PackageUrl) && string.IsNullOrWhiteSpace(Sha256))
            return "If you supply a URL directly, you must also fill in the SHA256.";
        if (!string.IsNullOrWhiteSpace(PackageUrl) && !PackageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "Package URL must use https://.";
        if (IsHostingOnServer && string.IsNullOrWhiteSpace(AssetFileName) && string.IsNullOrWhiteSpace(LocalZipPath))
            return "Pick or build a wrapped ZIP so the server upload knows what file to send.";
        return null;
    }

    private string? ValidateForUrlOnly()
    {
        if (string.IsNullOrWhiteSpace(Version)) return "Version is required.";
        if (string.IsNullOrWhiteSpace(Channel)) return "Channel is required.";
        if (ValidatePublishedNames() is { } nameError) return nameError;
        if (ValidateVersionMatchesPackage() is { } versionError) return versionError;

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
