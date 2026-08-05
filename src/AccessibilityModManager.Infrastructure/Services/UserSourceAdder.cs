using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>What a candidate source turned out to be, or why it cannot be added.</summary>
public sealed record SourcePreview(
    bool CanAdd,
    string? Refusal,
    string PluginId = "",
    string DisplayName = "",
    string IndexUrl = "",
    int GameCount = 0);

/// <summary>
/// Looks at an address the user typed and decides whether it can become a source.
///
/// <para>Everything here happens BEFORE the risk notice and before anything is written: the catalog
/// is fetched, parsed and checked, and the identity it claims is tested against the registry, the
/// user's existing sources and their installed mods. So the notice the user reads names a real
/// developer with a real number of mods, rather than asking them to approve an address that might
/// turn out to be nothing.</para>
///
/// <para>Nothing is cached and nothing is saved from here, and that is enforced rather than
/// intended: the fetch goes through <see cref="IPluginRepoClient.FetchIndexUncachedAsync"/>, which
/// neither reads a saved copy nor writes one. A candidate the user then cancels must leave no
/// trace — otherwise "cancel" would still have taught the manager something about a source they
/// decided against, and a cached copy could later make an unreachable address look healthy.</para>
/// </summary>
public sealed class UserSourceAdder(IPluginRepoClient repoClient, ILogger logger)
{
    /// <param name="address">The address the user typed.</param>
    /// <param name="registryPlugins">Entries from the accepted, signature-verified registry.</param>
    /// <param name="existingSources">Sources already configured.</param>
    /// <param name="installedPluginIds">Plugin ids with something installed under them.</param>
    public async Task<SourcePreview> PreviewAsync(
        string? address,
        IReadOnlyList<PluginEntry> registryPlugins,
        IReadOnlyList<UserPluginSource> existingSources,
        IReadOnlyList<string> installedPluginIds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Refuse("Enter the address of the source you want to add.");

        var trimmed = address.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url))
            return Refuse("That doesn't look like a web address.");

        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return Refuse("Sources have to use a secure address, starting with https.");

        PluginRepoIndex index;
        try
        {
            // Fetched as a user source from the start. Previewing it as anything else would mean
            // the document that gets checked is read under different rules from the one that will
            // be read later — and the whole point is to check the thing that will actually be used.
            // The id is not known yet — it is whatever this catalog declares about itself, and
            // ForPreview is the one construction that says so. A placeholder id here refused every
            // real catalog, because no genuine index claims to be called "candidate".
            var candidate = CatalogSource.ForPreview(url);

            // Uncached on purpose: a cached copy would let this succeed while the address is
            // currently unreachable, and a cache WRITE would leave a trace of a source the user is
            // about to decline — one shared trace, since a candidate has no id of its own yet.
            index = await repoClient.FetchIndexUncachedAsync(candidate, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Host only. This address was typed by the user and NOT accepted; a full URL can carry
            // a query or credentials, and a log is the wrong place for either — least of all for
            // something they decided against.
            logger.Warning(ex, "Couldn't read a candidate source at {Host}", url.Host);
            return Refuse("Couldn't read a mod catalog at that address. " +
                          CatalogRefusedException.SpeakableReason(ex));
        }

        var pluginId = index.PluginId;
        if (!SafeId.IsValid(pluginId, out var idReason))
            return Refuse($"That source's developer id can't be used because {idReason}.");

        var displayName = index.Author?.DisplayName?.Trim() ?? pluginId;

        if (ReservedDeveloperNames.IsReserved(displayName))
        {
            return Refuse(
                "That source presents itself under a developer name it isn't allowed to use.");
        }

        var clash = CatalogSourceResolver.CanAdd(
            registryPlugins, existingSources, installedPluginIds, pluginId);
        if (clash is not null)
            return Refuse($"That source can't be added because {clash}.");

        var games = index.Games
            .Count(g => index.ReleasesByGameId.TryGetValue(g.GameId, out var releases) && releases.Count > 0);

        return new SourcePreview(
            CanAdd: true,
            Refusal: null,
            PluginId: pluginId,
            DisplayName: displayName,
            IndexUrl: url.AbsoluteUri,
            GameCount: games);
    }

    /// <summary>
    /// Builds the record to persist once the user has accepted the notice. The acceptance is bound
    /// to the identity it was given for, so a later edit of either field returns it to unconfirmed.
    /// </summary>
    public static UserPluginSource Accept(SourcePreview preview, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.CanAdd)
            throw new InvalidOperationException("A source that was refused cannot be accepted.");

        return new UserPluginSource
        {
            PluginId = preview.PluginId,
            IndexUrl = preview.IndexUrl,
            DisplayName = preview.DisplayName,
            AddedUtc = now,
            NoticeAcceptedUtc = now,
            AcceptedFor = UserPluginSource.AcceptanceKey(preview.PluginId, preview.IndexUrl)
        };
    }

    private static SourcePreview Refuse(string reason) => new(CanAdd: false, Refusal: reason);
}
