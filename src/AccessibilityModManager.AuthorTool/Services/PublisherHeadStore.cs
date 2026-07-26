using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// The registry this machine has acted on: which version, and exactly which document.
///
/// The content hash is not optional. Raising the version when the content changes is what the
/// publishing side already enforces, so two different validly-signed registries at one version is
/// a contradiction — and a restored machine that knew only the number would accept whichever of
/// them a replaying server offered first.
/// </summary>
public sealed record RegistryHighWater
{
    [JsonPropertyName("version")]
    public required long Version { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

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

        PublisherRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<PublisherRecord>(File.ReadAllText(path), Json);
        }
        // Only a definite "it is not there" counts as absence. File.Exists answers false for a
        // permission failure or an unreadable volume too, and collapsing those into "this key has
        // never published" hands back the most permissive state there is at exactly the moment
        // something is already wrong.
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
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

    /// <summary>
    /// Refuses a registry older than one this machine has already acted on, and records it.
    ///
    /// The registry is signed, so a replayed old one is cryptographically perfect — and it is the
    /// document that says which index URL and which key a plugin publishes under. Re-pointing a
    /// plugin, or rotating its key, is how a compromised source gets disowned; replaying the
    /// registry from before that change puts the tool back into the retired context, where its
    /// still-retained head for that context matches and publishing proceeds as if nothing had
    /// happened. The head rules cannot catch it, because each trust context keeps its own head and
    /// both of them are genuinely this machine's.
    ///
    /// Kept outside any one trust context on purpose: it is what orders the contexts themselves.
    /// </summary>
    public void RequireRegistryNotOlder(string registryJson)
    {
        var version = ReadRegistryVersion(registryJson);
        var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(registryJson)));
        var path = Path.Combine(Directory, "registry-highwater.json");

        var seen = TryLoadHighWater(path);
        if (seen is not null)
        {
            if (version < seen.Version)
            {
                throw new InvalidOperationException(
                    $"The registry on the server says version {version}, but this machine has already " +
                    $"seen version {seen.Version}. An older registry can name an index address or a " +
                    "signing key that has since been retired, so publishing against it is refused.");
            }

            if (version == seen.Version && !string.Equals(hash, seen.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The registry on the server is different from the one this machine saw at version " +
                    $"{version}, and the version was not raised. Publishing against it is refused.");
            }
        }

        if (seen is null || version > seen.Version)
        {
            System.IO.Directory.CreateDirectory(Directory);
            DurableFile.Write(path, System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new RegistryHighWater { Version = version, Sha256 = hash }, Json)));
        }
    }

    /// <summary>
    /// The registry's version, as a number.
    ///
    /// It is a free-form string in the schema and every published one has been an integer, which
    /// the publishing side already relies on when it refuses to publish content without raising it.
    /// Anything that is not a number cannot be ordered, and something that cannot be ordered cannot
    /// be a high-water mark — so it is refused rather than silently treated as "fine".
    /// </summary>
    private static long ReadRegistryVersion(string registryJson)
    {
        using var document = JsonDocument.Parse(registryJson);
        if (!document.RootElement.TryGetProperty("registryVersion", out var element) ||
            element.ValueKind != JsonValueKind.String ||
            !long.TryParse(element.GetString(), out var version) ||
            version < 1)
        {
            throw new InvalidOperationException(
                "The registry has no whole-number registryVersion, so there is no way to tell whether " +
                "it is the current one. Publishing against it is refused.");
        }

        return version;
    }

    private RegistryHighWater? TryLoadHighWater(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<RegistryHighWater>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"This machine's record of the registry version can't be read ({path}). Publishing is " +
                "blocked until it is: without it, an older registry cannot be told from the current one.", ex);
        }
    }



    /// <summary>
    /// Every publishing record belonging to one plugin, for a key backup to carry.
    ///
    /// A key without its history cannot publish anywhere else — that is the whole point of the
    /// single-writer rule — so the two travel together or the backup is not a backup.
    /// </summary>
    public IReadOnlyList<PublisherRecord> RecordsFor(string pluginId)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        var records = new List<PublisherRecord>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != 64) continue; // the high-water file, or something else entirely

            var record = TryLoad(name);
            if (record is not null && string.Equals(record.PluginId, pluginId, StringComparison.Ordinal))
                records.Add(record);
        }

        return records;
    }

    /// <summary>The registry this machine has acted on, or null when it has acted on none.</summary>
    public RegistryHighWater? CurrentRegistryHighWater() =>
        TryLoadHighWater(Path.Combine(Directory, "registry-highwater.json"));

    /// <summary>
    /// Takes the publishing state out of a key backup.
    ///
    /// Never downgrades: an existing record is kept when it is at or beyond the one being restored,
    /// because a backup is by definition a photograph of an earlier moment, and a machine that has
    /// published since knows more than the backup does. The registry high-water moves the same way,
    /// forwards only.
    /// </summary>
    public void RestoreFromBackup(
        string pluginId, IReadOnlyList<PublisherRecord> records, RegistryHighWater? registry)
    {
        System.IO.Directory.CreateDirectory(Directory);

        foreach (var record in records)
        {
            if (!string.Equals(record.PluginId, pluginId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"That backup carries publishing state for '{record.PluginId}', which is not the " +
                    $"plugin it says it is for ('{pluginId}').");
            }
        }

        // The freshness guard goes down first, and the heads after it. The other order leaves a
        // crash window with a usable key and a retired head restored, and nothing yet in place to
        // stop a replayed registry putting the two back into service together.
        var path = Path.Combine(Directory, "registry-highwater.json");
        if (registry is not null && registry.Version > (TryLoadHighWater(path)?.Version ?? 0))
            DurableFile.Write(path, JsonSerializer.Serialize(registry, Json));

        var restored = new List<string>();
        foreach (var record in records)
        {
            if (!MovesForward(TryLoad(record.TrustContext), record))
            {
                logger.Information("Kept this machine's newer publishing state for {PluginId}", pluginId);
                continue;
            }

            Write(record);
            restored.Add(record.TrustContext);
            logger.Information(
                "Restored publishing state for {PluginId} at generation {Generation}",
                record.PluginId, record.Committed?.Generation ?? 0);
        }

        if (restored.Count > 0) MarkRestored(restored);
    }

    /// <summary>
    /// Whether a restored record is worth taking over what is already here.
    ///
    /// A backup is a photograph of an earlier moment, so a machine that has published since knows
    /// more than it does. The comparison has to cover the empty cases too: an incoming record with
    /// no committed head, or none of the pending journal, must never erase one that exists — which
    /// the first version allowed by comparing only when both sides were populated.
    /// </summary>
    private static bool MovesForward(PublisherRecord? existing, PublisherRecord incoming)
    {
        if (existing is null) return true;
        if (existing.Pending is not null && incoming.Pending is null) return false;
        if (existing.Committed is null) return true;
        if (incoming.Committed is null) return false;

        return incoming.Committed.Generation > existing.Committed.Generation;
    }

    /// <summary>
    /// Trust contexts whose state came out of a backup and has not been confirmed against reality
    /// since.
    ///
    /// This is the honest answer to what a backup cannot do. Restoring one proves the state is
    /// AUTHENTIC; nothing in a file can prove it is CURRENT. If the author published twice after
    /// taking that backup, a restored machine believes the earlier of the two — and a server
    /// replaying that same earlier publish matches it exactly, so the ordinary head check passes
    /// and the key signs a second, different version of a generation that already exists.
    ///
    /// Nothing local can detect that. So the machine remembers where its state came from, and the
    /// first publish afterwards is a decision the author makes with the generation in front of
    /// them, rather than something that happens quietly.
    /// </summary>
    public bool IsRestoredAndUnconfirmed(string trustContext) =>
        ReadRestoredMarks().Contains(trustContext);

    private void MarkRestored(IEnumerable<string> trustContexts)
    {
        var marks = ReadRestoredMarks();
        foreach (var context in trustContexts) marks.Add(context);
        DurableFile.Write(RestoredMarkPath, JsonSerializer.Serialize(marks.Order(StringComparer.Ordinal), Json));
    }

    private void ClearRestoredMark(string trustContext)
    {
        var marks = ReadRestoredMarks();
        if (!marks.Remove(trustContext)) return;
        DurableFile.Write(RestoredMarkPath, JsonSerializer.Serialize(marks.Order(StringComparer.Ordinal), Json));
    }

    private string RestoredMarkPath => Path.Combine(Directory, "restored.json");

    private HashSet<string> ReadRestoredMarks()
    {
        try
        {
            return new HashSet<string>(
                JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RestoredMarkPath), Json) ?? [],
                StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "This machine's record of restored publishing state can't be read. Publishing is " +
                "blocked until it is: without it there is no way to tell state this machine " +
                "confirmed from state that came out of a backup.", ex);
        }
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
        DurableFile.Write(PendingIndexPathFor(trustContext), indexBytes);

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

        // A confirmed publish is what turns restored state into state this machine has seen work.
        ClearRestoredMark(trustContext);

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

        DurableFile.Write(path, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Json)));
    }


    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { logger.Warning(ex, "Couldn't clean up {Path}", path); }
    }
}
