using System.Security.Cryptography;
using System.Text.Json;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.CatalogClaims;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>What opening a project should do about the copy that is published.</summary>
public enum ReconcileAction
{
    /// <summary>Leave the folder alone and say nothing. Nothing is wrong and nothing has moved.</summary>
    Nothing,

    /// <summary>
    /// This catalog is not signed, so the caller's existing unsigned reconciliation applies
    /// unchanged. Not a failure — it is today's normal state.
    /// </summary>
    Unsigned,

    /// <summary>
    /// Replace the local catalog with the verified one. Only when this folder is still exactly what
    /// was last published or last adopted, so there is nothing in it to lose.
    /// </summary>
    Adopt,

    /// <summary>
    /// The same replacement, but this folder holds work that was never published. Ask first, and
    /// default to keeping it: unpublished work is the one thing here that exists nowhere else.
    /// </summary>
    AdoptWithConsent,

    /// <summary>Leave the folder alone and tell the author why.</summary>
    Explain
}

/// <summary>
/// The answer, and the document to write when there is one.
/// </summary>
/// <param name="Document">
/// The exact bytes to write, complete — built from the verified catalog plus the author's own
/// fields. There is deliberately no "here are the live bytes, merge them yourself": every caller
/// that has to do the merging is a caller that can get it wrong.
/// </param>
public sealed record ReconcileOutcome(ReconcileAction Action, string? Message, byte[]? Document)
{
    /// <summary>Which publish the adopted catalog is, for the message the author reads.</summary>
    public long? Generation { get; init; }
}

/// <summary>
/// Decides what to do when a project is opened and the published catalog might have moved on.
///
/// <para>Opening a project is the one moment the tool writes over the author's folder without being
/// asked to, which makes it a place where a hostile server gets to choose content the author will
/// later sign. So under a signed catalog nothing is adopted that has not been rebuilt from claims
/// this machine verified, and nothing is adopted at all unless the published head is the one this
/// machine last confirmed.</para>
///
/// <para>Whether any of that applies is decided by the ANCHOR, not by whether a key happens to exist
/// here. A plugin the registry anchors no key for reconciles exactly as it always has — otherwise
/// switching signing on would change how every unsigned project opens, before the author had
/// switched anything on.</para>
/// </summary>
public sealed class ProjectReconciler(
    PublisherHeadStore headStore, IndexProofService proofs, ILogger logger)
{
    /// <param name="published">
    /// Null when this machine has no server connection configured. Deliberately nullable rather than
    /// a stand-in object built from an empty configuration: a reader that cannot read is a state to
    /// report, and one assembled from blank settings is a publish target of "" waiting to be handed
    /// to something that writes.
    /// </param>
    /// <param name="lastPublishedLocalSha">
    /// SHA-256 of the local bytes this folder last published or last adopted, or null when there is
    /// no such record. It is what separates "this folder is stale" from "this folder has work in it"
    /// — two situations that look identical from the file alone and want opposite answers.
    /// </param>
    public async Task<ReconcileOutcome> InspectAsync(
        IPublishedIndexReader? published, IVerifiedRegistrySource registry,
        string pluginId, byte[] localIndexJson, string? lastPublishedLocalSha, CancellationToken ct)
    {
        string registryJson;
        try
        {
            registryJson = await registry.ReadVerifiedAsync(pluginId, ct);
        }
        catch (RegistryUnusableException ex)
        {
            // Nothing is adopted and nothing is said. Opening a project offline is ordinary, and the
            // registry being unreadable is already visible elsewhere in this screen.
            logger.Information(ex, "Couldn't read the registry while opening {PluginId}", pluginId);
            return new ReconcileOutcome(ReconcileAction.Nothing, null, null);
        }

        var resolution = IndexProofService.ResolveAnchor(registryJson, pluginId);
        if (resolution.Status == IndexTrustStatus.Unusable)
        {
            // Nothing is adopted on the strength of an entry that cannot say which key signs this
            // catalog. Adopting the live plaintext here is the same mistake as publishing it.
            return new ReconcileOutcome(ReconcileAction.Explain,
                $"The registry's signing key for this catalog can't be used: {resolution.Reason}. " +
                "Your local project was left alone.", null);
        }

        if (resolution.Status == IndexTrustStatus.None)
        {
            // A signed history behind us and no anchor in front is not "this catalog is unsigned" —
            // it is the registry having moved backwards or the entry having been edited, and
            // adopting plaintext on the strength of it is exactly what nothing here may do.
            if (headStore.RecordsFor(pluginId).Count > 0)
            {
                return new ReconcileOutcome(ReconcileAction.Explain,
                    "This machine has published this catalog with a signing key, but the registry no " +
                    "longer names a key for it. Your local project was left alone.", null);
            }

            return new ReconcileOutcome(ReconcileAction.Unsigned, null, null);
        }

        // Anchored is all that is left that may proceed, asked for by name rather than inferred from
        // an anchor being present. Any other state means the registry was never actually consulted,
        // and nothing is adopted on the strength of a question nobody asked.
        if (resolution.Status != IndexTrustStatus.Anchored || resolution.Anchor is not { } anchor)
        {
            return new ReconcileOutcome(ReconcileAction.Explain,
                "The signing key for this catalog was never resolved from the registry, so nothing " +
                "was adopted. Your local project was left alone.", null);
        }

        // This machine's own blockers first, before anything is fetched. They are what the author has
        // to act on, they cannot be answered by looking at the server, and asking the server first
        // meant a dropped connection could hide an interrupted publish behind a shrug.
        var trustContext = ClaimTrustContext.Compute(anchor);
        var record = headStore.TryLoad(trustContext);

        if (record?.Pending is not null)
        {
            return new ReconcileOutcome(ReconcileAction.Explain,
                $"A publish of this catalog was interrupted and hasn't been settled yet, so nothing " +
                "was adopted. Choose Publish to finish it.", null);
        }

        if (headStore.HasUnconfirmedRestoredState(pluginId, trustContext))
        {
            return new ReconcileOutcome(ReconcileAction.Explain,
                "This machine's publishing history came from a backup and hasn't been confirmed by a " +
                "publish since, so what's on the server can't be checked against it yet. Your local " +
                "project was left alone.", null);
        }

        if (published is null)
        {
            return new ReconcileOutcome(ReconcileAction.Explain,
                "This catalog is signed, but server upload isn't set up on this machine — so what's " +
                "published couldn't be read and checked. Your local project was left alone.", null);
        }

        ServerUploadService.RemoteIndex live;
        try
        {
            live = await published.ReadIndexAsync(pluginId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Said rather than shrugged off. For a signed catalog, "I could not check" is a
            // different thing from "nothing has changed", and the author is about to edit a copy
            // that nothing has confirmed is current.
            logger.Warning(ex, "Couldn't read the published index while opening {PluginId}", pluginId);
            return new ReconcileOutcome(ReconcileAction.Explain,
                $"Couldn't read the published catalog to compare it with this folder ({ex.Message}). " +
                "Your local project was left alone.", null);
        }

        IndexProofService.VerifiedCatalog? catalog;
        try
        {
            catalog = proofs.ReadPublishedCatalog(anchor, live.Present ? live.Bytes : null);
        }
        catch (Exception ex)
        {
            // A proof that exists and does not verify means the registry now names a different key,
            // or that file was altered. Neither is a catalog to take anything from.
            return new ReconcileOutcome(ReconcileAction.Explain,
                "The proof on the published index doesn't verify against the key the registry names, " +
                $"so nothing was taken from it and your local project was left alone.\n\n{ex.Message}", null);
        }

        if (catalog is null)
        {
            return new ReconcileOutcome(ReconcileAction.Explain,
                "The published index carries no signature yet, so there is nothing here that can be " +
                "checked before adopting it. Your local project was kept.", null);
        }

        if (record?.Committed is null)
        {
            return new ReconcileOutcome(ReconcileAction.Explain,
                $"There is a signed catalog on the server — publish {catalog.Generation} — but this " +
                "machine has no record of publishing it. That happens on a new computer, or after " +
                "restoring a profile, and it is also what a rolled-back server looks like, so nothing " +
                "was adopted. Import this machine's publishing backup.", null);
        }

        if (!string.Equals(record.Committed.ManifestHash, catalog.ManifestHash, StringComparison.Ordinal))
        {
            return new ReconcileOutcome(ReconcileAction.Explain,
                $"The server says publish {catalog.Generation}; this machine last confirmed publish " +
                $"{record.Committed.Generation}. Adopting a catalog this machine didn't publish would " +
                "take content from somewhere it can't account for, so nothing was changed.", null);
        }

        var document = IndexProofService.BuildLocalDocument(catalog, localIndexJson);

        // Already says the same thing. Saying so out loud on every open would be noise, and
        // rewriting a file to its own contents is a modification time nobody asked for.
        if (document.AsSpan().SequenceEqual(localIndexJson))
            return new ReconcileOutcome(ReconcileAction.Nothing, null, null);

        // Refuse to hand back a document this machine could not read back, rather than discovering
        // it after the author's folder has been replaced with it.
        try
        {
            _ = JsonSerializer.Deserialize<PluginRepoIndex>(document, DocumentOptions)
                ?? throw new InvalidOperationException("it came back empty");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "The catalog rebuilt from claims is not a readable index");
            return new ReconcileOutcome(ReconcileAction.Explain,
                "The published catalog verified, but the index rebuilt from it could not be read " +
                $"back ({ex.Message}). Nothing was changed.", null);
        }

        // "Different" covers two opposite situations and only one of them is safe to act on. If this
        // folder is still exactly what was last published or last adopted, then the catalog moved on
        // without it and replacing it loses nothing. If it has been edited since, those edits exist
        // in precisely one place in the world, and taking the published copy would end that.
        var unpublishedWork =
            lastPublishedLocalSha is null ||
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(localIndexJson)), lastPublishedLocalSha,
                StringComparison.OrdinalIgnoreCase);

        return new ReconcileOutcome(
            unpublishedWork ? ReconcileAction.AdoptWithConsent : ReconcileAction.Adopt,
            unpublishedWork
                ? $"The catalog published on your server is publish {catalog.Generation}, and it isn't " +
                  "the same as the copy in this folder — and this folder has changes that were never " +
                  "published.\n\nTaking the published copy would discard those changes. Your presets " +
                  "and default scripts are kept either way.\n\nReplace this folder's copy with " +
                  $"publish {catalog.Generation}?"
                : null,
            document)
        {
            Generation = catalog.Generation
        };
    }

    private static readonly JsonSerializerOptions DocumentOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
