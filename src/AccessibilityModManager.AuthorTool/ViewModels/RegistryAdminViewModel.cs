using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.AuthorTool.ViewModels;

/// <summary>
/// Admin-only view for managing the public plugin registry. One-stop shop for the
/// whole publish flow: pick the registry repo, browse open issues, edit plugin-registry.json,
/// commit + push, and sign with the maintainer's RSA private key. Replaces the per-plugin
/// "Sign registry" button so admin work doesn't require opening a plugin project first.
/// </summary>
public sealed partial class RegistryAdminViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions RegistryJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AuthorConfigService _configService;
    private readonly GitHubService _gitHubService;
    private readonly GitService _gitService;
    private readonly IndexValidator _indexValidator;
    private readonly ILogger _logger;
    private readonly Action<string, string> _showInfoDialog;
    private readonly Func<string, string, bool> _confirmDialog;
    private readonly Func<string?, string?> _browseForFolder;
    private readonly Func<string, string, string?, string?> _browseForFile;
    private readonly Action _navigateBack;

    [ObservableProperty]
    private string? _registryRepoPath;

    [ObservableProperty]
    private string? _registryJsonPath;

    [ObservableProperty]
    private string? _registryJsonContent;

    [ObservableProperty]
    private bool _hasUnsavedJsonChanges;

    [ObservableProperty]
    private string? _privateKeyPath;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<IssueListItemViewModel> Issues { get; } = [];

    [ObservableProperty]
    private IssueListItemViewModel? _selectedIssue;

    /// <summary>
    /// Conventional local path for the registry clone. Derived from the hardcoded repo name
    /// so the admin never has to pick — first run clones into this folder, subsequent runs
    /// reuse it.
    /// </summary>
    public static string DefaultRegistryRepoPath => Path.Combine(
        AuthorConfigService.GetReposDirectory(),
        RegistryMembershipChecker.RegistryRepo.Replace('/', '-'));

    public RegistryAdminViewModel(
        AuthorConfigService configService,
        GitHubService gitHubService,
        GitService gitService,
        IndexValidator indexValidator,
        ILogger logger,
        Action<string, string> showInfoDialog,
        Func<string, string, bool> confirmDialog,
        Func<string?, string?> browseForFolder,
        Func<string, string, string?, string?> browseForFile,
        Action navigateBack)
    {
        _configService = configService;
        _gitHubService = gitHubService;
        _gitService = gitService;
        _indexValidator = indexValidator;
        _logger = logger;
        _showInfoDialog = showInfoDialog;
        _confirmDialog = confirmDialog;
        _browseForFolder = browseForFolder;
        _browseForFile = browseForFile;
        _navigateBack = navigateBack;

        // Auto-resolve the registry repo path: hardcoded location, cloned on first use.
        _ = EnsureRepoAndLoadAsync();
    }

    /// <summary>
    /// Clones the registry repo to its conventional location if missing, then loads the
    /// JSON + issues. Runs once at view-open time.
    /// </summary>
    private async Task EnsureRepoAndLoadAsync()
    {
        IsBusy = true;
        try
        {
            var path = DefaultRegistryRepoPath;
            if (!Directory.Exists(path) || !await _gitService.IsRepoAsync(path))
            {
                if (!await _gitService.IsAvailableAsync())
                {
                    StatusMessage = "Git CLI not found. Install Git for Windows to enable the registry admin flow.";
                    return;
                }

                StatusMessage = $"Cloning registry repo into {path}...";
                var url = $"https://github.com/{RegistryMembershipChecker.RegistryRepo}.git";
                var clone = await _gitService.CloneAsync(url, path);
                if (!clone.Success)
                {
                    StatusMessage = $"Clone failed: {clone.Combined}";
                    return;
                }
            }
            else
            {
                // Pull latest so we don't sign stale state.
                StatusMessage = "Updating registry repo (git pull)...";
                var pull = await _gitService.PullAsync(path);
                if (!pull.Success)
                    _logger.Warning("git pull on registry repo failed: {Output}", pull.Combined);
            }

            RegistryRepoPath = path;
            var config = _configService.Load();
            config.LastRegistryRepoPath = path;
            _configService.Save(config);

            await LoadFromRepoAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to ensure registry repo");
            StatusMessage = $"Couldn't set up the registry repo: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshRepoAsync()
    {
        await EnsureRepoAndLoadAsync();
    }

    private async Task LoadFromRepoAsync()
    {
        if (string.IsNullOrEmpty(RegistryRepoPath)) return;

        // Find the registry JSON in the chosen folder. Conventional names first.
        var candidates = new[] { "plugin-registry.json", "registry.json" };
        var jsonPath = candidates
            .Select(name => Path.Combine(RegistryRepoPath, name))
            .FirstOrDefault(File.Exists);

        if (jsonPath == null)
        {
            StatusMessage = $"No plugin-registry.json found in {RegistryRepoPath}. Pick a different folder.";
            RegistryJsonPath = null;
            RegistryJsonContent = null;
            return;
        }

        RegistryJsonPath = jsonPath;
        RegistryJsonContent = File.ReadAllText(jsonPath);
        HasUnsavedJsonChanges = false;
        StatusMessage = $"Loaded {Path.GetFileName(jsonPath)}.";

        await RefreshIssuesAsync();
    }

    [RelayCommand]
    private async Task RefreshIssuesAsync()
    {
        Issues.Clear();
        IsBusy = true;
        try
        {
            if (!await _gitHubService.IsAvailableAsync()) return;
            if (!await _gitHubService.IsAuthenticatedAsync()) return;

            var issues = await _gitHubService.ListIssuesAsync(
                RegistryMembershipChecker.RegistryRepo, limit: 30, state: "open");
            var items = issues.Select(i => new IssueListItemViewModel(i)).ToList();
            foreach (var item in items) Issues.Add(item);
            StatusMessage = $"Loaded {issues.Count} open issues.";

            // Kick off validation for parseable issues in parallel — no need to block the UI.
            foreach (var item in items.Where(i => i.IsParseable))
                _ = ValidateIssueAsync(item);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to list registry issues");
            StatusMessage = $"Couldn't load issues: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ValidateIssueAsync(IssueListItemViewModel item)
    {
        if (item.ParsedEntry is null) return;
        item.IsValidating = true;
        try
        {
            item.Validation = await _indexValidator.ValidateAsync(item.ParsedEntry);
        }
        finally
        {
            item.IsValidating = false;
        }
    }

    [RelayCommand]
    private void OpenIssue(IssueListItemViewModel? item)
    {
        if (item is null || string.IsNullOrEmpty(item.Issue.Url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.Issue.Url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to open issue URL");
        }
    }

    [RelayCommand]
    private async Task AcceptAndMergeAsync(IssueListItemViewModel? item)
    {
        if (item?.ParsedEntry is null) return;
        if (item.Validation is not { Ok: true })
        {
            _showInfoDialog("Validation hasn't passed",
                "Wait for the index.json validation to complete (or fix what failed) before accepting.");
            return;
        }
        if (string.IsNullOrEmpty(RegistryRepoPath) || string.IsNullOrEmpty(RegistryJsonPath))
        {
            _showInfoDialog("Registry repo not loaded", "Click Refresh to set up the registry repo first.");
            return;
        }

        var entry = item.ParsedEntry;
        var diffPreview = JsonSerializer.Serialize(entry, RegistryJsonOptions);
        if (!_confirmDialog($"Accept issue #{item.Issue.Number}",
            $"This will:\n" +
            $"  1. Pull main\n" +
            $"  2. Add the entry below to plugin-registry.json\n" +
            $"  3. Branch + commit + push + open PR\n" +
            $"  4. Squash-merge the PR (closes the issue)\n\n" +
            $"Entry to add:\n\n{diffPreview}\n\nProceed?"))
            return;

        IsBusy = true;
        try
        {
            // Always start from a clean main with latest remote state so we don't merge a stale tree.
            await _gitService.CheckoutAsync(RegistryRepoPath, "main");
            await _gitService.PullAsync(RegistryRepoPath);

            // Re-read JSON from disk in case it changed since the UI loaded it.
            var json = File.ReadAllText(RegistryJsonPath);
            var registry = JsonSerializer.Deserialize<PluginRegistry>(json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                ?? throw new InvalidOperationException("Registry deserialized to null");

            if (registry.Plugins.Any(p => p.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _showInfoDialog("Plugin already listed",
                    $"A plugin with id '{entry.Id}' is already in the registry. If you meant to update it, edit the JSON directly via the editor.");
                return;
            }

            registry.Plugins.Add(entry);
            var newJson = JsonSerializer.Serialize(registry, RegistryJsonOptions);
            File.WriteAllText(RegistryJsonPath, newJson + Environment.NewLine);
            // Reflect the change in the open editor immediately.
            RegistryJsonContent = newJson + Environment.NewLine;
            HasUnsavedJsonChanges = false;

            var branchName = $"add-plugin-{Sanitize(entry.Id)}-issue-{item.Issue.Number}";
            StatusMessage = $"Branch + commit + push for {branchName}...";
            var checkout = await _gitService.CheckoutNewBranchAsync(RegistryRepoPath, branchName);
            if (!checkout.Success) { _showInfoDialog("git checkout failed", checkout.Combined); return; }

            var add = await _gitService.AddAsync(RegistryRepoPath, "plugin-registry.json");
            if (!add.Success) { _showInfoDialog("git add failed", add.Combined); return; }

            var commitMsg = $"Add plugin: {entry.Id}\n\nCloses #{item.Issue.Number}";
            var commit = await _gitService.CommitAsync(RegistryRepoPath, commitMsg);
            if (!commit.Success) { _showInfoDialog("git commit failed", commit.Combined); return; }

            var push = await _gitService.PushNewBranchAsync(RegistryRepoPath, branchName);
            if (!push.Success) { _showInfoDialog("git push failed", push.Combined); return; }

            StatusMessage = "Opening PR and merging...";
            var prNumber = await _gitHubService.CreatePullRequestAsync(
                RegistryMembershipChecker.RegistryRepo,
                headBranch: branchName,
                baseBranch: "main",
                title: $"Add plugin: {entry.Id}",
                body: $"Closes #{item.Issue.Number}\n\nAccepted via Plugin Index Author admin tool.");
            if (prNumber is null)
            {
                _showInfoDialog("PR opened, but couldn't parse number",
                    "Branch was pushed but I couldn't parse the PR number from gh's output. Merge it manually on GitHub.");
                return;
            }

            var merge = await _gitHubService.MergePullRequestAsync(
                RegistryMembershipChecker.RegistryRepo, prNumber.Value, strategy: "squash");
            if (!merge.Success)
            {
                _showInfoDialog("PR merge failed",
                    $"PR #{prNumber} was opened but couldn't auto-merge:\n\n{merge.Combined}\n\n" +
                    "Resolve any conflicts on GitHub and merge manually.");
                return;
            }

            // Pull merged main + clean up the local branch.
            await _gitService.CheckoutAsync(RegistryRepoPath, "main");
            await _gitService.PullAsync(RegistryRepoPath);
            await _gitService.DeleteLocalBranchAsync(RegistryRepoPath, branchName, force: true);

            // Refresh the in-memory editor copy from the new main state.
            RegistryJsonContent = File.ReadAllText(RegistryJsonPath);
            HasUnsavedJsonChanges = false;

            // Remove the now-closed issue from the visible list. (GitHub auto-closed it via
            // "Closes #N" in the squash commit.)
            Issues.Remove(item);

            StatusMessage = $"Merged PR #{prNumber}. Sign + publish to make it live.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Accept-and-merge failed");
            _showInfoDialog("Accept failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RejectUnparseableAsync(IssueListItemViewModel? item)
    {
        if (item is null) return;
        if (item.IsParseable)
        {
            _showInfoDialog("Issue is parseable", "This issue has a valid registry-entry block — use Accept instead.");
            return;
        }
        if (!_confirmDialog("Close issue with template",
            $"Close issue #{item.Issue.Number} with a templated comment asking the author to re-submit via the AuthorTool's Request listing button?"))
            return;

        IsBusy = true;
        try
        {
            var comment =
                $"Hi @{item.Issue.Author} — thanks for the request. We've moved to a structured " +
                "registry entry format. Please use the AuthorTool's **Request listing** button " +
                "(see the README of the registry repo) so the issue body includes the auto-generated " +
                "`registry-entry` JSON block. Closing this one; feel free to re-open a new request.";

            var commentResult = await _gitHubService.AddIssueCommentAsync(
                RegistryMembershipChecker.RegistryRepo, item.Issue.Number, comment);
            if (!commentResult.Success)
            {
                _showInfoDialog("Comment failed", commentResult.Combined);
                return;
            }

            var closeResult = await _gitHubService.CloseIssueAsync(
                RegistryMembershipChecker.RegistryRepo, item.Issue.Number, reason: "not planned");
            if (!closeResult.Success)
            {
                _showInfoDialog("Close failed", closeResult.Combined);
                return;
            }

            Issues.Remove(item);
            StatusMessage = $"Closed issue #{item.Issue.Number} with re-submit template.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Reject failed");
            _showInfoDialog("Reject failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
            else sb.Append('-');
        }
        return sb.ToString().ToLowerInvariant();
    }

    partial void OnRegistryJsonContentChanged(string? value)
    {
        // Once user has touched the JSON, mark dirty so they don't lose edits silently.
        if (!string.IsNullOrEmpty(RegistryJsonPath) && File.Exists(RegistryJsonPath))
        {
            try
            {
                var diskContent = File.ReadAllText(RegistryJsonPath);
                HasUnsavedJsonChanges = !string.Equals(value, diskContent, StringComparison.Ordinal);
            }
            catch
            {
                HasUnsavedJsonChanges = true;
            }
        }
    }

    [RelayCommand]
    private void SaveJson()
    {
        if (string.IsNullOrEmpty(RegistryJsonPath)) return;
        if (RegistryJsonContent is null) return;

        try
        {
            // Validate JSON before writing.
            using var _ = System.Text.Json.JsonDocument.Parse(RegistryJsonContent);
        }
        catch (Exception ex)
        {
            _showInfoDialog("Invalid JSON",
                $"The content isn't valid JSON. Fix it before saving:\n\n{ex.Message}");
            return;
        }

        File.WriteAllText(RegistryJsonPath, RegistryJsonContent);
        HasUnsavedJsonChanges = false;
        StatusMessage = $"Saved {Path.GetFileName(RegistryJsonPath)}. The .sig is now stale — sign before pushing.";
    }

    [RelayCommand]
    private void PickPrivateKey()
    {
        var path = _browseForFile(
            "Select your encrypted private key (PEM)",
            "PEM files (*.pem;*.key)|*.pem;*.key|All files (*.*)|*.*",
            null);
        if (string.IsNullOrEmpty(path)) return;
        PrivateKeyPath = path;
    }

    /// <summary>
    /// Signs the registry JSON with the chosen private key. Password chars come from the
    /// view's PasswordBox; we zero them after use.
    /// </summary>
    public void Sign(char[] passwordChars)
    {
        if (HasUnsavedJsonChanges)
        {
            _showInfoDialog("Save first",
                "The JSON has unsaved changes. Click \"Save JSON\" before signing so the signature matches what's on disk.");
            return;
        }
        if (string.IsNullOrEmpty(RegistryJsonPath))
        {
            StatusMessage = "Pick the registry repo first.";
            return;
        }
        if (string.IsNullOrEmpty(PrivateKeyPath))
        {
            StatusMessage = "Pick your private key file first.";
            return;
        }

        IsBusy = true;
        try
        {
            var json = File.ReadAllText(RegistryJsonPath);
            var pem = File.ReadAllText(PrivateKeyPath);

            using var rsa = RSA.Create();
            rsa.ImportFromEncryptedPem(pem, passwordChars);

            var data = Encoding.UTF8.GetBytes(json);
            var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            var sigBase64 = Convert.ToBase64String(signature);

            var sigPath = RegistryJsonPath + ".sig";
            File.WriteAllText(sigPath, sigBase64);

            StatusMessage = $"Signed. Wrote {Path.GetFileName(sigPath)} ({signature.Length} bytes). Commit + push when ready.";
            _logger.Information("Signed registry {Json} -> {Sig}", RegistryJsonPath, sigPath);
        }
        catch (CryptographicException ex)
        {
            _logger.Error(ex, "Crypto error during sign");
            StatusMessage = "Signing failed (likely wrong password or unreadable key): " + ex.Message;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Sign failed");
            StatusMessage = $"Sign failed: {ex.Message}";
        }
        finally
        {
            for (int i = 0; i < passwordChars.Length; i++) passwordChars[i] = '\0';
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PublishReleaseAsync()
    {
        if (string.IsNullOrEmpty(RegistryJsonPath))
        {
            _showInfoDialog("Registry not loaded", "Set up the registry repo first.");
            return;
        }
        var sigPath = RegistryJsonPath + ".sig";
        if (!File.Exists(sigPath))
        {
            _showInfoDialog("No signature found",
                "Sign the registry JSON before publishing — the manager won't accept an unsigned release.");
            return;
        }

        // Replay-guard discipline (audit finding 19): the manager refuses a registry whose
        // content changed without a higher registryVersion, so publishing must enforce the bump
        // HERE — republishing an unchanged-version registry would strand every up-to-date user.
        string registryVersion;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RegistryJsonPath));
            registryVersion = doc.RootElement.GetProperty("registryVersion").GetString()
                ?? throw new InvalidOperationException("registryVersion is null");
        }
        catch (Exception ex)
        {
            _showInfoDialog("Can't read registryVersion",
                $"The registry JSON has no readable registryVersion field:\n\n{ex.Message}");
            return;
        }

        var lastPublished = ReadLastPublishedVersion();
        if (!string.IsNullOrEmpty(lastPublished) &&
            VersionComparer.Instance.Compare(registryVersion, lastPublished) <= 0)
        {
            _showInfoDialog("Version bump needed",
                $"registryVersion is still {registryVersion}, but {lastPublished} was already published from " +
                "this machine. Managers refuse a changed registry that doesn't raise its version.\n\n" +
                "Edit registryVersion in the JSON to a higher value, Save, Sign, and publish again.");
            return;
        }

        // Tag based on UTC timestamp keeps releases unique without bookkeeping. The manager
        // fetches /releases/latest/download/... so the tag itself is informational; users
        // never see it.
        var tag = $"r-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        if (!_confirmDialog("Publish release",
            $"This runs `gh release create {tag}` on {RegistryMembershipChecker.RegistryRepo} with " +
            $"plugin-registry.json + plugin-registry.json.sig attached. The manager auto-updates from " +
            $"the latest release, so users will see the change on their next launch. Proceed?"))
            return;

        IsBusy = true;
        try
        {
            var result = await _gitHubService.CreateReleaseAsync(
                RegistryMembershipChecker.RegistryRepo,
                tag,
                title: tag,
                notes: "Registry update.",
                new[] { RegistryJsonPath, sigPath });
            if (!result.Success)
            {
                _showInfoDialog("Publish failed", result.Combined);
                return;
            }
            var markerSaved = WriteLastPublishedVersion(registryVersion);
            StatusMessage = $"Published release {tag} (registry v{registryVersion}). Live for users on next manager refresh." +
                (markerSaved ? "" : " Warning: couldn't record the published version locally — remember to bump registryVersion yourself before the next publish.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Publish failed");
            _showInfoDialog("Publish failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static readonly string LastPublishedMarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager.AuthorTool", "registry-last-published.txt");

    private string? ReadLastPublishedVersion()
    {
        try
        {
            return File.Exists(LastPublishedMarkerPath)
                ? File.ReadAllText(LastPublishedMarkerPath).Trim()
                : null;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't read last-published registry version marker");
            return null;
        }
    }

    private bool WriteLastPublishedVersion(string version)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastPublishedMarkerPath)!);
            File.WriteAllText(LastPublishedMarkerPath, version);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't persist last-published registry version marker");
            return false;
        }
    }

    [RelayCommand]
    private async Task CommitAndPushAsync()
    {
        if (string.IsNullOrEmpty(RegistryRepoPath)) return;

        IsBusy = true;
        try
        {
            if (!await _gitService.IsRepoAsync(RegistryRepoPath))
            {
                _showInfoDialog("Not a git repo", $"{RegistryRepoPath} is not a git repository.");
                return;
            }

            // Stage everything that changed in the working tree (typically the JSON + .sig).
            var status = await _gitService.StatusPorcelainAsync(RegistryRepoPath);
            if (string.IsNullOrWhiteSpace(status.Stdout))
            {
                _showInfoDialog("Nothing to commit", "Working tree is clean — nothing to push.");
                return;
            }

            var addAll = await _gitService.AddAsync(RegistryRepoPath, ".");
            if (!addAll.Success)
            {
                _showInfoDialog("git add failed", addAll.Combined);
                return;
            }

            var defaultMessage = "Update plugin registry";
            if (!_confirmDialog("Commit and push",
                $"Commit message:\n\n{defaultMessage}\n\nProceed with commit and push?"))
            {
                return;
            }

            StatusMessage = "Committing...";
            var commit = await _gitService.CommitAsync(RegistryRepoPath, defaultMessage);
            if (!commit.Success)
            {
                _showInfoDialog("git commit failed", commit.Combined);
                return;
            }

            StatusMessage = "Pushing...";
            var push = await _gitService.PushAsync(RegistryRepoPath);
            if (!push.Success)
            {
                _showInfoDialog("git push failed", push.Combined);
                return;
            }

            StatusMessage = "Pushed. Remember to publish the registry as a GitHub release so the manager picks it up.";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Commit/push failed");
            _showInfoDialog("Commit/push failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        if (HasUnsavedJsonChanges)
        {
            if (!_confirmDialog("Unsaved changes",
                "The registry JSON has unsaved changes. Discard and go back?"))
                return;
        }
        _navigateBack();
    }
}
