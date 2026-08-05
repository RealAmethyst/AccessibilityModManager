using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Tests.Helpers;

/// <summary>
/// User-added sources for tests, with their acceptance bound the way the add flow binds it.
///
/// <para>Built by hand, a source is easy to leave half-valid — an acceptance timestamp with no
/// identity binding now reads as "confirmed for something else" and is refused on load. A test that
/// constructed one of those and then asserted the source was ignored would be passing for the wrong
/// reason: not because of the rule it meant to exercise, but because its fixture was malformed. So
/// building a VALID one goes through here, and a test that wants a broken one breaks it explicitly.</para>
/// </summary>
public static class TestUserSource
{
    public static UserPluginSource Accepted(
        string pluginId, string? displayName = null, string? indexUrl = null)
    {
        var url = indexUrl ?? $"https://example.invalid/{pluginId}/index.json";
        return new UserPluginSource
        {
            PluginId = pluginId,
            IndexUrl = url,
            DisplayName = displayName,
            AddedUtc = DateTimeOffset.UnixEpoch,
            NoticeAcceptedUtc = DateTimeOffset.UnixEpoch,
            AcceptedFor = UserPluginSource.AcceptanceKey(pluginId, url)
        };
    }
}
