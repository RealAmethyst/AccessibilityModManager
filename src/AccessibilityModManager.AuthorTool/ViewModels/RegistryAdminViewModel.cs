using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using AccessibilityModManager.Infrastructure.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AccessibilityModManager.AuthorTool.ViewModels;

/// <summary>
/// Admin-only view for managing the public plugin registry. One-stop shop for the
/// whole publish flow: pick the registry repo, edit plugin-registry.json, sign it with the
/// maintainer's RSA private key, and publish the signed pair to the server. Replaces the
/// per-plugin "Sign registry" button so admin work doesn't require opening a plugin project
/// first.
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
    private readonly ServerUploadService _serverUploadService;

    /// <summary>Live-catalog fetches (pre-publish comparison, post-publish verification).</summary>
    private static readonly System.Net.Http.HttpClient CatalogHttp = new();
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

    private readonly ClaimSigningKeyStore _claimKeys;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

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
        ServerUploadService serverUploadService,
        ClaimSigningKeyStore claimKeys,
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
        _serverUploadService = serverUploadService;
        _claimKeys = claimKeys;
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
    /// JSON. Runs once at view-open time.
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

        SyncFieldsFromJson();
    }

    // ---- Quick fields over the raw JSON (the outage lesson: editing the index URL meant
    // hand-editing a JSON blob in a screen reader; these are real, labeled fields for the
    // values that actually get edited. They read from and write into RegistryJsonContent, so
    // Save / Sign / Publish stay exactly as they are.) ----

    [ObservableProperty]
    private string? _fieldRegistryVersion;

    public System.Collections.ObjectModel.ObservableCollection<string> PluginIds { get; } = [];

    [ObservableProperty]
    private string? _selectedPluginId;

    [ObservableProperty]
    private string? _fieldRepoIndexUrl;

    [ObservableProperty]
    private string? _fieldWebsite;

    /// <summary>Guards against feedback while fields and JSON update each other.</summary>
    private bool _syncingFields;

    private void SyncFieldsFromJson()
    {
        if (_syncingFields) return;
        System.Text.Json.Nodes.JsonNode? root;
        try
        {
            root = string.IsNullOrWhiteSpace(RegistryJsonContent)
                ? null
                : System.Text.Json.Nodes.JsonNode.Parse(RegistryJsonContent);
        }
        catch
        {
            return; // mid-edit invalid JSON — fields keep their last values until it parses again
        }
        if (root is null) return;

        _syncingFields = true;
        try
        {
            FieldRegistryVersion = root["registryVersion"]?.GetValue<string>();

            var ids = new List<string>();
            if (root["plugins"] is System.Text.Json.Nodes.JsonArray plugins)
            {
                foreach (var p in plugins)
                {
                    if (p?["id"]?.GetValue<string>() is { Length: > 0 } id)
                        ids.Add(id);
                }
            }
            if (!ids.SequenceEqual(PluginIds, StringComparer.Ordinal))
            {
                PluginIds.Clear();
                foreach (var id in ids) PluginIds.Add(id);
            }
            if (SelectedPluginId is null || !ids.Contains(SelectedPluginId, StringComparer.Ordinal))
                SelectedPluginId = ids.FirstOrDefault();

            var plugin = FindPluginNode(root, SelectedPluginId);
            FieldRepoIndexUrl = plugin?["repoIndexUrl"]?.GetValue<string>();
            FieldWebsite = plugin?["website"]?.GetValue<string>();
        }
        finally
        {
            _syncingFields = false;
        }
    }

    private static System.Text.Json.Nodes.JsonNode? FindPluginNode(
        System.Text.Json.Nodes.JsonNode root, string? pluginId)
    {
        if (pluginId is null || root["plugins"] is not System.Text.Json.Nodes.JsonArray plugins)
            return null;
        return plugins.FirstOrDefault(p =>
            string.Equals(p?["id"]?.GetValue<string>(), pluginId, StringComparison.Ordinal));
    }

    partial void OnSelectedPluginIdChanged(string? value)
    {
        if (_syncingFields) return;
        // Selection changes just re-point the fields; nothing is written.
        SyncFieldsFromJson();
    }

    partial void OnFieldRegistryVersionChanged(string? value) => ApplyFieldsToJson();
    partial void OnFieldRepoIndexUrlChanged(string? value) => ApplyFieldsToJson();
    partial void OnFieldWebsiteChanged(string? value) => ApplyFieldsToJson();

    private void ApplyFieldsToJson()
    {
        if (_syncingFields) return;
        if (string.IsNullOrWhiteSpace(RegistryJsonContent)) return;

        System.Text.Json.Nodes.JsonNode? root;
        try
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(RegistryJsonContent);
        }
        catch
        {
            StatusMessage = "The JSON box has a syntax error — fix it before the fields can apply.";
            return;
        }
        if (root is null) return;

        // The version must never be emptied; a non-https or empty index URL must never reach
        // the JSON (the manager would refuse the whole registry). While a URL is mid-typing
        // the apply simply waits — the field keeps the text, the JSON keeps its last value.
        if (!string.IsNullOrWhiteSpace(FieldRegistryVersion))
            root["registryVersion"] = FieldRegistryVersion.Trim();

        var plugin = FindPluginNode(root, SelectedPluginId);
        if (plugin is not null)
        {
            var url = FieldRepoIndexUrl?.Trim();
            if (!string.IsNullOrWhiteSpace(url) &&
                Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                parsed.Scheme == Uri.UriSchemeHttps)
            {
                plugin["repoIndexUrl"] = url;
            }

            var website = FieldWebsite?.Trim();
            if (string.IsNullOrWhiteSpace(website))
            {
                plugin.AsObject().Remove("website");
            }
            else if (Uri.TryCreate(website, UriKind.Absolute, out var siteParsed) &&
                     siteParsed.Scheme == Uri.UriSchemeHttps)
            {
                plugin["website"] = website;
            }
        }

        // The guard stops the changed-handler from re-syncing the fields mid-typing; its
        // dirty-check still runs, which is exactly right.
        _syncingFields = true;
        try
        {
            RegistryJsonContent = root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        finally
        {
            _syncingFields = false;
        }
    }

    /// <summary>
    /// Writes the selected plugin's local signing key into its registry entry, and raises the
    /// registry version.
    ///
    /// <para>This is the act that turns signing on — not here, but two steps later. Editing the
    /// JSON changes nothing anyone can see; signing the registry and publishing it is what every
    /// manager then reads, and from that moment every publish of that plugin has to be signed by
    /// this key. So the confirmation says so plainly, and everything that can be checked first is
    /// checked first.</para>
    ///
    /// <para>An existing, different <c>indexTrust</c> is refused rather than overwritten. Replacing
    /// it is a key rotation: every claim already published stops verifying the moment the new
    /// registry goes live, so it needs its own deliberate flow rather than a button that happens to
    /// overwrite.</para>
    /// </summary>
    [RelayCommand]
    private void UseLocalSigningKey()
    {
        if (SelectedPluginId is not { Length: > 0 } pluginId)
        {
            _showInfoDialog("Pick a plugin first",
                "Choose which plugin's entry should name a signing key.");
            return;
        }

        if (_claimKeys.TryGet(pluginId) is not { } signing)
        {
            _showInfoDialog("There is no key for that plugin on this machine",
                $"'{pluginId}' has no signing key here. Open the project and use Catalog signing to " +
                "create one, then come back.\n\nNothing was changed.");
            return;
        }

        System.Text.Json.Nodes.JsonNode? root;
        try
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(RegistryJsonContent ?? "");
        }
        catch (Exception ex)
        {
            _showInfoDialog("The registry JSON doesn't parse", ex.Message);
            return;
        }

        if (root is null || FindPluginNode(root, pluginId) is not { } plugin)
        {
            _showInfoDialog("That plugin isn't listed",
                $"The registry has no entry for '{pluginId}', so there is nothing to add a key to.");
            return;
        }

        // Anchoring a key against an address managers are not sent to would publish signed
        // catalogs nobody reads — and publishing refuses afterwards, which is a confusing place to
        // discover it. The same comparison the publish path makes, made here first.
        if (plugin["repoIndexUrl"]?.GetValue<string>() is { } registered &&
            IndexPublishCoordinator.IndexUrlMismatch(registered, pluginId) is { } mismatch)
        {
            _showInfoDialog("Fix the index address first", mismatch);
            return;
        }

        if (plugin["indexTrust"] is System.Text.Json.Nodes.JsonObject existing)
        {
            var same =
                existing["scheme"]?.GetValue<string>() == ClaimTrustAnchor.SchemeV1 &&
                existing["keyId"]?.GetValue<string>() == signing.KeyId &&
                existing["algorithm"]?.GetValue<string>() == ClaimTrustAnchor.AlgorithmRsaPssSha256 &&
                existing["publicKeyPem"]?.GetValue<string>() == signing.PublicKeyPem;

            if (same)
            {
                StatusMessage = $"'{pluginId}' already names this key. Nothing to change.";
                return;
            }

            _showInfoDialog("That entry already names a different key",
                $"'{pluginId}' is anchored to key '{existing["keyId"]?.GetValue<string>() ?? "unknown"}', " +
                $"and this machine holds '{signing.KeyId}'.\n\n" +
                "Replacing it would stop every claim already published from verifying, the moment " +
                "this registry went live. That is a key rotation, and it needs to be done " +
                "deliberately rather than by this button.\n\nNothing was changed.");
            return;
        }

        var version = FieldRegistryVersion?.Trim();
        if (!long.TryParse(version, out var current) || current < 0)
        {
            _showInfoDialog("The registry version has to be a whole number",
                $"It currently reads '{version}'. Managers refuse a registry older than one they " +
                "have already seen, and that comparison needs a number.\n\nNothing was changed.");
            return;
        }

        if (!_confirmDialog("Name this key in the registry?",
                $"This adds key '{signing.KeyId}' to the entry for '{pluginId}' and raises the " +
                $"registry version to {current + 1}.\n\n" +
                "Fingerprint: " + signing.PublicKeyFingerprint + "\n\n" +
                "Nothing goes live yet — you still have to sign the registry and publish it. But " +
                "once you do, every later publish of that plugin has to be signed by this key, and " +
                "changing that means another signed registry.\n\n" +
                "Add it?"))
        {
            StatusMessage = "Left the registry as it was.";
            return;
        }

        plugin["indexTrust"] = new System.Text.Json.Nodes.JsonObject
        {
            ["scheme"] = ClaimTrustAnchor.SchemeV1,
            ["keyId"] = signing.KeyId,
            ["algorithm"] = ClaimTrustAnchor.AlgorithmRsaPssSha256,
            ["publicKeyPem"] = signing.PublicKeyPem
        };

        // Through the field, so the version box and the JSON cannot disagree.
        _syncingFields = true;
        try
        {
            RegistryJsonContent = root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            FieldRegistryVersion = (current + 1).ToString();
        }
        finally
        {
            _syncingFields = false;
        }

        ApplyFieldsToJson();

        _logger.Information("Anchored claim signing key {KeyId} for {PluginId} in the registry JSON",
            signing.KeyId, pluginId);

        StatusMessage = $"'{pluginId}' now names key '{signing.KeyId}', version {current + 1}. " +
                        "Sign the registry, then publish it — that is when signing starts.";
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
        StatusMessage = $"Saved {Path.GetFileName(RegistryJsonPath)}. The signature is now stale — sign it, then publish.";
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

            StatusMessage = $"Signed. Wrote {Path.GetFileName(sigPath)} ({signature.Length} bytes). Publish when ready to make it live.";
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

        // Finding 8: a .sig that merely EXISTS is not a signed registry. Edit, save, forget to
        // re-sign, publish — and every manager in the world fails verification and shows an
        // empty catalog. Verify the signature against the exact on-disk bytes with the same
        // public key the manager embeds, and refuse to ship a mismatch. The bytes read here are
        // the ones verified AND the ones parsed for the version below — one read, no window.
        byte[] registryBytes;
        byte[] sigFileBytes;
        try
        {
            registryBytes = File.ReadAllBytes(RegistryJsonPath);
            sigFileBytes = File.ReadAllBytes(sigPath);
            var sigBase64 = Encoding.UTF8.GetString(sigFileBytes).Trim();
            using var rsa = RSA.Create();
            rsa.ImportFromPem(RegistryTrustKey.PublicKeyPem);
            if (!rsa.VerifyData(registryBytes, Convert.FromBase64String(sigBase64),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            {
                _showInfoDialog("Signature is stale",
                    "The signature on disk does not match the current registry JSON — the JSON changed " +
                    "after it was signed (or the signature came from a different key). Publishing it would " +
                    "break every manager.\n\nClick Sign, then publish again.");
                return;
            }
        }
        catch (Exception ex)
        {
            _showInfoDialog("Can't verify the signature",
                $"The signature file couldn't be checked against the registry JSON:\n\n{ex.Message}\n\n" +
                "Sign again, then publish.");
            return;
        }

        // The manager accepts or refuses the registry as a whole — one unsafe id or one non-https
        // link anywhere in it takes the catalog down for everybody, signature and all. So it is
        // held to the manager's own rules before it goes live, not after.
        try
        {
            var report = AccessibilityModManager.Infrastructure.Services.PluginRegistryValidation
                .Validate(Encoding.UTF8.GetString(registryBytes));
            if (!report.IsValid)
            {
                _showInfoDialog("Fix the registry before publishing",
                    "Managers would refuse this whole registry — every plugin would disappear for every " +
                    "user, even though the signature is valid:\n\n" +
                    string.Join("\n\n", report.Errors));
                return;
            }
        }
        catch (Exception ex)
        {
            _showInfoDialog("The registry doesn't validate", ex.Message);
            return;
        }

        // Replay-guard discipline (audit finding 19): the manager refuses a registry whose
        // content changed without a higher registryVersion, so publishing must enforce the bump
        // HERE — republishing an unchanged-version registry would strand every up-to-date user.
        string registryVersion;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(registryBytes);
            registryVersion = doc.RootElement.GetProperty("registryVersion").GetString()
                ?? throw new InvalidOperationException("registryVersion is null");
        }
        catch (Exception ex)
        {
            _showInfoDialog("Can't read registryVersion",
                $"The registry JSON has no readable registryVersion field:\n\n{ex.Message}");
            return;
        }

        // A UTF-8 BOM would make the served bytes differ from what this tool signs and saves —
        // refuse it here rather than let managers refuse the whole registry.
        if (registryBytes.Length >= 3 &&
            registryBytes[0] == 0xEF && registryBytes[1] == 0xBB && registryBytes[2] == 0xBF)
        {
            _showInfoDialog("File starts with a byte-order mark",
                "The registry JSON starts with a UTF-8 byte-order mark, which the manager doesn't expect. " +
                "Open the JSON here, click Save JSON (this tool saves without one), sign, and publish again.");
            return;
        }

        var cfg = _configService.GetServerUploadConfig();
        if (cfg is null)
        {
            _showInfoDialog("Server upload not configured",
                "Publishing sends the registry to your server over SFTP. Set up Server upload " +
                "settings (host, key, host key fingerprint) first.");
            return;
        }

        // What is actually published decides everything below — the local marker is only a
        // fallback. The public address is the manager's view, so it's asked first; if it can't
        // be reached, the server itself is asked over SFTP, because "I couldn't read it" and
        // "there is nothing there" must never be confused: publishing an older version on top of
        // a newer one strands every manager that already recorded the newer one.
        var liveJsonBytes = await TryFetchLiveAsync(RegistryMembershipChecker.RegistryUrl);
        var liveReadFailed = false;
        if (liveJsonBytes is null)
        {
            try
            {
                var (remoteJson, _) = await _serverUploadService.ReadPublishedRegistryAsync(cfg, CancellationToken.None);
                liveJsonBytes = remoteJson;
                if (remoteJson is not null)
                    _logger.Information("Public registry unreachable; read the published copy over SFTP instead");
            }
            catch (Exception ex)
            {
                liveReadFailed = true;
                _logger.Warning(ex, "Couldn't read the published registry over SFTP either");
            }
        }

        if (liveReadFailed)
        {
            _showInfoDialog("Can't tell what's currently published",
                $"Neither the public address nor {cfg.Host} would say what registry is live right now. " +
                "Publishing blind risks replacing a newer registry with an older one, which every " +
                "up-to-date manager would then refuse.\n\nFix the connection and try again. Nothing was uploaded.");
            return;
        }

        // Byte-identical JSON is NOT proof the catalog is healthy: if the signature rename failed
        // after the JSON rename, managers are seeing these bytes with the previous signature and
        // refusing the whole catalog. So the pair is verified before this is called a no-op, and
        // a broken pair is republished at the same version to repair it.
        var repairingPair = false;
        if (liveJsonBytes is not null && liveJsonBytes.AsSpan().SequenceEqual(registryBytes))
        {
            var pairProblem = await VerifyLivePairAsync(registryBytes);
            if (pairProblem is null)
            {
                WriteLastPublishedVersion(registryVersion);
                StatusMessage = $"The live registry is already byte-identical to this v{registryVersion}. Nothing to publish.";
                return;
            }
            repairingPair = true;
            _logger.Warning("Live registry bytes match but the pair is broken ({Problem}) — republishing to repair", pairProblem);
        }
        var liveUnparseable = false;
        if (!repairingPair && liveJsonBytes is not null)
        {
            try
            {
                using var liveDoc = System.Text.Json.JsonDocument.Parse(liveJsonBytes);
                var liveVersion = liveDoc.RootElement.GetProperty("registryVersion").GetString();
                if (!string.IsNullOrEmpty(liveVersion) &&
                    VersionComparer.Instance.Compare(registryVersion, liveVersion) <= 0)
                {
                    _showInfoDialog("Version bump needed",
                        $"The LIVE registry is already v{liveVersion}, and this file is v{registryVersion} with " +
                        "different content. Managers refuse a changed registry that doesn't raise its version.\n\n" +
                        "Edit registryVersion to a higher value, Save, Sign, and publish again.");
                    return;
                }
            }
            catch (Exception ex)
            {
                // Publishing over an unreadable live registry is allowed — that's how a corrupt
                // one gets repaired — but it happens with the author's eyes open, because the
                // version gate that normally prevents a downgrade can't run.
                liveUnparseable = true;
                _logger.Warning(ex, "The live registry couldn't be parsed for version comparison");
            }
        }

        // The local marker only speaks up when there is genuinely nothing published to compare
        // against — a fresh catalog on a machine that has published before.
        if (!repairingPair && liveJsonBytes is null)
        {
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
        }

        var confirmText = repairingPair
            ? $"The registry published on {cfg.Host} has the right contents but a signature that doesn't " +
              "match them, so managers are refusing the whole catalog right now. Re-publishing v" +
              $"{registryVersion} repairs the pair.\n\nRepair it?"
            : $"This uploads plugin-registry.json v{registryVersion} and its signature to {cfg.Host} and switches " +
              "them live atomically. Every manager sees the change on its next refresh." +
              (liveJsonBytes is null
                  ? "\n\nNote: nothing is published there yet — this is the first publish."
                  : "") +
              (liveUnparseable
                  ? "\n\nWarning: the registry currently published there can't be read as JSON, so its version " +
                    "couldn't be compared with yours. If what's live is actually NEWER than v" + registryVersion +
                    ", publishing this would roll managers back and they'd refuse it. Check the file on the " +
                    "server if you're not sure."
                  : "") +
              "\n\nProceed?";
        if (!_confirmDialog(repairingPair ? "Repair the published registry" : "Publish registry", confirmText))
            return;

        IsBusy = true;
        try
        {
            await _serverUploadService.PublishRegistryPairAsync(cfg, registryBytes, sigFileBytes, CancellationToken.None);

            // Trust nothing until it's proven from the USER'S side: fetch both public files
            // fresh and verify the signature over the exact served bytes.
            var verifyError = await VerifyLivePairAsync(registryBytes);
            if (verifyError != null)
            {
                _showInfoDialog("Published, but verification failed",
                    "The registry uploaded and switched live, but reading it back from the public address " +
                    $"didn't check out: {verifyError}\n\nDo NOT leave this unresolved — managers may be failing. " +
                    "Publish again; if it persists, check the server.");
                return;
            }

            var markerSaved = WriteLastPublishedVersion(registryVersion);
            StatusMessage = $"Published registry v{registryVersion} and verified it live from the public address." +
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

    /// <summary>Cache-busted fetch of a live catalog file; null on any failure.</summary>
    private static async Task<byte[]?> TryFetchLiveAsync(Uri url)
    {
        try
        {
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

    /// <summary>
    /// End-to-end proof after publishing: the PUBLIC registry URL serves exactly the uploaded
    /// bytes and its signature verifies over them with the manager's key. Null when everything
    /// checks out; otherwise a short description of what didn't.
    /// </summary>
    private async Task<string?> VerifyLivePairAsync(byte[] expectedJson)
    {
        var json = await TryFetchLiveAsync(RegistryMembershipChecker.RegistryUrl);
        if (json is null) return "the live registry couldn't be fetched";
        if (!json.AsSpan().SequenceEqual(expectedJson)) return "the live registry's bytes differ from what was uploaded";

        var sig = await TryFetchLiveAsync(new Uri(RegistryMembershipChecker.RegistryUrl.AbsoluteUri + ".sig"));
        if (sig is null) return "the live signature couldn't be fetched";

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(RegistryTrustKey.PublicKeyPem);
            var sigBytes = Convert.FromBase64String(Encoding.UTF8.GetString(sig).Trim());
            if (!rsa.VerifyData(json, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                return "the live signature does not verify over the live bytes";
        }
        catch (Exception ex)
        {
            return "the live signature couldn't be checked: " + ex.Message;
        }

        return null;
    }

    /// <summary>
    /// The registry's PUBLIC verification key — the same PEM the manager embeds
    /// (App.xaml.cs, GetRegistryPublicKey). Used to prove, before anything ships, that the
    /// on-disk .sig matches the on-disk JSON. Public keys are safe to embed; only the private
    /// key is sensitive, and it never leaves the maintainer's chosen key file.
    /// </summary>

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

            StatusMessage = "Pushed. That's local history only — click Publish to change what managers actually read.";
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
