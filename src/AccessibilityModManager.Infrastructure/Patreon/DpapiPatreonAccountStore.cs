using System.Security.Cryptography;
using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Patreon;

/// <summary>
/// DPAPI-encrypted token store (Q5=B). The blob lives in
/// <c>%LocalAppData%\AccessibilityModManager\patreon.dat</c> and is encrypted with the
/// current user's Windows credentials — copying the file to another machine or another
/// Windows user yields garbage. Same security envelope a Windows password manager uses.
/// </summary>
public sealed class DpapiPatreonAccountStore : IPatreonAccountStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager",
        "patreon.dat");

    // DPAPI requires entropy to be the same on read + write. Using a fixed salt is fine
    // — DPAPI's per-user key is the actual secret.
    private static readonly byte[] Entropy = "AMM:Patreon:v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger _logger;

    public DpapiPatreonAccountStore(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Written by sign-out, honoured by load. It exists for the case where the token file can be
    /// neither overwritten nor deleted — locked by a backup agent, denied by permissions — which
    /// used to mean a user who signed out was quietly signed back in on the next launch, with a
    /// token that still worked. A tombstone is a different file, so it usually succeeds where
    /// touching the token failed; while it's there, any token beside it is ignored.
    /// </summary>
    private static readonly string TombstonePath = FilePath + ".signedout";

    public Task<PatreonAccount?> LoadAsync()
    {
        if (!File.Exists(FilePath)) return Task.FromResult<PatreonAccount?>(null);

        if (File.Exists(TombstonePath))
        {
            _logger.Information("Ignoring a stored Patreon token — it was signed out but couldn't be removed");
            TryRemoveTokenFile();
            return Task.FromResult<PatreonAccount?>(null);
        }

        try
        {
            var encrypted = File.ReadAllBytes(FilePath);
            var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var account = JsonSerializer.Deserialize<PatreonAccount>(json, JsonOptions);
            return Task.FromResult<PatreonAccount?>(account);
        }
        catch (CryptographicException ex)
        {
            // Either the file was copied from another user/machine, or it's been tampered
            // with. Either way, we can't recover — wipe and treat as signed-out.
            _logger.Warning(ex, "Couldn't decrypt patreon.dat — treating as signed out");
            try { File.Delete(FilePath); } catch { }
            return Task.FromResult<PatreonAccount?>(null);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read patreon.dat");
            return Task.FromResult<PatreonAccount?>(null);
        }
    }

    public Task SaveAsync(PatreonAccount account)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(account, JsonOptions);
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, encrypted);
        // A new session outranks any leftover sign-out marker — without this, signing back in
        // after a sign-out that couldn't delete the file would be ignored on the next launch.
        TryDelete(TombstonePath);
        _logger.Information("Saved patreon account for user {UserId}", account.UserId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears a tombstoned token, tombstone LAST and only if the token itself is gone. Dropping
    /// the marker while the token it guards is still locked would hand the next launch a working
    /// session — the marker is the only thing standing between the user and the sign-out they
    /// asked for.
    /// </summary>
    private void TryRemoveTokenFile()
    {
        if (TryDelete(FilePath))
            TryDelete(TombstonePath);
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Couldn't delete {Path}", path);
            return false;
        }
    }

    public Task ClearAsync()
    {
        if (!File.Exists(FilePath)) return Task.CompletedTask;

        // Three attempts at making the session unusable, weakest requirement last, because a
        // sign-out that silently doesn't stick is the worst outcome here (audit finding 36).
        // Deletion can fail for ordinary reasons — a backup agent holding the file, a synced
        // folder, permissions — and the old code just logged and moved on, leaving a working
        // token that signed the user back in on the next launch.
        var neutralized = false;

        try
        {
            // 1. Overwrite: whatever survives is undecryptable, which loads as signed-out.
            File.WriteAllBytes(FilePath, "signed-out"u8.ToArray());
            neutralized = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't overwrite patreon.dat");
        }

        try
        {
            // 2. Delete. Normal path; also cleans up after a successful overwrite.
            File.Delete(FilePath);
            TryDelete(TombstonePath);
            _logger.Information("Cleared patreon account storage");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Couldn't delete patreon.dat");
        }

        if (!neutralized)
        {
            try
            {
                // 3. A tombstone beside it. A different file, so it usually writes even when the
                // token itself is locked; LoadAsync refuses any token standing next to one.
                File.WriteAllBytes(TombstonePath, "signed-out"u8.ToArray());
                _logger.Warning("patreon.dat could be neither overwritten nor deleted — marked it signed-out instead");
            }
            catch (Exception ex)
            {
                _logger.Error(ex,
                    "SIGN-OUT INCOMPLETE: patreon.dat could not be overwritten, deleted, or marked. " +
                    "The stored session may still load on the next launch");
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory entitlement cache. Q4=A says recheck on every install attempt — but back-to-back
/// reads inside a single install (e.g. enumerate releases on Game Details, then the install
/// itself) shouldn't double-call the API. This holds the last-fetched memberships so the
/// VM layer doesn't have to plumb them through.
/// </summary>
public sealed class PatreonEntitlementCache : IPatreonEntitlementCache
{
    private IReadOnlyList<PatreonMembership> _memberships = [];
    private bool _hasFresh;

    public bool HasFresh => _hasFresh;
    public IReadOnlyList<PatreonMembership> Memberships => _memberships;

    public void Set(IReadOnlyList<PatreonMembership> memberships)
    {
        _memberships = memberships;
        _hasFresh = true;
    }

    public void Invalidate()
    {
        _memberships = [];
        _hasFresh = false;
    }
}
