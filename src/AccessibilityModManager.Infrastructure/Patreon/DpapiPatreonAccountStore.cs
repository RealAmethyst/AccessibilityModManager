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

    public Task<PatreonAccount?> LoadAsync()
    {
        if (!File.Exists(FilePath)) return Task.FromResult<PatreonAccount?>(null);

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
        _logger.Information("Saved patreon account for user {UserId}", account.UserId);
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
                _logger.Information("Cleared patreon account storage");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to clear patreon.dat");
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
