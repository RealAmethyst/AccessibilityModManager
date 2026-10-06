using System.Text.Json;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Patreon;

/// <summary>Stores a Patreon session in the desktop Secret Service.</summary>
public sealed class SecretServicePatreonAccountStore(string accountName = "patreon-manager")
    : IPatreonAccountStore
{
    private const string Label = "Accessibility Mod Manager Patreon";
    private const string SessionOnlyMarkerText = "session-only\n";
    private const string SignedOutMarkerText = "signed-out\n";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly string SignedOutMarker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessibilityModManager", "patreon-linux.signedout");

    private string SessionAccountName => accountName + "-session";

    public bool IsSessionOnly { get; private set; }

    public async Task<PatreonAccount?> LoadAsync()
    {
        IsSessionOnly = false;
        var marker = File.Exists(SignedOutMarker) ? File.ReadAllText(SignedOutMarker) : null;
        if (marker is not null && marker != SessionOnlyMarkerText) return null;
        string? value = null;
        if (marker is null)
        {
            try { value = await Task.Run(() => SecretServiceValueStore.Lookup(accountName)); }
            catch (Exception ex) when (IsStoreUnavailable(ex))
            {
                // The login collection may be locked while the session collection is usable.
            }
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            value = await Task.Run(() => SecretServiceValueStore.Lookup(SessionAccountName));
            IsSessionOnly = !string.IsNullOrWhiteSpace(value);
        }
        return string.IsNullOrWhiteSpace(value) ? null :
            JsonSerializer.Deserialize<PatreonAccount>(value, JsonOptions) ??
            throw new InvalidDataException("The stored Patreon session is unreadable.");
    }

    public async Task SaveAsync(PatreonAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var value = JsonSerializer.Serialize(account, JsonOptions);
        if (!IsSessionOnly)
        {
            try
            {
                await Task.Run(() => SecretServiceValueStore.Store(accountName, Label, value));
                if (File.Exists(SignedOutMarker)) File.Delete(SignedOutMarker);
                return;
            }
            catch (Exception ex) when (IsStoreUnavailable(ex))
            {
                // A locked login collection must not hold the completed OAuth flow open.
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(SignedOutMarker)!);
        File.WriteAllText(SignedOutMarker, SessionOnlyMarkerText);
        await Task.Run(() => SecretServiceValueStore.Store(
            SessionAccountName, Label, value,
            collection: "/org/freedesktop/secrets/collection/session"));
        IsSessionOnly = true;
    }

    public async Task ClearAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SignedOutMarker)!);
        File.WriteAllText(SignedOutMarker, SignedOutMarkerText);
        try { await Task.Run(() => SecretServiceValueStore.Clear(SessionAccountName)); }
        catch (Exception ex) when (IsStoreUnavailable(ex))
        {
            // The marker prevents a failed clear from restoring this session on next launch.
        }
        try { await Task.Run(() => SecretServiceValueStore.Clear(accountName)); }
        catch (Exception ex) when (IsStoreUnavailable(ex))
        {
            // Keep the marker until a later successful persistent sign-in supersedes it.
        }
        IsSessionOnly = false;
    }

    private static bool IsStoreUnavailable(Exception ex) =>
        ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception;
}
