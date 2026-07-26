using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>What this machine last confirmed live for one trust context.</summary>
public sealed record PublisherHead
{
    [JsonPropertyName("generation")]
    public required long Generation { get; init; }

    [JsonPropertyName("manifestHash")]
    public required string ManifestHash { get; init; }
}

/// <summary>
/// A publish that has been prepared but not yet confirmed.
///
/// Written before anything is uploaded. Between the remote rename and the local commit there is an
/// interval where this machine may have published and has no record of it — and under a hostile
/// server that lost record is exactly what lets the next attempt sign a second, different publish
/// at the same generation. The journal is what survives that interval.
/// </summary>
public sealed record PendingPublish
{
    /// <summary>The head this publish extends; null when it is the first.</summary>
    [JsonPropertyName("baseManifestHash")]
    public string? BaseManifestHash { get; init; }

    [JsonPropertyName("generation")]
    public required long Generation { get; init; }

    [JsonPropertyName("manifestHash")]
    public required string ManifestHash { get; init; }

    /// <summary>
    /// SHA-256 of the exact index bytes prepared for this publish, which are kept beside the
    /// record. A retry re-sends those bytes verbatim rather than rebuilding: RSA-PSS is randomised,
    /// so a rebuild at the same generation produces different bytes under the same counter — the
    /// fork this whole mechanism exists to prevent, arriving by way of the recovery path.
    /// </summary>
    [JsonPropertyName("indexSha256")]
    public required string IndexSha256 { get; init; }
}

public sealed record PublisherRecord
{
    [JsonPropertyName("v")]
    public int V { get; init; } = 1;

    [JsonPropertyName("trustContext")]
    public required string TrustContext { get; init; }

    [JsonPropertyName("pluginId")]
    public required string PluginId { get; init; }

    [JsonPropertyName("committed")]
    public PublisherHead? Committed { get; init; }

    [JsonPropertyName("pending")]
    public PendingPublish? Pending { get; init; }
}

/// <summary>
/// Where the publisher's own view of its history lives.
///
/// Deliberately NOT in the AuthorTool's general config. That file catches every load error and
/// starts fresh, which is right for preferences and catastrophic here: a corrupt file would
/// silently become "this key has never published", the single most permissive state there is, and
/// the one that lets a replayed server state be adopted as the truth. So this store is its own
/// file per trust context, parsed strictly, and an unreadable record blocks publishing rather than
/// being replaced with an empty one.
///
/// Keyed by the full trust context — not by plugin id, and not by key id. Re-pointing an index
/// URL, changing a key id, or rotating a key all change what claims are bound to, and each one
/// starts a namespace of its own rather than inheriting a head that no longer means anything.
/// </summary>
public sealed class PublisherHeadStore(AuthorConfigService configService, ILogger logger)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private string Directory => Path.Combine(configService.StorageDirectory, "publisher");

    /// <summary>
    /// The trust context is 64 hex characters, so it is its own safe file name — nothing here is
    /// ever built from a plugin id or any other value that arrived from outside.
    /// </summary>
    private string PathFor(string trustContext)
    {
        if (trustContext.Length != 64 || !trustContext.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("A trust context is 64 lowercase hex characters.", nameof(trustContext));

        return Path.Combine(Directory, $"{trustContext}.json");
    }

    private string PendingIndexPathFor(string trustContext) =>
        Path.ChangeExtension(PathFor(trustContext), ".pending-index.json");

    /// <summary>
    /// The record for a trust context, or null when this machine has never published under it.
    ///
    /// Throws rather than returning null when a record exists but cannot be read. "I have never
    /// seen this" and "my record is damaged" must not collapse into one answer, because only one of
    /// them is safe to act on.
    /// </summary>
    public PublisherRecord? TryLoad(string trustContext)
    {
        var path = PathFor(trustContext);
        if (!File.Exists(path)) return null;

        PublisherRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<PublisherRecord>(File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"This machine's publishing record for the catalog can't be read ({path}). " +
                "Publishing is blocked until it is: without it there is no way to tell a normal " +
                "publish from a server that has been rolled back.", ex);
        }

        if (record is null || record.V != 1 ||
            !string.Equals(record.TrustContext, trustContext, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This machine's publishing record at {path} is for something else, or is not a " +
                "record this version understands. Publishing is blocked until it is sorted out.");
        }

        return record;
    }

    /// <summary>The exact bytes a pending publish prepared, if they are still on disk.</summary>
    public byte[]? TryReadPendingIndex(string trustContext)
    {
        var path = PendingIndexPathFor(trustContext);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// Records a publish that is about to be attempted, together with the bytes it will send.
    /// Durable before anything touches the server — that ordering is the whole point.
    /// </summary>
    public void WritePending(
        string trustContext, string pluginId, PublisherHead? committed, PendingPublish pending, byte[] indexBytes)
    {
        System.IO.Directory.CreateDirectory(Directory);
        WriteAtomic(PendingIndexPathFor(trustContext), indexBytes);

        Write(new PublisherRecord
        {
            TrustContext = trustContext,
            PluginId = pluginId,
            Committed = committed,
            Pending = pending
        });

        logger.Information(
            "Journalled publish {Generation} for {PluginId} before uploading", pending.Generation, pluginId);
    }

    /// <summary>
    /// Promotes the pending publish to committed, once the live index has been read back and
    /// verified as the one that was sent.
    /// </summary>
    public void Commit(string trustContext, string pluginId, PublisherHead head)
    {
        System.IO.Directory.CreateDirectory(Directory);
        Write(new PublisherRecord
        {
            TrustContext = trustContext,
            PluginId = pluginId,
            Committed = head,
            Pending = null
        });

        TryDelete(PendingIndexPathFor(trustContext));
        logger.Information("Publishing head for {PluginId} is now generation {Generation}",
            pluginId, head.Generation);
    }

    /// <summary>
    /// Drops a pending publish that has been established never to have landed. The committed head
    /// is left exactly as it was.
    /// </summary>
    public void DiscardPending(string trustContext, string pluginId, PublisherHead? committed)
    {
        System.IO.Directory.CreateDirectory(Directory);
        Write(new PublisherRecord
        {
            TrustContext = trustContext,
            PluginId = pluginId,
            Committed = committed,
            Pending = null
        });

        TryDelete(PendingIndexPathFor(trustContext));
    }

    private void Write(PublisherRecord record)
    {
        var path = PathFor(record.TrustContext);

        // Keep the previous copy. Losing this record does not lose the key, but it does force the
        // author through a deliberate recovery to publish again, and a retained copy makes that a
        // last resort rather than the first thing that happens after a bad write.
        if (File.Exists(path))
        {
            try { File.Copy(path, path + ".previous", overwrite: true); }
            catch (Exception ex) { logger.Warning(ex, "Couldn't keep a copy of the previous publishing record"); }
        }

        WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Json)));
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { logger.Warning(ex, "Couldn't clean up {Path}", path); }
    }
}
