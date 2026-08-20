using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed partial class IndexEditorViewModel : ObservableObject
{
    private readonly string _projectPath;
    private readonly AuthorConfigService _configService;
    private readonly IndexFileService _indexFileService;
    private readonly Sha256HashService _hashService;
    private readonly GitService _gitService;
    private readonly GitHubService _gitHubService;
    private readonly ServerUploadService _serverUploadService;
    private readonly ILogger _logger;

    /// <summary>Live-index fetches (baseline capture, third-party-change check, post-publish verify).</summary>
    private static readonly System.Net.Http.HttpClient CatalogHttp = new();

    /// <summary>
    /// The live index's bytes as they were when this project OPENED — the third-party-change
    /// baseline. A live index at publish time that matches neither this nor the candidate means
    /// someone else changed it while this editor was open; publishing would clobber their work.
    /// Null when the live index couldn't be read at load (offline) — the check then softens to
    /// a confirm.
    /// </summary>
    private byte[]? _liveIndexAtLoad;
    private readonly Action<string, string> _showInfoDialog;
    private readonly Func<string, string, bool> _confirmDialog;
    private readonly Func<string, string, string?, string?> _browseForFile;
    private readonly Action _closeProject;
    private readonly Func<string, string, string, string, string?, ObservableCollection<string>, IList<Dependency>, LifecycleScriptInputs, ModRelease?, ReleaseDialogResult?> _showReleaseDialog;
    private readonly Func<ISet<string>, ObservableCollection<string>, AddGameDialogViewModel?> _showAddGameDialog;
    private readonly Func<string, PluginAuthorInfo?, PluginAuthorInfo?> _showAuthorInfoDialog;
    private readonly Action _showServerUploadSettingsDialog;
    private readonly RegistryMembershipChecker _registryChecker;
    private readonly ProjectReconciler _reconciler;
    private readonly IndexPublishCoordinator _publishCoordinator;
    private readonly Action<string, RegistryTrustState> _showClaimSigningDialog;

    private PluginRepoIndex _index;
    private bool _suppressDirty;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(PublishIndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(BreakPublishLockCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditCatalogSigningCommand))]
    private bool _isBusy;

    /// <summary>
    /// Whether an action that talks to the server may start. It is not merely about double-clicks:
    /// clearing the publish lock while THIS copy is publishing would delete the lock this copy is
    /// holding and let a second machine in, which is the one situation the lock exists to prevent.
    ///
    /// <para>Editing itself is deliberately left alone. A publish sends the bytes it read when it
    /// started, so typing during one cannot change what goes out, and freezing the form under a
    /// screen reader mid-operation costs more than it protects.</para>
    /// </summary>
    public bool IsNotBusy => !IsBusy;

    /// <summary>
    /// Whether a server operation owns this editor right now. It is what actually enforces one at a
    /// time; <see cref="IsBusy"/> is the announcement of it.
    ///
    /// <para>Disabling the commands is not enough on its own, and the way it fails is worth writing
    /// down. Adding or editing a release saves and then offers to publish, and those flows are not
    /// disabled — deliberately, since editing stays available. So a publish can be entered while
    /// one is already running. It would take the lock, find this copy holding it, report that and
    /// return — and its <c>finally</c> would clear the shared flag, re-enabling Save, Publish and
    /// Clear-lock while the first publish is still going. The author could then be asked whether
    /// every OTHER copy is closed, answer yes perfectly honestly, and delete the lock their own
    /// in-flight publish is holding. A second machine gets in, and two publishes under one key can
    /// produce two different versions of the same publish number, which nothing downstream can
    /// untangle.</para>
    ///
    /// <para>A plain bool is sufficient here and a lock is not needed: every one of these paths
    /// runs on the WPF UI thread, and nothing can be interleaved between testing this and setting
    /// it because there is no await between them.</para>
    /// </summary>
    private bool _serverOperationInFlight;

    /// <summary>
    /// Claims the editor for one server operation, or reports that another one already has it.
    /// </summary>
    private bool TryBeginServerOperation()
    {
        if (_serverOperationInFlight) return false;

        _serverOperationInFlight = true;
        IsBusy = true;
        return true;
    }

    private void EndServerOperation()
    {
        _serverOperationInFlight = false;
        IsBusy = false;
    }

    [ObservableProperty]
    private GameItemViewModel? _selectedGame;

    [ObservableProperty]
    private bool _isLoadingGitHubRepos;

    /// <summary>
    /// Three-way state for the public-registry banner:
    /// null = checking or unknown, true = listed, false = not listed (and registry was reachable).
    /// </summary>
    [ObservableProperty]
    private bool? _isListedInRegistry;

    [ObservableProperty]
    private string? _registryStatusText;

    [ObservableProperty]
    private bool _registryCheckCompleted;

    /// <summary>
    /// True when at least one game has zero tags AND zero languages set. Drives a soft
    /// warning banner near the Save button so authors notice — Save still works.
    /// </summary>
    public bool HasGamesWithoutFilters =>
        Games.Any(g => !g.HasAnyFilters);

    public string? FilterWarningText =>
        HasGamesWithoutFilters
            ? $"{Games.Count(g => !g.HasAnyFilters)} game(s) have no filter tags or languages set yet — users won't find them via filters."
            : null;

    public string PluginId => _index.PluginId;
    public string ProjectPath => _projectPath;
    public string DisplayProjectPath => _projectPath;

    public ObservableCollection<GameItemViewModel> Games { get; } = [];

    /// <summary>
    /// User's GitHub repos, fetched lazily via 'gh repo list'. Shared with the new-release
    /// dialog and the game form so both can use a dropdown picker. Empty if gh is missing
    /// or the user isn't authenticated — the editable ComboBox falls back to free text.
    /// </summary>
    public ObservableCollection<string> AvailableGitHubRepos { get; } = [];

    private readonly GitHubIndexPublisher _gitHubPublisher;
    private readonly UnsignedPublishGate _unsignedGate;

    /// <summary>
    /// Resolved once per publish and cached only within it — a branch can be switched or a remote
    /// re-pointed between publishes, and a stale target would push somewhere the author has moved on
    /// from.
    /// </summary>
    private GitPublishTarget? _gitTarget;

    public IndexEditorViewModel(
        string projectPath,
        AuthorConfigService configService,
        IndexFileService indexFileService,
        Sha256HashService hashService,
        GitService gitService,
        GitHubService gitHubService,
        ServerUploadService serverUploadService,
        PatreonAuthorService patreon,
        ILogger logger,
        Action<string, string> showInfoDialog,
        Func<string, string, bool> confirmDialog,
        Func<string, string, string?, string?> browseForFile,
        Action closeProject,
        Func<string, string, string, string, string?, ObservableCollection<string>, IList<Dependency>, LifecycleScriptInputs, ModRelease?, ReleaseDialogResult?> showReleaseDialog,
        Func<ISet<string>, ObservableCollection<string>, AddGameDialogViewModel?> showAddGameDialog,
        Func<string, PluginAuthorInfo?, PluginAuthorInfo?> showAuthorInfoDialog,
        Action showServerUploadSettingsDialog,
        RegistryMembershipChecker registryChecker,
        ProjectReconciler reconciler,
        IndexPublishCoordinator publishCoordinator,
        GitHubIndexPublisher gitHubPublisher,
        UnsignedPublishGate unsignedGate,
        Action<string, RegistryTrustState> showClaimSigningDialog)
    {
        _gitHubPublisher = gitHubPublisher;
        _unsignedGate = unsignedGate;
        _projectPath = projectPath;
        _configService = configService;
        _indexFileService = indexFileService;
        _hashService = hashService;
        _gitService = gitService;
        _gitHubService = gitHubService;
        _serverUploadService = serverUploadService;
        _patreon = patreon;
        _logger = logger;
        _showInfoDialog = showInfoDialog;
        _confirmDialog = confirmDialog;
        _browseForFile = browseForFile;
        _closeProject = closeProject;
        _showReleaseDialog = showReleaseDialog;
        _showAddGameDialog = showAddGameDialog;
        _showAuthorInfoDialog = showAuthorInfoDialog;
        _showServerUploadSettingsDialog = showServerUploadSettingsDialog;
        _registryChecker = registryChecker;
        _reconciler = reconciler;
        _publishCoordinator = publishCoordinator;
        _showClaimSigningDialog = showClaimSigningDialog;

        _patreon.StateChanged += OnPatreonStateChanged;

        _index = LoadOrThrow();
        RebuildGameList();
        if (Games.Count > 0) SelectedGame = Games[0];

        _ = LoadGitHubReposAsync();
        _ = CheckRegistryMembershipAsync();
        _ = ReconcileWithPublishedCatalogAsync();
    }

    /// <summary>
    /// Works out, in the background, whether the published catalog has moved on — and adopts it only
    /// if the folder is still exactly as this method found it.
    ///
    /// <para>It runs unawaited because opening a project must not wait on the network: the window
    /// appears at once and stays usable. The cost of that is a race this used to lose. The author can
    /// start typing while the fetch is in flight, and the continuation would then overwrite
    /// <c>index.json</c> and reload the model out from under them. So the exact bytes it started from
    /// are captured first, and nothing is written unless those bytes are still on disk and nothing
    /// has been edited since. The continuation resumes on the UI thread, so there is no gap between
    /// that check and the write for a keystroke to fall into.</para>
    ///
    /// <para>Declining is not silent. Carrying on against a stale copy is the situation the author
    /// most needs to know about, since the next publish is the one that discovers it.</para>
    /// </summary>
    private async Task ReconcileWithPublishedCatalogAsync()
    {
        byte[] localAtStart;
        try
        {
            localAtStart = File.ReadAllBytes(Path.Combine(_projectPath, "index.json"));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't read the local index to compare it with the published one");
            return;
        }

        try
        {
            var cfg = _configService.GetServerUploadConfig();
            var outcome = await _reconciler.InspectAsync(
                cfg is null ? null : new ServerUploadPublishTransport(_serverUploadService, cfg),
                new RegistryVerifiedSource(_registryChecker),
                _index.PluginId, localAtStart,
                _configService.GetLastPublishedIndexSha(_projectPath), CancellationToken.None);

            if (outcome.Action == ReconcileAction.Explain)
            {
                StatusMessage = outcome.Message;
                return;
            }

            if (outcome.Action == ReconcileAction.Unsigned)
            {
                // No key is anchored for this plugin, so this is the catalog as it has always been:
                // adopt over HTTPS exactly as before, presets and scripts kept local.
                //
                // _liveIndexAtLoad records what the server was SERVING when this opened, which is
                // what the publish-time third-party check compares against. That is an observation
                // and stays true whether or not it was adopted.
                _liveIndexAtLoad = await TryFetchLiveIndexAsync();
                if (_liveIndexAtLoad is null) return;

                if (StillUntouched(localAtStart)) ReconcileWithLiveIndex(_liveIndexAtLoad);
                else AnnounceRaceLost();
                return;
            }

            if (outcome.Action is not (ReconcileAction.Adopt or ReconcileAction.AdoptWithConsent)) return;

            // Read once, and immediately before the write it guards. Everything from here to the
            // replacement is synchronous on the UI thread, so the author cannot get an edit in
            // between.
            if (!StillUntouched(localAtStart))
            {
                AnnounceRaceLost();
                return;
            }

            if (outcome.Action == ReconcileAction.AdoptWithConsent &&
                !_confirmDialog("Your copy and the published one differ", outcome.Message!))
            {
                StatusMessage = "Kept your local copy. It differs from what's published until you publish it.";
                return;
            }

            AdoptVerifiedCatalog(outcome, localAtStart);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't reconcile with the published catalog at load");
        }
    }

    /// <summary>
    /// Whether the folder is still exactly what reconciliation started from.
    ///
    /// <para>Both halves are needed and they catch different things: unsaved changes are edits that
    /// exist only in this window, and a changed file is one another program wrote — or one this
    /// window already saved. Either way the copy on disk is no longer the copy that was compared.</para>
    /// </summary>
    private bool StillUntouched(byte[] localAtStart)
    {
        if (HasUnsavedChanges) return false;

        try
        {
            return File.ReadAllBytes(Path.Combine(_projectPath, "index.json"))
                .AsSpan().SequenceEqual(localAtStart);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't re-read the local index before adopting the published one");
            return false;
        }
    }

    /// <summary>
    /// Says why the folder was left alone when reconciliation was overtaken by the author.
    /// </summary>
    private void AnnounceRaceLost() =>
        StatusMessage = "What's published isn't the same as this folder — but you'd already started " +
                        "editing, so nothing was replaced. Reopen the project to compare again.";

    /// <summary>
    /// Writes the verified catalog into the folder. Every field in it came out of a signed claim,
    /// and the author's own fields were carried across before it got here.
    ///
    /// <para>Through <see cref="DurableFile"/> rather than a plain write, because the file being
    /// replaced is the author's own work and a half-written index.json is a project that will not
    /// open. Everything that can fail is separated by whether the file has been replaced yet, so the
    /// message can never say the folder is unchanged when it is not — that reassurance is only
    /// useful if it is always true.</para>
    /// </summary>
    private void AdoptVerifiedCatalog(ReconcileOutcome outcome, byte[] localAtStart)
    {
        // Compared again here, with nothing between it and the write. The caller compared too, only
        // so a question is not asked about an adoption that is about to be refused — a confirmation
        // dialog is a person thinking, and the folder can move while they do.
        if (HasUnsavedChanges)
        {
            AnnounceRaceLost();
            return;
        }

        var replaced = LocalIndexAdoption.ReplaceIfUnchanged(
            Path.Combine(_projectPath, "index.json"), localAtStart, outcome.Document!, out var error);

        if (replaced == AdoptionResult.Superseded)
        {
            AnnounceRaceLost();
            return;
        }

        if (replaced == AdoptionResult.Failed)
        {
            _logger.Error("Couldn't adopt the published catalog for {Project}: {Error}", _projectPath, error);
            _showInfoDialog("Couldn't take the published copy",
                $"{error}\n\nThis folder's copy is unchanged.");
            return;
        }

        try
        {
            _index = LoadOrThrow();
            RebuildGameList();
            if (Games.Count > 0) SelectedGame = Games[0];
            HasUnsavedChanges = false;
            RecordPublishedIndex(outcome.Document!);

            StatusMessage = $"Loaded publish {outcome.Generation} from the server — this folder's copy " +
                            "was out of date. Your presets and default scripts were kept.";
            _logger.Information("Adopted verified publish {Generation} for {Project}",
                outcome.Generation, _projectPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Adopted the published catalog but couldn't reload it for {Project}",
                _projectPath);
            _showInfoDialog("Took the published copy, but couldn't load it",
                $"{ex.Message}\n\nThis folder now holds publish {outcome.Generation}. Close and reopen " +
                "the project.");
        }
    }

    /// <summary>
    /// When the local index.json and the published one disagree at open time, the published one
    /// is what users are actually reading, so it's the copy that should win — Amethyst's call,
    /// 2026-07-25.
    /// <para>
    /// With one exception, which is the whole reason this isn't a silent overwrite: "different"
    /// covers two opposite situations. If the local file is still exactly what this machine last
    /// published, then the LIVE copy moved on without it (published from somewhere else) and
    /// adopting it loses nothing. But if the local file has been edited since that publish, those
    /// edits are unpublished work, and taking the live copy would throw them away. That case asks
    /// first, and defaults to keeping the local draft.
    /// </para>
    /// </summary>
    private void ReconcileWithLiveIndex(byte[] live)
    {
        var indexPath = Path.Combine(_projectPath, "index.json");
        byte[] local;
        try
        {
            local = File.ReadAllBytes(indexPath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't read the local index to compare it with the live one");
            return;
        }

        if (local.AsSpan().SequenceEqual(live)) return;

        var localSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(local));
        var lastPublished = _configService.GetLastPublishedIndexSha(_projectPath);
        var localIsUnpublishedWork =
            lastPublished is null ||
            !string.Equals(localSha, lastPublished, StringComparison.OrdinalIgnoreCase);

        if (localIsUnpublishedWork &&
            !_confirmDialog("Your copy and the published one differ",
                "The index published on your server isn't the same as the copy in this folder, and " +
                "this folder has changes that were never published.\n\n" +
                "Taking the published copy would discard those local changes. Keeping yours means the " +
                "published index stays as it is until you publish.\n\n" +
                "Replace this folder's copy with the published one?"))
        {
            StatusMessage = "Kept your local copy. It differs from what's published until you publish it.";
            return;
        }

        try
        {
            File.WriteAllBytes(indexPath, KeepLocalAuthoringFields(live, local));
            _index = LoadOrThrow();
            RebuildGameList();
            if (Games.Count > 0) SelectedGame = Games[0];
            HasUnsavedChanges = false;
            _configService.SetLastPublishedIndexSha(_projectPath, Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(live)));
            StatusMessage = "Loaded the published index — this folder's copy was out of date.";
            _logger.Information("Adopted the live index for {Project}", _projectPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Couldn't adopt the live index for {Project}", _projectPath);
            _showInfoDialog("Couldn't take the published copy",
                $"{ex.Message}\n\nThis folder's copy is unchanged.");
        }
    }

    /// <summary>
    /// Adopting the published index takes its catalog, never its authoring data.
    ///
    /// Presets, default lifecycle scripts and dependency version-discovery rules are author-only.
    /// No signed claim will ever cover them, so nothing downstream protects them — and each one
    /// feeds something the author later signs: a preset fills in a dependency's download URL and
    /// hash, a default script fills in a release form, a discovery rule decides which upstream
    /// build a dependency points at. A server that edited any of them would be choosing content for
    /// the author to put their signing key behind, and one plausible click later every manager
    /// installs it. That is the signing-oracle attack the claim design exists to prevent, arriving
    /// through authoring data instead of catalog data.
    ///
    /// So what is in this folder stays in this folder.
    /// </summary>
    private byte[] KeepLocalAuthoringFields(byte[] live, byte[] local)
    {
        try
        {
            var adopted = JsonNode.Parse(live)?.AsObject();
            var mine = JsonNode.Parse(local)?.AsObject();
            if (adopted is null || mine is null) return live;

            AuthoringOnlyFields.RestoreFromLocal(adopted, mine);

            return Encoding.UTF8.GetBytes(adopted.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException ex)
        {
            // Neither copy is readable as JSON, which the caller is about to discover anyway.
            _logger.Warning(ex, "Couldn't separate authoring fields while adopting the published index");
            return live;
        }
    }

    /// <summary>
    /// Where THIS project's published index is read from.
    ///
    /// <para>Destination-aware on purpose: it is used both to verify a publish and to reconcile the
    /// folder when the project opens, and a GitHub-hosted catalog reconciled against the server
    /// address would compare against somebody else's index — or nothing at all — and offer to
    /// replace the author's work with it.</para>
    /// </summary>
    private Uri LiveIndexUrl =>
        _gitTarget is { } target && CurrentDestination == PublishDestination.GitHub
            ? new Uri(target.BranchRawUrl)
            : new Uri(RegistryMembershipChecker.RegistryUrl,
                $"plugins/{Uri.EscapeDataString(_index.PluginId)}/index.json");

    /// <summary>The author's chosen destination for this project, or Unset if never chosen.</summary>
    private PublishDestination CurrentDestination =>
        _configService.GetPublishDestination(_projectPath, _index.PluginId);

    /// <summary>
    /// Names the destination on the button that changes it, so the current setting is readable
    /// without opening anything — this decides where an author's catalog goes, and a control that
    /// only says "destination" would make them press it to find out.
    /// </summary>
    public string PublishDestinationLabel => CurrentDestination switch
    {
        PublishDestination.GitHub => "Publishing to: GitHub",
        PublishDestination.Server => "Publishing to: your server",
        _ => "Publishing to: not set"
    };

    /// <summary>Names the destination on the Publish button itself, for the same reason.</summary>
    public string PublishButtonName => CurrentDestination switch
    {
        PublishDestination.GitHub => "Publish index to GitHub",
        PublishDestination.Server => "Publish index to your server",
        _ => "Publish index"
    };

    /// <summary>
    /// Changes where this project publishes. Separate from the first-publish question so a wrong
    /// answer there is not permanent — the setting decides where an author's catalog goes, and
    /// there has to be a way back.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private void ChangePublishDestination()
    {
        var current = CurrentDestination;
        var chosen = AskWhereThisPublishes();

        _configService.SetPublishDestination(_projectPath, _index.PluginId, chosen);
        _gitTarget = null;   // a new destination invalidates any resolved git target

        OnPropertyChanged(nameof(PublishDestinationLabel));
        OnPropertyChanged(nameof(PublishButtonName));

        StatusMessage = current == chosen
            ? PublishDestinationLabel + ", unchanged."
            : PublishDestinationLabel + ".";
    }

    /// <summary>Reads what the catalog's own download addresses actually serve.</summary>
    private static readonly PublishedAssetProbe PublishedAssets = new();

    /// <summary>
    /// How long to keep asking before calling a public address unreachable.
    ///
    /// <para>The download server does not read the catalog on every request — it reloads it on a
    /// timer (30 seconds at the time of writing). So for up to one full interval after an index
    /// goes live, a release the catalog now describes is one the server has not heard about yet,
    /// and it answers 404 correctly. Asking once, immediately, tests the timer rather than the
    /// configuration. Two full intervals is the smallest window that survives a publish landing
    /// just after a tick.</para>
    /// </summary>
    private static readonly TimeSpan ReachabilityDeadline = TimeSpan.FromSeconds(75);

    private static readonly TimeSpan ReachabilityRetryGap = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Per-request bound. Generous because a hit streams and hashes a whole mod package, and a
    /// large one over a slow line is not a fault.
    /// </summary>
    private static readonly TimeSpan ReachabilityRequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The whole check's budget. A per-release bound is not enough on its own: a catalog of
    /// releases that are each slow, or each absent, would multiply into something that looks like
    /// a hang long after the answer stopped being in doubt.
    /// </summary>
    private static readonly TimeSpan ReachabilityTotalBudget = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether a download address answers, retrying across the download server's catalog reload.
    ///
    /// <para>Deliberately a GET, not a HEAD: the download server answers GET (401 for a gated file,
    /// 404 for a missing one) but rejects HEAD with 405, so a HEAD check would report every healthy
    /// release as unreachable.</para>
    ///
    /// <para><paramref name="retryUntil"/> is shared by every release in one check rather than
    /// restarted for each. The wait exists to let ONE catalog reload happen; once it has, a second
    /// release learns nothing by waiting again, and per-release windows would multiply a catalog of
    /// absent releases into many minutes of asking questions already answered.</para>
    /// </summary>
    private async Task<PublishedAssetResult> PublicUrlAnswersAsync(
        Uri url, bool hash, DateTimeOffset retryUntil, CancellationToken ct)
    {
        PublishedAssetResult result;

        while (true)
        {
            result = await PublishedAssets.ReadAsync(url, hash, ReachabilityRequestTimeout, ct);

            // Only absence is worth waiting out — it is the one answer the catalog reload changes.
            // A 5xx, a refused connection or bytes that don't match are not repaired by waiting,
            // and retrying them would just make the author wait to be told the same thing.
            if (result.Status != PublishedAssetStatus.Absent) break;
            if (DateTimeOffset.UtcNow + ReachabilityRetryGap >= retryUntil) break;

            await Task.Delay(ReachabilityRetryGap, ct);
        }

        _logger.Information("Public download {Url}: {Status} ({Detail})", url, result.Status, result.Detail);
        return result;
    }

    /// <summary>
    /// Proves that every public release the live catalog describes can actually be downloaded from
    /// the author's own server, and that what comes back is what the catalog promised.
    ///
    /// <para>This is the check the release dialog cannot make. An ungated file is servable only
    /// because the catalog says so, so before the index is live the right answer to "does this
    /// address work" is "no", and no amount of checking at upload time can distinguish that from a
    /// public base URL that points somewhere the uploads never went. Once the catalog is live the
    /// question has a true answer, and this asks it.</para>
    ///
    /// <para>Every public server-hosted release is checked, not just the one that changed. The
    /// cost is the size of the public catalog, paid once per publish, and the breadth is the point:
    /// a base URL that changes, a web root that moves, or an upload that never landed breaks
    /// releases nobody edited that day, and the diff-shaped version of this check would look
    /// straight past them. It also removes the need to remember anything between runs — a release
    /// that could not be verified last time is simply checked again on the next publish.</para>
    ///
    /// <para>NEVER GATES THE PUBLISH. It runs after the catalog is live and after any queued tier
    /// change has been applied, and reports what it found. Letting it fail the publish would let a
    /// health check undo a committed publishing position, or — worse — leave a release the catalog
    /// calls public still locked on the server, because that removal is what a failed publish
    /// skips.</para>
    /// </summary>
    private async Task VerifyPublicDownloadsAsync()
    {
        var cfg = _configService.GetServerUploadConfig();
        if (cfg is null) return;

        var downloads = PublicDownloadVerification.ServerHostedPublicDownloads(_index, cfg.PublicBaseUrl);
        if (downloads.Count == 0) return;

        // What the publish itself just said. Kept, because that is the part the author was waiting
        // for — a health check appended to it must not erase it.
        var published = StatusMessage?.TrimEnd() ?? "";

        // Three outcomes, kept apart because they license completely different sentences. "The
        // server says there is nothing there" is a fact about what every user will get. "This
        // computer couldn't tell" is a fact about this computer — a name that didn't resolve, a
        // proxy, a timeout, a 500 — and saying it means the download is broken for everyone is the
        // same overreach that sent the author to check settings that were correct.
        var missing = new List<PublicDownload>();
        var wrongBytes = new List<PublicDownload>();
        var unverified = new List<(PublicDownload Download, string Reason)>();

        using var budget = new CancellationTokenSource(ReachabilityTotalBudget);
        var retryUntil = DateTimeOffset.UtcNow + ReachabilityDeadline;

        for (var i = 0; i < downloads.Count; i++)
        {
            var download = downloads[i];

            if (budget.IsCancellationRequested)
            {
                unverified.Add((download, "the check ran out of time before reaching it"));
                continue;
            }

            StatusMessage = $"Checking your downloads work ({i + 1} of {downloads.Count}): {download.Describe()}...";

            PublishedAssetResult result;
            try
            {
                result = await PublicUrlAnswersAsync(download.Url, hash: true, retryUntil, budget.Token);
            }
            catch (OperationCanceledException)
            {
                // The budget ran out mid-read. Nothing was learned about this address, and saying
                // otherwise would report a slow line as a broken release.
                _logger.Warning("Checking public downloads ran out of time at {Url}", download.Url);
                unverified.Add((download, "the check ran out of time"));
                continue;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Checking {Url} stopped unexpectedly", download.Url);
                unverified.Add((download, ex.Message));
                continue;
            }

            switch (result.Status)
            {
                case PublishedAssetStatus.Absent:
                    missing.Add(download);
                    break;

                case PublishedAssetStatus.Unreadable:
                    unverified.Add((download, result.Detail));
                    break;

                default:
                    if (!string.Equals(result.Sha256, download.Sha256, StringComparison.OrdinalIgnoreCase))
                        wrongBytes.Add(download);
                    break;
            }
        }

        if (missing.Count == 0 && wrongBytes.Count == 0 && unverified.Count == 0)
        {
            var verified = downloads.Count == 1
                ? "Your public download works."
                : $"All {downloads.Count} of your public downloads work.";
            StatusMessage = Follows(published, verified);
            return;
        }

        ReportBrokenDownloads(missing, wrongBytes, unverified, cfg, published);
    }

    /// <summary>
    /// Says what was actually observed, and offers the likeliest cause as a suggestion rather than
    /// a diagnosis. The old wording asserted that a missing file MEANT the public base URL and the
    /// remote releases path disagreed, and sent the author to check settings that were correct —
    /// absence can equally be an upload that never landed or a download server that hasn't picked
    /// the catalog up, and "couldn't be read" is usually neither.
    /// </summary>
    private void ReportBrokenDownloads(
        List<PublicDownload> missing,
        List<PublicDownload> wrongBytes,
        List<(PublicDownload Download, string Reason)> unverified,
        ServerUploadConfig cfg,
        string published)
    {
        var body = new StringBuilder(
            "Your index is published — this is about the files it points at.\n\n");

        if (missing.Count > 0)
        {
            body.Append(missing.Count == 1
                ? "Your server says this release isn't there:\n"
                : "Your server says these releases aren't there:\n");

            foreach (var download in missing)
                body.Append($"\n  {download.Describe()}\n  {download.Url}\n");

            body.Append(
                $"\nThat answer is the same for everyone, so anyone following your catalog would get " +
                $"nothing. Worth checking: that the public base URL and the remote releases path in " +
                $"Server upload settings describe the same place on {cfg.Host}, and that the file " +
                $"really did land on the server.\n");
        }

        if (wrongBytes.Count > 0)
        {
            if (missing.Count > 0) body.Append('\n');
            body.Append(wrongBytes.Count == 1
                ? "This release downloaded, but the file doesn't match what your catalog promises:\n"
                : "These releases downloaded, but the files don't match what your catalog promises:\n");

            foreach (var download in wrongBytes)
                body.Append($"\n  {download.Describe()}\n  {download.Url}\n");

            body.Append(
                "\nEvery user's manager checks the fingerprint before installing, so these would be " +
                "refused. Upload the release again.\n");
        }

        if (unverified.Count > 0)
        {
            if (missing.Count > 0 || wrongBytes.Count > 0) body.Append('\n');
            body.Append(unverified.Count == 1
                ? "This release couldn't be checked from this computer:\n"
                : "These releases couldn't be checked from this computer:\n");

            foreach (var (download, reason) in unverified)
                body.Append($"\n  {download.Describe()} — {reason}\n");

            body.Append(
                "\nThat isn't the server saying they're missing — it's this check not getting an " +
                "answer it could read, which a connection here can cause just as easily as anything " +
                "on the server. They may well be fine. Publishing again checks them.\n");
        }

        var broken = missing.Count + wrongBytes.Count;

        _showInfoDialog(
            broken > 0 ? "Published, but some downloads don't work" : "Published, but not everything was checked",
            body.ToString().TrimEnd());

        var summary = (broken, unverified.Count) switch
        {
            (0, var u) => u == 1
                ? "One of your public downloads couldn't be checked."
                : $"{u} of your public downloads couldn't be checked.",
            (1, 0) => "One of your public downloads doesn't work.",
            (var b, 0) => $"{b} of your public downloads don't work.",
            (1, _) => "One of your public downloads doesn't work, and others couldn't be checked.",
            (var b, _) => $"{b} of your public downloads don't work, and others couldn't be checked."
        };
        StatusMessage = Follows(published, summary);
    }

    /// <summary>Appends a health line to whatever the publish itself reported, if anything.</summary>
    private static string Follows(string published, string addition) =>
        published.Length == 0 ? addition : $"{published} {addition}";

    /// <summary>
    /// Remembers exactly what went live, so a later open can tell "this folder is stale" apart
    /// from "this folder has work that was never published". Best-effort: failing to record it
    /// only costs the next open a question it could otherwise have answered itself.
    /// </summary>
    private void RecordPublishedIndex(byte[] published)
    {
        try
        {
            _configService.SetLastPublishedIndexSha(_projectPath, Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(published)));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't record the published index fingerprint");
        }
    }

    private async Task<byte[]?> TryFetchLiveIndexAsync()
    {
        try
        {
            var url = LiveIndexUrl;
            var separator = string.IsNullOrEmpty(url.Query) ? "?" : "&";
            var busted = new Uri(url.AbsoluteUri + separator + "_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var resp = await CatalogHttp.GetAsync(busted);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch
        {
            return null;
        }
    }

    private readonly PatreonAuthorService _patreon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatreonButtonText))]
    [NotifyPropertyChangedFor(nameof(PatreonStatusText))]
    private bool _patreonStateBumper;

    public string PatreonButtonText => _patreon.IsSignedIn ? "Sign out of Patreon" : "Sign in to Patreon...";

    public string PatreonStatusText
    {
        get
        {
            if (!_patreon.IsSignedIn) return "Not signed in to Patreon — sign in to mark releases as Patron-only.";
            var name = _patreon.CurrentAccount?.FullName ?? _patreon.CurrentAccount?.Email ?? "your account";
            var camp = _patreon.OwnCampaign?.DisplayName;
            return camp != null
                ? $"Signed in as {name}. Campaign: {camp} ({_patreon.OwnCampaign!.Tiers.Count} tier(s))."
                : $"Signed in as {name}. Couldn't load campaign — try refresh.";
        }
    }

    private void OnPatreonStateChanged() => PatreonStateBumper = !PatreonStateBumper;

    [RelayCommand]
    private async Task SignInOrOutOfPatreonAsync()
    {
        try
        {
            if (_patreon.IsSignedIn)
            {
                await _patreon.SignOutAsync(CancellationToken.None);
                StatusMessage = "Signed out of Patreon.";
            }
            else
            {
                await _patreon.SignInAsync(CancellationToken.None);
                StatusMessage = "Signed in to Patreon.";
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Patreon sign-in/out failed");
            _showInfoDialog("Patreon sign-in failed", ex.Message);
        }
    }

    private async Task CheckRegistryMembershipAsync()
    {
        IsListedInRegistry = null;
        RegistryStatusText = "Checking the public registry...";
        try
        {
            var result = await _registryChecker.CheckAsync(_index.PluginId);
            if (!result.RegistryReachable)
            {
                RegistryStatusText = "Couldn't reach the public registry to check listing. " +
                                     $"({result.Error})";
                IsListedInRegistry = null;
            }
            else if (result.IsListed)
            {
                RegistryStatusText = $"This plugin is listed in the public registry as " +
                                     $"\"{result.Entry?.Author ?? _index.PluginId}\".";
                IsListedInRegistry = true;
            }
            else
            {
                RegistryStatusText = $"Plugin id '{_index.PluginId}' is not in the public registry yet — add it from the registry admin screen.";
                IsListedInRegistry = false;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Registry membership check failed");
            RegistryStatusText = $"Couldn't check registry: {ex.Message}";
            IsListedInRegistry = null;
        }
        finally
        {
            RegistryCheckCompleted = true;
        }
    }

    [RelayCommand]
    private async Task RecheckRegistryMembershipAsync()
    {
        await CheckRegistryMembershipAsync();
    }

    private async Task LoadGitHubReposAsync()
    {
        if (AvailableGitHubRepos.Count > 0) return;

        IsLoadingGitHubRepos = true;
        try
        {
            if (!await _gitHubService.IsAvailableAsync()) return;
            if (!await _gitHubService.IsAuthenticatedAsync()) return;

            var repos = await _gitHubService.ListReposAsync();
            foreach (var r in repos.OrderBy(r => r.NameWithOwner, StringComparer.OrdinalIgnoreCase))
                AvailableGitHubRepos.Add(r.NameWithOwner);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not load GitHub repos for picker");
        }
        finally
        {
            IsLoadingGitHubRepos = false;
        }
    }

    private PluginRepoIndex LoadOrThrow()
    {
        if (!_indexFileService.Exists(_projectPath))
            throw new InvalidOperationException($"index.json not found in {_projectPath}");
        return _indexFileService.Load(_projectPath);
    }

    private void RebuildGameList()
    {
        _suppressDirty = true;
        try
        {
            var prevSelectedId = SelectedGame?.GameId;
            Games.Clear();
            foreach (var g in _index.Games.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var releases = _index.ReleasesByGameId.TryGetValue(g.GameId, out var rs) ? rs : [];
                var item = new GameItemViewModel(g, releases, this);
                item.PerGameSourceRepo = _configService.GetGameSourceRepo(_projectPath, g.GameId);

                // Re-hydrate the absolute script paths the author picked in earlier sessions
                // so the editor reflects them on reopen. Setting these inside _suppressDirty
                // prevents the load from marking the project dirty.
                var scriptSources = _configService.GetGameScriptSources(_projectPath, g.GameId);
                if (scriptSources != null)
                {
                    item.PreInstallScript.AbsoluteSourcePath = scriptSources.PreInstall;
                    item.PostInstallScript.AbsoluteSourcePath = scriptSources.PostInstall;
                    item.PostUninstallScript.AbsoluteSourcePath = scriptSources.PostUninstall;
                }

                Games.Add(item);
            }
            SelectedGame = prevSelectedId == null
                ? Games.FirstOrDefault()
                : Games.FirstOrDefault(g => g.GameId == prevSelectedId) ?? Games.FirstOrDefault();
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    /// <summary>
    /// Asks the author to confirm something during a save-back. Kept internal so
    /// <see cref="GameItemViewModel"/> can warn about a rename that will break already-published
    /// packages, without knowing anything about dialogs.
    /// </summary>
    internal bool ConfirmDuringSave(string title, string message) => _confirmDialog(title, message);

    internal void MarkDirty()
    {
        if (_suppressDirty) return;
        HasUnsavedChanges = true;
        OnPropertyChanged(nameof(HasGamesWithoutFilters));
        OnPropertyChanged(nameof(FilterWarningText));
    }

    [RelayCommand]
    private void AddGame()
    {
        var existingIds = _index.Games
            .Select(g => g.GameId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = _showAddGameDialog(existingIds, AvailableGitHubRepos);
        if (result == null) return;

        var game = result.ToGame();
        _index.Games.Add(game);
        if (!_index.ReleasesByGameId.ContainsKey(game.GameId))
            _index.ReleasesByGameId[game.GameId] = [];

        // Persist the per-game GitHub repo into author config (not into index.json).
        if (!string.IsNullOrWhiteSpace(result.GitHubRepo))
            _configService.SetGameSourceRepo(_projectPath, game.GameId, result.GitHubRepo);

        MarkDirty();
        RebuildGameList();
        SelectedGame = Games.FirstOrDefault(g => g.GameId == game.GameId);
    }

    [RelayCommand]
    private void RemoveSelectedGame()
    {
        if (SelectedGame == null) return;
        var game = SelectedGame;
        if (!_confirmDialog("Remove game",
            $"Remove '{game.DisplayName}' and all its releases from the index?\n\nThis change is not saved until you click Save."))
            return;

        _index.Games.RemoveAll(g => g.GameId == game.GameId);
        _index.ReleasesByGameId.Remove(game.GameId);
        MarkDirty();
        RebuildGameList();
    }

    [RelayCommand]
    private async Task AddReleaseAsync()
    {
        if (SelectedGame == null) return;
        if (!TryValidateGameInstaller(SelectedGame)) return;

        var initialSourceRepo = _configService.GetGameSourceRepo(_projectPath, SelectedGame.GameId)
            ?? SelectedGame.PerGameSourceRepo;

        if (!TryBuildDependencies(SelectedGame, out var deps))
            return;
        if (!TryBuildScripts(SelectedGame, out var scriptInputs))
            return;

        var dialogResult = _showReleaseDialog(
            SelectedGame.GameId,
            SelectedGame.DisplayName,
            _index.PluginId,
            _projectPath,
            initialSourceRepo,
            AvailableGitHubRepos,
            deps,
            scriptInputs!,
            null);

        if (dialogResult == null) return;
        var release = dialogResult.Release;

        if (!_index.ReleasesByGameId.TryGetValue(SelectedGame.GameId, out var list))
        {
            list = [];
            _index.ReleasesByGameId[SelectedGame.GameId] = list;
        }
        // Replace existing release with same version+channel, otherwise add.
        var existing = list.FindIndex(r => r.Version == release.Version && r.Channel == release.Channel);
        if (existing >= 0) list[existing] = release;
        else list.Add(release);

        SelectedGame.RefreshReleases(list);

        // The dialog persists the source repo to config on save. Reflect it back in the form.
        var savedRepo = _configService.GetGameSourceRepo(_projectPath, SelectedGame.GameId);
        if (!string.IsNullOrWhiteSpace(savedRepo))
            SelectedGame.PerGameSourceRepo = savedRepo;

        MarkDirty();
        StatusMessage = $"Release v{release.Version} ({release.Channel}) added.";

        await PublishAfterReleaseChangeAsync(
            $"Add {SelectedGame.DisplayName} v{release.Version} ({release.Channel})",
            dialogResult.GateChange);
    }

    [RelayCommand]
    private async Task EditSelectedReleaseAsync()
    {
        if (SelectedGame?.SelectedRelease == null) return;
        if (!TryValidateGameInstaller(SelectedGame)) return;
        var existing = SelectedGame.SelectedRelease;

        var initialSourceRepo = _configService.GetGameSourceRepo(_projectPath, SelectedGame.GameId)
            ?? SelectedGame.PerGameSourceRepo;
        if (!TryBuildDependencies(SelectedGame, out var deps))
            return;
        if (!TryBuildScripts(SelectedGame, out var scriptInputs))
            return;

        var dialogResult = _showReleaseDialog(
            SelectedGame.GameId,
            SelectedGame.DisplayName,
            _index.PluginId,
            _projectPath,
            initialSourceRepo,
            AvailableGitHubRepos,
            deps,
            scriptInputs!,
            existing);

        if (dialogResult == null) return;
        var updated = dialogResult.Release;

        if (!_index.ReleasesByGameId.TryGetValue(SelectedGame.GameId, out var list))
        {
            list = [];
            _index.ReleasesByGameId[SelectedGame.GameId] = list;
        }

        // Remove the original by its identity (version+channel may have changed during edit)
        // and add the updated record. If identity stayed the same, this is just a replace.
        list.RemoveAll(r => r.Version == existing.Version && r.Channel == existing.Channel);
        var clash = list.FindIndex(r => r.Version == updated.Version && r.Channel == updated.Channel);
        if (clash >= 0) list[clash] = updated;
        else list.Add(updated);

        SelectedGame.RefreshReleases(list);
        MarkDirty();
        StatusMessage = $"Release v{updated.Version} ({updated.Channel}) updated.";

        await PublishAfterReleaseChangeAsync(
            $"Update {SelectedGame.DisplayName} v{updated.Version} ({updated.Channel})",
            dialogResult.GateChange);
    }

    [RelayCommand]
    private async Task RemoveSelectedReleaseAsync()
    {
        if (SelectedGame?.SelectedRelease == null) return;
        var rel = SelectedGame.SelectedRelease;
        if (!_confirmDialog("Remove release",
            $"Remove v{rel.Version} ({rel.Channel}) from {SelectedGame.DisplayName}?\n\n" +
            "This will also save and publish the index to your server (you'll be asked to confirm)."))
            return;

        if (_index.ReleasesByGameId.TryGetValue(SelectedGame.GameId, out var list))
        {
            list.RemoveAll(r => r.Version == rel.Version && r.Channel == rel.Channel);
            SelectedGame.RefreshReleases(list);
        }
        MarkDirty();

        // Match Add/Edit flows so the removal actually reaches users — without this, the
        // local index reflects the removal but GitHub still serves the old release and
        // managers continue to show it as installable.
        await PublishAfterReleaseChangeAsync(
            $"Remove {SelectedGame.DisplayName} v{rel.Version} ({rel.Channel})");
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task SaveAsync()
    {
        // Save + prompt-to-publish, matching the Add/Edit/Remove-release flows. Without this,
        // changes that don't go through the release dialog (deps, filters, scripts, author
        // info) only land on disk and the manager keeps fetching the stale live index — so
        // dep auto-install silently no-ops, filters don't update, etc. The user can still
        // click No on the publish prompt to keep the old "save only" behavior.
        await PublishAfterReleaseChangeAsync(SuggestCommitMessage());
    }

    [RelayCommand]
    private void EditServerUploadSettings() => _showServerUploadSettingsDialog();

    [RelayCommand]
    private void EditAuthorInfo()
    {
        var result = _showAuthorInfoDialog(_index.PluginId, _index.Author);
        if (result == null) return;

        _index = new PluginRepoIndex
        {
            PluginId = _index.PluginId,
            RepoVersion = _index.RepoVersion,
            GeneratedAt = _index.GeneratedAt,
            Games = _index.Games,
            ReleasesByGameId = _index.ReleasesByGameId,
            Author = result
        };
        MarkDirty();
        StatusMessage = "Author info updated. Click Save to persist.";
    }

    private void CommitGameEditsToModel()
    {
        foreach (var item in Games)
        {
            item.WriteBackTo(_index);
            // The per-game GitHub repo is author-only metadata: kept in author config,
            // never written into the public index.json.
            if (!string.IsNullOrWhiteSpace(item.PerGameSourceRepo))
                _configService.SetGameSourceRepo(_projectPath, item.GameId, item.PerGameSourceRepo);

            // Same story for the absolute paths picked via Browse on the Scripts tab —
            // private to the author's machine, persisted via author config so they survive
            // restarts, never serialized into index.json.
            _configService.SetGameScriptSources(_projectPath, item.GameId, new GameScriptSources
            {
                PreInstall = item.PreInstallScript.AbsoluteSourcePath,
                PostInstall = item.PostInstallScript.AbsoluteSourcePath,
                PostUninstall = item.PostUninstallScript.AbsoluteSourcePath
            });
        }
    }

    /// <summary>
    /// Pulls the three lifecycle script slots off the game's editor view-models. Each editor's
    /// <see cref="LifecycleScriptEditorViewModel.ToModel"/> throws when the slot is enabled but
    /// missing required text — surface that clearly to the author and abort the release dialog
    /// rather than crashing the call. The returned <see cref="LifecycleScriptInputs"/> pairs
    /// each public script with the absolute source path the author picked via Browse so the
    /// builder can always bundle the file (Browse paths can live outside the source folder).
    /// </summary>
    /// <summary>
    /// Builds the dependency models for a release dialog, surfacing validation errors (bad SHA,
    /// absolute/traversing target folder, non-leaf target file name) as an info dialog instead of
    /// letting the exception escape the relay command — an unhandled throw there closes the app.
    /// </summary>
    private bool TryBuildDependencies(GameItemViewModel game, out List<Dependency> deps)
    {
        deps = [];
        try
        {
            deps = game.Dependencies.Select(d => d.ToModel()).ToList();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _showInfoDialog("Dependency needs fixing",
                $"{ex.Message}\n\nFix it on the Dependencies tab, then try again.");
            return false;
        }
    }

    private bool TryBuildScripts(GameItemViewModel game, out LifecycleScriptInputs? inputs)
    {
        inputs = null;
        try
        {
            inputs = new LifecycleScriptInputs(
                PreInstall: game.PreInstallScript.ToModel(),
                PreInstallSourcePath: game.PreInstallScript.AbsoluteSourcePath,
                PostInstall: game.PostInstallScript.ToModel(),
                PostInstallSourcePath: game.PostInstallScript.AbsoluteSourcePath,
                PostUninstall: game.PostUninstallScript.ToModel(),
                PostUninstallSourcePath: game.PostUninstallScript.AbsoluteSourcePath);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _showInfoDialog("Lifecycle script incomplete",
                $"{ex.Message}\n\nFix it on the Scripts tab, then try again.");
            return false;
        }
    }

    /// <summary>
    /// A game whose game-installer is a portable app (emulator) needs an Exe name: it's the Play
    /// target and the key by which a second game reuses the same emulator install (F1-B). Warn and
    /// abort the release cleanly if it's missing. See EMULATOR_INSTALL_QUESTIONS.md.
    /// </summary>
    private bool TryValidateGameInstaller(GameItemViewModel game)
    {
        var portableApp = game.Dependencies.FirstOrDefault(
            d => d.IsGameInstaller && d.AutoInstallEnabled && d.AutoInstallKind == "extractApp");
        if (portableApp == null) return true;

        if (string.IsNullOrWhiteSpace(game.ExeName))
        {
            _showInfoDialog("Exe name required",
                $"\"{game.DisplayName}\" installs a portable app (emulator), so it needs an Exe name " +
                "on the General tab — it's the Play target and how a second game reuses the same " +
                "emulator install. Set it, then try again.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(portableApp.FixDownloadUrl))
        {
            _showInfoDialog("Download URL required",
                $"\"{game.DisplayName}\" installs a portable app (emulator), so its game-installer " +
                "dependency needs the emulator ZIP's HTTPS download URL. Set it on the Dependencies " +
                "tab (and \"Fetch from URL\" for the SHA256), then try again.");
            return false;
        }
        return true;
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task PublishIndexAsync()
    {
        if (HasUnsavedChanges)
        {
            _showInfoDialog("Unsaved changes",
                "You have unsaved changes. Click Save first, then Publish index.");
            return;
        }

        // Only when the live catalog describes this folder. On every other outcome what is live is
        // something else — an earlier publish that turned out to have landed, or nothing at all —
        // and checking THIS folder's addresses would be asking about a catalog that isn't there.
        if (await PublishToDestinationAsync(SuggestCommitMessage(), confirmFirst: true))
            await VerifyPublicDownloadsAsync();
    }

    /// <summary>
    /// Clears a publish lock that nothing is holding any more.
    ///
    /// <para>Its own command, reachable when publishing is not, and deliberately never offered from
    /// the dialog that reports a lock in the way. A lock that is in the way is usually a lock that
    /// is doing its job — the other copy is still publishing — and the failure this guards against
    /// is a person clearing it because it was the button in front of them. Two copies publishing at
    /// once is how one key comes to sign two different versions of the same publish number, which
    /// nothing downstream can untangle.</para>
    ///
    /// <para>It breaks the lock and stops. It does not retry the publish, and it does not touch
    /// this machine's record of what it has published: an interrupted publish is settled by
    /// publishing again, which reads the server and decides, not by removing the evidence.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task BreakPublishLockAsync()
    {
        var cfg = _configService.GetServerUploadConfig();
        if (cfg is null)
        {
            _showInfoDialog("Server upload not configured",
                "Publish locks live on your server, so there are no settings here to work with yet.");
            return;
        }

        if (!TryBeginServerOperation())
        {
            _showInfoDialog("Busy", AnotherOperationInFlight);
            return;
        }

        try
        {
            ServerUploadService.RemoteLock found;
            try
            {
                found = await _serverUploadService.ReadPublishLockAsync(
                    cfg, _index.PluginId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Couldn't read the publish lock for {PluginId}", _index.PluginId);
                _showInfoDialog("Couldn't check the publish lock", ex.Message);
                return;
            }

            if (!found.Present)
            {
                _showInfoDialog("There is no publish lock",
                    $"Nothing is holding the publish lock for '{_index.PluginId}', so there is nothing " +
                    "to clear. If publishing is refusing for another reason, the message it gave says " +
                    "which.");
                return;
            }

            var whoHasIt = found.Body is not null
                ? $"It is held by {found.Body.Describe()}."
                : "There is a lock file there, but its contents can't be read, so who holds it is unknown.";

            if (!_confirmDialog("Clear the publish lock?",
                    $"{whoHasIt}\n\n" +
                    "Close every other copy of the AuthorTool first. Clearing a lock that another copy " +
                    "is really using lets both publish at once, and two publishes under one key can " +
                    "produce two different versions of the same publish — which can't be undone.\n\n" +
                    "Are all other copies closed?"))
            {
                StatusMessage = "Left the publish lock alone.";
                return;
            }

            bool cleared;
            try
            {
                // The lock the author just read about is named, so a different one that has since
                // been taken at the same path is left alone rather than deleted on the strength of
                // a question that was about something else.
                cleared = await _serverUploadService.BreakPublishLockAsync(
                    cfg, _index.PluginId, found.Fingerprint, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Couldn't clear the publish lock for {PluginId}", _index.PluginId);
                _showInfoDialog("Couldn't clear the publish lock", ex.Message);
                return;
            }

            if (!cleared)
            {
                _showInfoDialog("The lock changed",
                    "The publish lock is no longer the one you were shown — it was released and a " +
                    "new one taken while this was open, so something is publishing right now. It " +
                    "was left alone. Try again once that has finished.");
                return;
            }

            StatusMessage = "Cleared the publish lock. Choose Publish index to try again.";
            _logger.Warning("Cleared the publish lock for {PluginId} at the author's request", _index.PluginId);
        }
        finally
        {
            EndServerOperation();
        }
    }

    /// <summary>
    /// Tries the parts of the server signed publishing needs, and changes nothing.
    ///
    /// <para>Here so the machinery can be proved against the real server BEFORE the key is anchored
    /// in the registry — after that, the first signed publish would be the first time those paths
    /// ever ran for real, and a wrong lock directory discovered then is discovered at the worst
    /// possible moment.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task CheckServerAsync()
    {
        var cfg = _configService.GetServerUploadConfig();
        if (cfg is null)
        {
            _showInfoDialog("Server upload not configured",
                "There are no server settings to check yet. Set up Server upload settings first.");
            return;
        }

        if (!TryBeginServerOperation())
        {
            _showInfoDialog("Busy", AnotherOperationInFlight);
            return;
        }

        try
        {
            StatusMessage = "Checking your server...";

            var transport = new ServerUploadPublishTransport(_serverUploadService, cfg);
            var steps = await ServerSelfTest.RunAsync(
                transport, _index.PluginId, CancellationToken.None,
                rehearsal: transport,
                registry: new RegistryVerifiedSource(_registryChecker));

            var (title, message) = ServerSelfTest.Describe(steps);
            _showInfoDialog(title, message);
            StatusMessage = title;

            foreach (var step in steps)
                _logger.Information("Server check — {Step}: {Ok} ({Detail})", step.Name, step.Ok, step.Detail);
        }
        catch (Exception ex)
        {
            // RunAsync reports failures as steps rather than throwing, so this is the unexpected
            // case. It still cannot have published anything: nothing in the self-test uploads.
            _logger.Error(ex, "The server check stopped unexpectedly");
            _showInfoDialog("The check stopped", $"{ex.Message}\n\nNothing was changed.");
        }
        finally
        {
            EndServerOperation();
        }
    }

    /// <summary>
    /// Opens the catalog-signing screen: create the key, back it up, restore it.
    ///
    /// <para>Not gated on <see cref="IsNotBusy"/>. Nothing on that screen touches the server, and
    /// the one thing that could collide — exporting a backup while a publish is unsettled — is
    /// refused by the key store itself, which will not write a backup that remembers an attempt
    /// without the bytes it was going to send.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task EditCatalogSigningAsync() => OpenCatalogSigningAsync();

    /// <summary>
    /// Opens the signing screen with the registry's answer already in hand.
    ///
    /// <para>Resolved here rather than inside the screen because the screen is modal and synchronous,
    /// and because the answer decides what it may do at all: a catalog the registry already anchors a
    /// key for must never offer to create another, and a restore must be checked against the key the
    /// REGISTRY names rather than whatever this machine holds — which, on a replacement machine, is
    /// nothing.</para>
    /// </summary>
    private async Task OpenCatalogSigningAsync()
    {
        RegistryTrustState trust;
        try
        {
            var json = await new RegistryVerifiedSource(_registryChecker)
                .ReadVerifiedAsync(_index.PluginId, CancellationToken.None);

            var resolution = IndexProofService.ResolveAnchor(json, _index.PluginId);
            trust = resolution.Status switch
            {
                IndexTrustStatus.Anchored => RegistryTrustState.Anchored(
                    ClaimTrustContext.PublicKeyFingerprint(resolution.Anchor!.PublicKeyPem),
                    resolution.Anchor.KeyId),

                // Deliberately Unreadable rather than NoKeyAnchored. The screen refuses to CREATE a
                // key against an unknown answer, and an entry naming an unusable key is exactly when
                // creating one strands recovery: the new key signs nothing anyone trusts, and it
                // then becomes what the real backup is checked against.
                IndexTrustStatus.Unusable => RegistryTrustState.Unreadable(resolution.Reason!),

                IndexTrustStatus.None => RegistryTrustState.NoKeyAnchored(),

                // Never assigned, so nobody asked the registry. That is Unreadable too — the one
                // state that must not fall through to "no key anchored", which is what permits
                // creating one.
                _ => RegistryTrustState.Unreadable("the registry was not checked")
            };
        }
        catch (Exception ex)
        {
            // Not treated as "no key anchored". Unknown is its own state and the screen refuses to
            // create against it.
            _logger.Warning(ex, "Couldn't read the registry before opening catalog signing");
            trust = RegistryTrustState.Unreadable(ex.Message);
        }

        _showClaimSigningDialog(_index.PluginId, trust);
    }

    /// <summary>Shown when a second server operation is asked for while one is running.</summary>
    private const string AnotherOperationInFlight =
        "This project is already talking to your server. Wait for that to finish, then try again.";

    /// <summary>
    /// Publishes <c>index.json</c> to the author's server (the catalog's canonical home since
    /// GitHub retired).
    ///
    /// <para>Which of two paths it takes is decided by the signed registry, and by nothing else. A
    /// plugin the registry anchors a signing key for goes through
    /// <see cref="IndexPublishCoordinator"/>: locked, signed, journalled, read back and recorded.
    /// A plugin it anchors no key for publishes exactly as it always has. Deliberately not decided
    /// by whether a key exists on this machine — creating a key is a local, private act, and it
    /// must not be able to change how anything publishes until the author puts that key in the
    /// registry on purpose.</para>
    ///
    /// <para>A local git commit records history afterwards, best-effort — git state never decides
    /// whether the publish happened; the live remote does.</para>
    /// </summary>
    /// <returns>
    /// True when the live catalog now describes the local index — either because this publish put
    /// it there, or because it was already saying so. Callers use it to gate work that must only
    /// happen once users can see the change. Notably NOT true after resuming an interrupted
    /// publish, which sends what that attempt prepared rather than what is in the folder now.
    /// </returns>
    private async Task<bool> PublishIndexToServerAsync(string commitMessage, bool confirmFirst)
    {
        // Claimed before anything else, and released in exactly one place. The release flows reach
        // here without going through the Publish command, so the command's own disabled state does
        // not stop a second entry — and a second entry that gave up would otherwise clear the
        // shared busy flag out from under the publish still running.
        if (!TryBeginServerOperation())
        {
            _showInfoDialog("Busy", AnotherOperationInFlight);
            return false;
        }

        try
        {
            return await PublishGuardedAsync(commitMessage, confirmFirst);
        }
        finally
        {
            EndServerOperation();
        }
    }

    private async Task<bool> PublishGuardedAsync(string commitMessage, bool confirmFirst)
    {
        var indexPath = Path.Combine(_projectPath, "index.json");
        byte[] candidate;
        try
        {
            candidate = File.ReadAllBytes(indexPath);
        }
        catch (Exception ex)
        {
            _showInfoDialog("Can't read index.json", ex.Message);
            return false;
        }

        // The manager's own validation, strictly: publishing something users' managers would
        // refuse (or silently drop) is an authoring error caught HERE, not in the field.
        try
        {
            var report = AccessibilityModManager.Infrastructure.Services.PluginIndexValidation
                .Validate(_index.PluginId, Encoding.UTF8.GetString(candidate));
            // Every severity, composed by the report itself — see IndexValidationReport
            // .PublishBlockers for why that decision does not live here.
            var problems = report.PublishBlockers;
            if (problems.Count > 0)
            {
                const int shown = 6;
                var text = string.Join("\n\n", problems.Take(shown));
                if (problems.Count > shown) text += $"\n\n...and {problems.Count - shown} more.";
                _showInfoDialog("Fix the index before publishing", text);
                return false;
            }
        }
        catch (Exception ex)
        {
            _showInfoDialog("Index doesn't validate", ex.Message);
            return false;
        }

        var cfg = _configService.GetServerUploadConfig();
        if (cfg is null)
        {
            _showInfoDialog("Server upload not configured",
                "Publishing sends index.json to your server over SFTP. Set up Server upload " +
                "settings (host, key, host key fingerprint) first.");
            return false;
        }

        PublishResult result;
        try
        {
            result = await _publishCoordinator.PublishAsync(
                new ServerUploadPublishTransport(_serverUploadService, cfg),
                new RegistryVerifiedSource(_registryChecker),
                new PublishRequest(_index.PluginId, candidate)
                {
                    ConfirmOrdinary = confirmFirst,
                    ChangeSummary = commitMessage
                },
                question => _confirmDialog(question.Title, question.Message),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The coordinator returns its outcomes rather than throwing them, and every outcome
            // where something may have gone out is a returned Interrupted — so in practice this
            // is a failure from before anything was sent.
            //
            // It still does not say so. "Nothing was uploaded" is the one claim in this tool
            // that must never rest on reasoning about a path nobody has walked, and this catch
            // exists precisely for the paths nobody thought of. Publishing again is the honest
            // instruction in every case: it reads this machine's journal and the server, and
            // either finishes what was started or explains why it cannot.
            _logger.Error(ex, "Publish stopped unexpectedly");
            _showInfoDialog("Publish stopped",
                $"{ex.Message}\n\nChoose Publish index again — it checks the server before doing " +
                "anything, and will either finish or tell you what it found.");
            return false;
        }

        if (result.Status == PublishStatus.NotSigned)
        {
            // Only one path produces NotSigned and it always carries the registry — but a
            // missing one is answered by refusing rather than by a null-forgiving '!', because
            // the alternative to having it is publishing without ever checking that managers
            // are told to read the address being published to.
            if (result.VerifiedRegistryJson is not { } verifiedRegistry)
            {
                _showInfoDialog("Couldn't check the registry",
                    "The registry was read but didn't come back with the plugin list, so there is " +
                    "no way to confirm this publish would go where managers are told to look. " +
                    "Nothing was uploaded.");
                return false;
            }

            return await PublishUnsignedAsync(
                cfg, candidate, commitMessage, confirmFirst, verifiedRegistry);
        }

        return await PublishPresentation.ApplyAsync(result, _index.PluginId, new PublishEffects(
            RecordPublishedSource: () => RecordPublishedIndex(candidate),
            ShowDialog: _showInfoDialog,
            SetStatus: message => StatusMessage = message,
            OfferKeyBackup: () =>
            {
                var (title, message) = PublishPresentation.FreshBackupPrompt(_index.PluginId);
                if (_confirmDialog(title, message)) _ = OpenCatalogSigningAsync();
            },
            OfferSigningSetup: () =>
            {
                if (_confirmDialog("Open catalog signing?",
                        "The registry names a signing key for this catalog that isn't on this " +
                        "machine. Restoring your key backup would let this copy publish again.\n\n" +
                        "Open the catalog signing screen now?"))
                {
                    // The screen resolves the registry itself, so it opens knowing the anchor
                    // exists and refusing to create a key against it.
                    _ = OpenCatalogSigningAsync();
                }
            }));
    }

    /// <summary>
    /// Publishing as it has always worked, for a plugin the signed registry anchors no key for.
    ///
    /// <para>Reached only through a registry that was fetched and whose signature verified — the
    /// coordinator refuses everything else, and that refusal is what this path is missing on its
    /// own. Its own registry check used to swallow every failure and carry on, so a registry whose
    /// signature did not verify read as "nothing to compare, go ahead". <paramref
    /// name="verifiedRegistryJson"/> is that document, so the address check below is made against
    /// bytes something has vouched for rather than against whatever answered.</para>
    /// </summary>
    private async Task<bool> PublishUnsignedAsync(
        ServerUploadConfig cfg, byte[] candidate, string commitMessage, bool confirmFirst,
        string verifiedRegistryJson)
    {
        // Set the moment the switch returns, and read only by the catch below. Without it, anything
        // that failed after a successful switch would be reported with the message for a failure
        // before one — and "the live index is unchanged" is the single claim here that costs the
        // most when it is wrong.
        var switched = false;

        try
        {
            // Publishing to an address nobody reads is the quietest possible failure: the tool
            // would upload, verify the upload from the public URL, and report success while every
            // manager went on fetching the address the SIGNED registry names.
            //
            // Not being listed at all is fine and common — a plugin nobody has added to the
            // registry yet publishes normally. Being listed with no usable address is not the same
            // thing and is not allowed to pass as it.
            var registered = IndexProofService.TryReadIndexUrl(verifiedRegistryJson, _index.PluginId);

            if (registered.IdCaseDiffers)
            {
                _showInfoDialog("The registry spells this plugin differently",
                    $"This project publishes '{_index.PluginId}', but the registry lists it under a " +
                    "different capitalisation. Nothing here can treat those as the same name: a " +
                    "signing key is matched exactly, so the entry as written anchors no key for this " +
                    "project — and if it ever does anchor one, publishing from here would put an " +
                    "unsigned index over a signed catalog.\n\n" +
                    "Make the id in the registry match this project exactly, then sign and publish " +
                    "the registry. Nothing was uploaded.");
                return false;
            }

            if (registered.Listed && registered.Url is null)
            {
                _showInfoDialog("The registry can't say where this is read",
                    $"The registry lists '{_index.PluginId}' but its entry carries no usable index " +
                    "address, so there is no way to tell whether publishing here would reach anyone. " +
                    "Fix the entry in the registry admin screen, then sign and publish the registry. " +
                    "Nothing was uploaded.");
                return false;
            }

            if (registered.Url is { } address &&
                IndexPublishCoordinator.IndexUrlMismatch(address, _index.PluginId) is { } mismatch)
            {
                _showInfoDialog("The registry points somewhere else", mismatch);
                return false;
            }

            // Third-party-change check: the live index should be either what was live when this
            // project opened, or already the candidate (an interrupted earlier publish).
            var live = await TryFetchLiveIndexAsync();
            if (live is not null && live.AsSpan().SequenceEqual(candidate))
            {
                StatusMessage = "The live index is already identical. Nothing to publish.";
                _liveIndexAtLoad = candidate;
                RecordPublishedIndex(candidate);
                return true;
            }
            if (live is not null && _liveIndexAtLoad is not null &&
                !live.AsSpan().SequenceEqual(_liveIndexAtLoad))
            {
                if (!_confirmDialog("The live index changed",
                    "The index on the server is different from when this project was opened — another " +
                    "publish happened in between. Publishing now REPLACES the server's copy with yours.\n\n" +
                    "Replace it anyway?"))
                {
                    StatusMessage = "Publish cancelled — the server's index was left alone.";
                    return false;
                }
            }

            if (confirmFirst &&
                !_confirmDialog("Publish index",
                    $"This uploads index.json for '{_index.PluginId}' to {cfg.Host} and switches it live " +
                    $"atomically. Managers see the change on their next refresh.\n\nChange: {commitMessage}\n\nProceed?"))
            {
                StatusMessage = "Saved locally. Publish index when ready.";
                return false;
            }

            StatusMessage = "Publishing index...";
            await _serverUploadService.PublishIndexAsync(
                cfg, _index.PluginId, candidate, beforeSwitchAsync: null, CancellationToken.None);
            switched = true;

            var verify = await TryFetchLiveIndexAsync();
            if (verify is null || !verify.AsSpan().SequenceEqual(candidate))
            {
                _showInfoDialog("Published, but verification failed",
                    "The index uploaded and switched live, but reading it back from the public address " +
                    "didn't return the same bytes. Publish again; if it persists, check the server.");
                return false;
            }

            _liveIndexAtLoad = candidate;
            RecordPublishedIndex(candidate);
            StatusMessage = "Published the index and verified it live from the public address.";
            return true;
        }
        catch (IndexPublishFailedException ex) when (ex.RenameAttempted)
        {
            // The switch may have run, so the live index is not known to be anything.
            _logger.Error(ex, "Index publish was interrupted around the switch");
            _showInfoDialog("Publish interrupted",
                $"{ex.Message}\n\nThe index may or may not have switched live. Publish again, and " +
                "check the result.");
            return false;
        }
        catch (Exception ex) when (switched)
        {
            _logger.Error(ex, "Index publish switched live but failed afterwards");
            _showInfoDialog("Published, but something failed afterwards",
                $"{ex.Message}\n\nThe index did switch live, so managers will see it. Publish again to " +
                "be sure the server holds what this folder holds.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Index publish failed");
            _showInfoDialog("Publish failed — the live index is unchanged", ex.Message);
            return false;
        }
    }

    // A server publish used to also make a local git commit that went nowhere. It is gone: for a
    // server project the upload IS the publish and the folder is a working copy, and now that GitHub
    // is a real destination a commit that looks like publishing but isn't is worse than no commit at
    // all. The commit on the GitHub path is the opposite of best-effort — it is the publication.

    /// <summary>
    /// Writes <c>index.json</c> to disk, including all in-progress game edits. Returns false on
    /// failure (caller surfaces nothing extra; the dialog already showed an error).
    /// </summary>
    private bool TrySaveIndexToDisk()
    {
        try
        {
            CommitGameEditsToModel();
            var updated = new PluginRepoIndex
            {
                PluginId = _index.PluginId,
                RepoVersion = _index.RepoVersion,
                GeneratedAt = DateTime.UtcNow,
                Games = _index.Games,
                ReleasesByGameId = _index.ReleasesByGameId,
                Author = _index.Author
            };
            _indexFileService.Save(_projectPath, updated);
            _index = updated;
            HasUnsavedChanges = false;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Save failed");
            _showInfoDialog("Save failed", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// THE publish entry point. Every route — the Publish button, Save, and each release change —
    /// comes through here, so a destination can never apply to one of them and not the others.
    ///
    /// <para>That is not a tidiness argument. Save publishes too, so wiring the destination into
    /// only the Publish button would have left a project set to GitHub quietly uploading to the
    /// server every time the author pressed Save.</para>
    ///
    /// <para>Returns whether the published catalog now describes the local file — the same contract
    /// the server path always had, which the Patreon gate transition depends on.</para>
    /// </summary>
    private async Task<bool> PublishToDestinationAsync(string commitMessage, bool confirmFirst)
    {
        var destination = CurrentDestination;
        if (destination == PublishDestination.Unset)
        {
            destination = AskWhereThisPublishes();
            if (destination == PublishDestination.Unset)
            {
                StatusMessage = "Saved locally. Choose where this index publishes when you're ready.";
                return false;
            }
            _configService.SetPublishDestination(_projectPath, _index.PluginId, destination);
            OnPropertyChanged(nameof(PublishDestinationLabel));
            OnPropertyChanged(nameof(PublishButtonName));
        }

        return destination == PublishDestination.GitHub
            ? await PublishIndexToGitHubAsync(commitMessage, confirmFirst)
            : await PublishIndexToServerAsync(commitMessage, confirmFirst);
    }

    /// <summary>
    /// Publishes by committing and pushing index.json to the project's GitHub repository.
    ///
    /// <para>Unsigned only. A catalog the registry anchors a key for must be published as a signed
    /// one, and <see cref="UnsignedPublishGate"/> refuses here rather than letting a plain push put
    /// an unsigned index over a signed catalog — which cannot be undone by publishing again.</para>
    ///
    /// <para>The gate runs twice: once before anything is touched, for a clear early refusal, and
    /// once with the commit made and the push not yet attempted, because a registry can be
    /// re-pointed or a key anchored in between and the check that counts is the last one.</para>
    /// </summary>
    private async Task<bool> PublishIndexToGitHubAsync(string commitMessage, bool confirmFirst)
    {
        var (target, targetError) = await _gitHubPublisher.ResolveTargetAsync(_projectPath);
        if (target is null)
        {
            _showInfoDialog("Can't publish to GitHub", targetError ?? "This project can't be published to GitHub.");
            StatusMessage = "Saved locally. Publishing to GitHub isn't possible from this folder yet.";
            return false;
        }
        _gitTarget = target;

        var registry = new RegistryVerifiedSource(_registryChecker);
        var authorized = await _unsignedGate.AuthorizeAsync(registry, _index.PluginId, CancellationToken.None);
        if (!authorized.Allowed)
        {
            _showInfoDialog(authorized.Title, authorized.Message);
            StatusMessage = "Saved locally. Nothing was published.";
            return false;
        }

        // A private repository serves 404 to the manager's anonymous fetch, so a push would look
        // like a success nobody could read. Checked BEFORE committing, since it is the author's
        // repository setting rather than anything this publish can fix.
        if (await _gitHubService.IsRepoPrivateAsync($"{target.Owner}/{target.Repo}") is true)
        {
            _showInfoDialog("That repository is private",
                $"{target.Describe} is private, so {target.BranchRawUrl} returns nothing to anyone but " +
                "you — the manager fetches it signed out. Make the repository public before publishing " +
                "its index. Nothing was committed or pushed.");
            StatusMessage = "Saved locally. The repository is private.";
            return false;
        }

        // The address managers are told to read, when the registry lists this plugin at all, must be
        // the one about to be written. Publishing to a place nobody reads is the quietest failure
        // this tool has: everything reports success and no manager sees a thing.
        if (authorized.RegisteredIndexUrl is { } registered &&
            !string.Equals(registered.TrimEnd('/'), target.BranchRawUrl, StringComparison.Ordinal))
        {
            _showInfoDialog("The registry points somewhere else",
                $"The registry tells managers to read '{_index.PluginId}' from:\n\n{registered}\n\n" +
                $"but publishing here would write to:\n\n{target.BranchRawUrl}\n\n" +
                "Publishing now would look like it worked while every manager kept reading the old " +
                "address. Nothing was committed or pushed.");
            StatusMessage = "Saved locally. The registry names a different address.";
            return false;
        }

        var candidate = await File.ReadAllBytesAsync(Path.Combine(_projectPath, "index.json"));

        // Creating a branch publishes everything already committed in the folder, because git pushes
        // a commit with its whole ancestry. Said before it happens, not after.
        var branchExists = await _gitHubPublisher.RemoteBranchExistsAsync(target);
        var branchNote = branchExists is false
            ? $"\n\nBranch '{target.Branch}' doesn't exist on that repository yet, so this creates it — " +
              "which publishes everything already committed in this folder, not just index.json."
            : "";

        if (confirmFirst &&
            !_confirmDialog("Publish index",
                $"This commits index.json for '{_index.PluginId}' and pushes it to {target.Describe}." +
                branchNote + "\n\n" +
                (authorized.Listed
                    ? "Managers see the change on their next refresh."
                    : "Note: this plugin isn't listed in the registry yet, so the manager won't show it " +
                      "to anyone until it is — hosting it is not the same as publishing it.") +
                $"\n\nChange: {commitMessage}\n\nProceed?"))
        {
            StatusMessage = "Saved locally. Publish index when ready.";
            return false;
        }

        StatusMessage = "Publishing index to GitHub...";

        var result = await _gitHubPublisher.PublishAsync(target, candidate, commitMessage,
            async () =>
            {
                var again = await _unsignedGate.AuthorizeAsync(registry, _index.PluginId, CancellationToken.None);
                return again.Allowed ? null : again.Message;
            });

        switch (result.Outcome)
        {
            case GitPublishOutcome.Published:
            case GitPublishOutcome.PublishedPendingCdn:
                // What was COMMITTED, not what was read off disk: the publisher normalizes line
                // endings, and recording the pre-normalization bytes would make the next
                // project-open decide this folder differs from what is live.
                var published = result.PublishedBytes ?? candidate;
                _liveIndexAtLoad = published;
                RecordPublishedIndex(published);
                StatusMessage = authorized.Listed
                    ? $"Published to {target.Describe}."
                    : $"Pushed to {target.Describe}. It still needs adding to the registry before " +
                      "anyone's manager will show it.";
                if (!authorized.Listed)
                {
                    _showInfoDialog("Pushed, but not listed yet",
                        $"index.json is live at:\n\n{target.BranchRawUrl}\n\n" +
                        "The manager only reads catalogs the registry lists, so nobody will see these " +
                        "mods until that exact address is added to the registry and it is signed.");
                }
                return true;

            case GitPublishOutcome.CommittedNotPushed:
                _showInfoDialog(result.Title, result.Message);
                StatusMessage = "Committed locally, but not pushed — managers can't see it.";
                return false;

            default:
                _showInfoDialog(result.Title, result.Message);
                StatusMessage = "Nothing was published.";
                return false;
        }
    }

    /// <summary>
    /// Asks once, and only when nothing is recorded. Deliberately not inferred: this project folder
    /// is a git repository AND publishes to the server, so "is there a remote" would answer GitHub
    /// for the one catalog that must never go there.
    /// </summary>
    private PublishDestination AskWhereThisPublishes()
    {
        var gitHub = _confirmDialog("Where does this index publish?",
            $"'{_index.PluginId}' hasn't been published from this folder before, so there's no record " +
            "of where its index.json belongs.\n\n" +
            "Yes — commit and push it to this project's GitHub repository.\n" +
            "No — upload it to your server over SFTP.\n\n" +
            "You can change this later. Publish to GitHub?");

        return gitHub ? PublishDestination.GitHub : PublishDestination.Server;
    }

    /// <summary>
    /// Auto-save + auto-publish prompt that runs after a release is added or edited. The
    /// release dialog only stages the new <see cref="ModRelease"/> in memory and uploads the
    /// asset — without this, the user has to remember to also click Save and Publish for the
    /// updated SHA256 to actually go live, and a stale live index means the manager rejects
    /// downloads with a hash mismatch. The publish step still confirms first so the author can
    /// defer if they want.
    /// </summary>
    private async Task PublishAfterReleaseChangeAsync(
        string commitMessage, PendingGateChange? gateChange = null)
    {
        if (!TrySaveIndexToDisk())
            return;

        var catalogMatches = await PublishToDestinationAsync(commitMessage, confirmFirst: true);

        if (gateChange == null)
        {
            if (catalogMatches) await VerifyPublicDownloadsAsync();
            return;
        }

        // What the server enforces changes here and nowhere else: only once the public catalog
        // describes the release does the enforcement follow it. A declined or failed publish
        // leaves both the live index and the server as they were — consistent with each other —
        // and saving the release again picks the change back up.
        if (!catalogMatches)
        {
            StatusMessage = gateChange.Gate == null
                ? "The catalog wasn't updated, so the release is still patrons-only on your server. " +
                  "Publish the index to finish making it public."
                : "The catalog wasn't updated, so your server still enforces the old tiers. Publish the " +
                  "index to apply the change.";
            return;
        }

        var cfg = _configService.GetServerUploadConfig();
        if (cfg == null) return;

        try
        {
            if (gateChange.Gate == null)
            {
                await _serverUploadService.RemoveGateAsync(
                    cfg, gateChange.GameId, gateChange.Version, CancellationToken.None);

                StatusMessage = "Published the index, and the release is now public on your server.";
            }
            else
            {
                await _serverUploadService.PublishGateOnlyAsync(
                    cfg, gateChange.GameId, gateChange.Version, gateChange.Gate, CancellationToken.None);
                StatusMessage = "Published the index, and your server now enforces the new tiers.";
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Applying the tier change failed for {Game} v{Version}",
                gateChange.GameId, gateChange.Version);
            _showInfoDialog("The catalog is live, but your server wasn't updated",
                $"Your index now describes {gateChange.GameId} {gateChange.Version}, but changing what the " +
                $"server enforces for it failed:\n\n{ex.Message}\n\n" +
                (gateChange.Gate == null
                    ? "Until that's cleared, patrons can download it and everyone else is turned away."
                    : "Until then, your server still enforces the old tiers.") +
                "\n\nSave the release again to retry.");
            return;
        }

        // Only now — with the catalog live AND the server enforcing what it says — is there a
        // question worth asking about the addresses. Checking before the tier lock came off would
        // have reported a release that is public in the catalog and still gated on the wire, which
        // is a true description of an intermediate state and a useless thing to alarm anyone with.
        await VerifyPublicDownloadsAsync();
    }

    private string SuggestCommitMessage()
    {
        var games = _index.Games.Select(g => g.DisplayName).ToList();
        var releases = _index.ReleasesByGameId
            .SelectMany(kv => kv.Value.Select(r => $"{kv.Key} v{r.Version}"))
            .ToList();

        if (releases.Count == 1) return $"Update index: {releases[0]}";
        if (games.Count == 1 && releases.Count > 0) return $"Update {games[0]} releases";
        return "Update plugin index";
    }

    /// <summary>
    /// Closing is refused while a server operation is running, and that is a safety rule rather
    /// than tidiness.
    ///
    /// <para>The one-at-a-time gate belongs to this editor. Closing mid-publish would not stop the
    /// publish — it keeps running, still holding the lock — but reopening the project builds a
    /// second editor with a fresh gate, which knows nothing about it. That copy could then be
    /// asked whether every other copy is closed, answer yes truthfully, and clear a lock the first
    /// one is still using.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private void CloseProject()
    {
        if (HasUnsavedChanges)
        {
            if (!_confirmDialog("Unsaved changes",
                "You have unsaved changes. Discard and close?"))
                return;
        }
        _closeProject();
    }
}
