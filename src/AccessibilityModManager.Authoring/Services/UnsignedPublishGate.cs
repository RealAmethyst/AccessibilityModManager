using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// Whether an UNSIGNED index may be published for a plugin, and where the registry says managers
/// read it.
///
/// <para>Every answer is derived from a freshly fetched, signature-checked registry. Nothing here
/// consults local state for permission — local absence of a key is not evidence of remote absence
/// of one, and a machine that has never imported a key is exactly the machine that would otherwise
/// conclude a signed catalog is unsigned and publish plaintext over it.</para>
///
/// <para><b>Fails closed.</b> An unreachable registry, a bad signature, a replayed older registry,
/// a malformed signing block, an entry differing only in capitalisation, or an anchor that says
/// this catalog IS signed all refuse. Only <see cref="IndexTrustStatus.None"/> from a current
/// registry, on a machine with no signed publishing history for the plugin, allows it.</para>
///
/// <para>The equivalent rules for the SERVER path live in <see cref="IndexPublishCoordinator"/>,
/// which additionally owns signing. Converging the two on this type is worthwhile and deliberately
/// not done in the same change that introduced it: that path is the one Amethyst publishes her live
/// catalog with, and it deserves its own verification round.</para>
/// </summary>
public sealed class UnsignedPublishGate(PublisherHeadStore headStore)
{
    /// <summary>Refused, or cleared with what the registry says about where this index lives.</summary>
    public sealed record Decision(bool Allowed, string Title, string Message)
    {
        /// <summary>True when the registry lists this plugin at all.</summary>
        public bool Listed { get; init; }

        /// <summary>
        /// The address the registry sends managers to, when it lists one. Null when the plugin is
        /// not listed yet — a normal state, and NOT a failure: the index can be hosted before it is
        /// listed. It does mean no manager will read it until the registry catches up, which the
        /// caller has to say out loud rather than call it "published".
        /// </summary>
        public string? RegisteredIndexUrl { get; init; }
    }

    private static Decision Refuse(string title, string message) => new(false, title, message);

    /// <summary>
    /// Runs the whole gate. Cheap enough to repeat immediately before the irreversible step, which
    /// is the point: a registry can be re-pointed or a key anchored between opening a publish and
    /// pushing it, and the check that matters is the last one.
    /// </summary>
    public async Task<Decision> AuthorizeAsync(
        IVerifiedRegistrySource registry, string pluginId, CancellationToken ct)
    {
        string registryJson;
        try
        {
            registryJson = await registry.ReadVerifiedAsync(pluginId, ct);
        }
        catch (RegistryUnusableException ex)
        {
            return Refuse("Couldn't check the registry",
                $"{ex.Message}\n\nThe registry is what says whether this catalog is signed and where " +
                "managers read it, so publishing without it is refused. Nothing was published.");
        }

        // A valid signature proves authenticity, not freshness. A replayed older registry is
        // cryptographically perfect and names whatever key and address were current before a
        // re-point retired them.
        try
        {
            headStore.RequireRegistryNotOlder(registryJson);
        }
        catch (Exception ex)
        {
            return Refuse("The registry looks older than one already seen",
                $"{ex.Message}\n\nNothing was published.");
        }

        var resolution = IndexProofService.ResolveAnchor(registryJson, pluginId);
        switch (resolution.Status)
        {
            case IndexTrustStatus.Anchored:
                return Refuse("This catalog is signed",
                    $"The registry anchors a signing key for '{pluginId}', so its index has to be " +
                    "published as a signed catalog. Publishing an unsigned one over it would break " +
                    "every manager that has already read the signed version, and it can't be undone.\n\n" +
                    "Signed catalogs publish to the server. Nothing was published.");

            case IndexTrustStatus.Unusable:
                return Refuse("The registry's signing key can't be used",
                    $"{resolution.Reason}\n\nUntil that entry is fixed there is no way to tell whether " +
                    "this catalog should be signed, so publishing is refused. Nothing was published.");

            case IndexTrustStatus.None:
                break;

            default:
                // Unresolved means nobody actually asked the registry, and an unasked question is
                // not an answer — least of all the one that grants the unsigned path.
                return Refuse("The registry wasn't checked",
                    "The signing state for this catalog was never resolved from the registry, so " +
                    "there is nothing to publish against. Nothing was published.");
        }

        // No anchor AND a signed history behind us is not "this catalog is unsigned" — it is the
        // registry having moved backwards, or the entry having been edited.
        if (headStore.RecordsFor(pluginId).Count > 0)
        {
            return Refuse("This machine has published this catalog signed",
                $"There are signed publishing records for '{pluginId}' on this machine, but the " +
                "registry now anchors no key for it. Publishing an unsigned index would strand every " +
                "manager that has already read a signed one. Nothing was published.");
        }

        var address = IndexProofService.TryReadIndexUrl(registryJson, pluginId);

        if (address.IdCaseDiffers)
        {
            return Refuse("The registry spells this plugin differently",
                $"The registry lists this plugin under a different capitalisation of '{pluginId}'. " +
                "Identity is matched exactly, so that entry describes a different plugin as far as " +
                "every check here is concerned — and if it carries a signing key pointing at the same " +
                "place, publishing would put an unsigned index over a signed catalog. Fix the " +
                "spelling in the registry. Nothing was published.");
        }

        if (address.Listed && string.IsNullOrWhiteSpace(address.Url))
        {
            return Refuse("The registry entry has no usable address",
                $"'{pluginId}' is listed in the registry but its entry does not say where managers " +
                "read the index, so there is no way to confirm this publish would go anywhere they " +
                "look. Nothing was published.");
        }

        return new Decision(true, "Ready to publish", "The registry allows an unsigned publish for this plugin.")
        {
            Listed = address.Listed,
            RegisteredIndexUrl = address.Url
        };
    }
}
