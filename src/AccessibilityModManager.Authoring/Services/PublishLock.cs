using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>
/// Who holds a plugin's publish lock, and since when. Written by the machine that took the lock and
/// read by whoever finds it in the way.
/// </summary>
public sealed record PublishLockBody
{
    [JsonPropertyName("v")]
    public int V { get; init; } = 1;

    [JsonPropertyName("pluginId")]
    public required string PluginId { get; init; }

    [JsonPropertyName("machine")]
    public required string Machine { get; init; }

    [JsonPropertyName("user")]
    public required string User { get; init; }

    /// <summary>
    /// When the lock was taken, ISO 8601 in UTC. Kept as text: it is shown to a person, and a lock
    /// whose timestamp cannot be parsed must still be reportable rather than becoming an exception
    /// in the middle of explaining why a publish stopped.
    /// </summary>
    [JsonPropertyName("takenAtUtc")]
    public required string TakenAtUtc { get; init; }

    /// <summary>
    /// Random per-acquisition value. Releasing compares it, so a lock that was broken and retaken by
    /// someone else is not deleted by its previous holder.
    /// </summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    /// <summary>A human-readable "held by X on Y since Z", for the message that stops a publish.</summary>
    public string Describe()
    {
        var when = DateTimeOffset.TryParse(TakenAtUtc, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : TakenAtUtc;

        return $"{User} on {Machine}, since {when}";
    }
}

/// <summary>
/// Raised when a plugin's publish lock is already held. Carries the holder so the caller can say who
/// and since when rather than "it failed".
/// </summary>
public sealed class PublishLockHeldException(string message, PublishLockBody? holder)
    : InvalidOperationException(message)
{
    /// <summary>The lock's contents, or null when it could not be read or understood.</summary>
    public PublishLockBody? Holder { get; } = holder;
}

/// <summary>What releasing a lock turned out to do.</summary>
public enum PublishLockRelease
{
    /// <summary>The lock was ours and is gone.</summary>
    Released,

    /// <summary>It was already gone — someone broke it, or a cleanup removed it.</summary>
    AlreadyGone,

    /// <summary>
    /// Something else is in it now, so it was left alone. Deleting a lock we cannot prove is ours
    /// would take it away from whoever is currently publishing under it.
    /// </summary>
    NotOurs
}

/// <summary>
/// The parts of the publish lock that can be decided without touching the network: where the lock
/// file goes, whether that is a safe place for it, and what it says.
///
/// Separated from <see cref="ServerUploadService"/> deliberately — the SFTP side needs a live server
/// to exercise, and these rules are the ones worth having tests for.
///
/// <para><b>What the lock is and is not.</b> It is concurrency control between the author's own
/// machines, and it is explicitly NOT part of the protection against a hostile server, which can
/// unlink it, fabricate one, or present a different directory to each caller.
///
/// Within its own scope it is load-bearing rather than convenient: if two publishers ever both get
/// past it, each can rename, read back its own result and commit — not merely race to overwrite —
/// leaving two differently-signed generations under one key. Every publisher of a catalog must
/// therefore resolve <see cref="ServerUploadConfig.RemoteLockRoot"/> to the same directory. Three
/// limits, stated so nothing is built on top of them:</para>
/// <list type="bullet">
/// <item>A dropped connection leaves the file behind, so an interrupted publish leaves a lock nobody
/// holds. Breaking it is a deliberate act, not something offered whenever one is in the way.</item>
/// <item>"Read the token, then delete" is not atomic: a lock broken and retaken between those two
/// steps could be deleted by its previous holder.</item>
/// <item>If two writers ever do get past it, each can rename and then read back its own result.</item>
/// </list>
/// <para>Recovery from an interrupted publish comes from the local journal and the verified live
/// proof — never from reasoning about the lock.</para>
/// </summary>
public static class PublishLock
{
    /// <summary>Directory name used under the SSH home when no lock root is configured.</summary>
    public const string DefaultDirectoryName = ".amm-publish-locks";

    /// <summary>
    /// A lock file is a few hundred bytes. The cap is here because the file is read from a server
    /// that may be lying, and an unbounded read of a "lock" is an easy way to be handed a gigabyte.
    /// </summary>
    public const int MaxBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>
    /// Where the lock directory is, given what the author configured and where the SSH session
    /// landed. Fails closed rather than guessing.
    ///
    /// <para>The one rule that matters: the lock must not live anywhere the web server publishes.
    /// Named after the machine that took it and timestamped, inside a directory served with
    /// <c>try_files $uri</c>, it would be a publicly readable record of who publishes and when.</para>
    /// </summary>
    /// <param name="configuredRoot">
    /// <see cref="ServerUploadConfig.RemoteLockRoot"/>. Empty means "derive it from the SSH home".
    /// </param>
    /// <param name="sshHome">
    /// The session's working directory after connecting, which for a normal account is its home.
    /// Only consulted when nothing is configured.
    /// </param>
    /// <param name="servedRoots">
    /// Every directory the web server hands out — the catalog root and the releases root, and the
    /// PARENT of each, because both are subdirectories of a vhost root that is itself served with
    /// <c>try_files $uri</c>. Without the parents, <c>/var/www/site/locks</c> beside
    /// <c>/var/www/site/registry</c> passes as "outside the served folders" while being downloadable.
    /// <para>
    /// This is a guard against honest misconfiguration and nothing more. It compares text: a symlink
    /// in the lock path, a bind mount, or a server presenting different directories to different
    /// callers all defeat it, and none of those are things a client can disprove over SFTP.
    /// </para>
    /// </param>
    public static string ResolveRoot(string? configuredRoot, string? sshHome, params string?[] servedRoots)
    {
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? DeriveFromHome(sshHome)
            : Normalise(configuredRoot);

        if (!root.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"The publish lock folder '{root}' is not an absolute path on the server. Set an " +
                "absolute path in Server upload settings.");
        }

        // A '..' anywhere defeats the containment check below, and it does so in the dangerous
        // direction: '/home/ola/../var/www/registry/locks' reads as outside the served folders and
        // resolves to inside one. Resolving it here instead would mean guessing at symlinks we
        // cannot see, so the path is refused rather than rewritten.
        if (HasDotDotSegment(root))
        {
            throw new InvalidOperationException(
                $"The publish lock folder '{root}' contains '..'. Give the real path instead — a " +
                "path that walks upwards can't be checked against the folders the web server hands " +
                "out.");
        }

        foreach (var servedRoot in servedRoots.SelectMany(WithParent))
        {
            if (!IsInside(servedRoot, root)) continue;

            throw new InvalidOperationException(
                $"The publish lock folder '{root}' is inside '{servedRoot}', which the web server " +
                "hands out. A lock names the machine that took it and when, so it must not sit " +
                "anywhere downloadable. Point it somewhere outside the served folders.");
        }

        return root;
    }

    private static string DeriveFromHome(string? sshHome)
    {
        // An empty or relative working directory means the server did not tell us where we are, and
        // a guessed home is how a lock ends up somewhere nobody looks — or somewhere served.
        if (string.IsNullOrWhiteSpace(sshHome) || !sshHome.TrimStart().StartsWith('/'))
        {
            throw new InvalidOperationException(
                "The server didn't report an absolute home directory, so there's nowhere obvious to " +
                "keep the publish lock. Set the publish lock folder explicitly in Server upload " +
                "settings — an absolute path outside the web folders.");
        }

        return Normalise($"{Normalise(sshHome)}/{DefaultDirectoryName}");
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="parent"/> or sits under it, compared
    /// on segment boundaries so <c>/var/www/registry-locks</c> is not judged to be inside
    /// <c>/var/www/registry</c>.
    /// </summary>
    public static bool IsInside(string parent, string candidate)
    {
        var p = Normalise(parent);
        var c = Normalise(candidate);

        if (string.Equals(p, c, StringComparison.Ordinal)) return true;

        // "/" is its own case: it already ends in the separator, so the usual concatenation would
        // look for a path beginning "//" and find nothing inside the root at all.
        var prefix = p == "/" ? "/" : p + "/";
        return c.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// A served directory and the vhost root it almost certainly sits under.
    ///
    /// The catalog root is <c>{site}/registry</c> and the releases root is <c>{site}/releases</c>;
    /// nginx serves the whole of <c>{site}</c>. Judging only the leaf would call a sibling folder
    /// safe while every file in it is downloadable. Stops at the filesystem root, which is nobody's
    /// vhost.
    /// </summary>
    private static IEnumerable<string> WithParent(string? served)
    {
        if (string.IsNullOrWhiteSpace(served)) yield break;

        var root = Normalise(served);
        yield return root;

        var cut = root.LastIndexOf('/');
        if (cut > 0) yield return root[..cut];
    }

    private static bool HasDotDotSegment(string normalisedPath) =>
        normalisedPath.Split('/').Any(segment => segment == "..");

    /// <summary>
    /// Collapses repeated separators and drops a trailing one, so two spellings of the same
    /// directory compare equal. Deliberately does NOT resolve <c>..</c> — <see cref="ResolveRoot"/>
    /// refuses a path containing one rather than quietly rewriting it into something that looks
    /// safe.
    /// </summary>
    private static string Normalise(string path)
    {
        var trimmed = path.Trim().Replace('\\', '/');
        while (trimmed.Contains("//", StringComparison.Ordinal))
            trimmed = trimmed.Replace("//", "/", StringComparison.Ordinal);

        if (trimmed.Length > 1) trimmed = trimmed.TrimEnd('/');
        return trimmed;
    }

    /// <summary>
    /// The lock file name for a plugin.
    ///
    /// An allowlist, not a denylist. The key store learned this the expensive way: a plugin id used
    /// to go straight into a path, and the fix was to stop asking "does this contain anything bad"
    /// and start asking "is this made only of things that are fine".
    /// </summary>
    public static string FileNameFor(string pluginId)
    {
        if (string.IsNullOrEmpty(pluginId) || pluginId.Length > 64)
        {
            throw new InvalidOperationException(
                $"'{pluginId}' can't be used as a publish lock name: a plugin id must be 1 to 64 " +
                "characters.");
        }

        if (!char.IsAsciiLetterOrDigit(pluginId[0]))
        {
            throw new InvalidOperationException(
                $"'{pluginId}' can't be used as a publish lock name: a plugin id must start with a " +
                "letter or a digit.");
        }

        foreach (var ch in pluginId)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-') continue;
            throw new InvalidOperationException(
                $"'{pluginId}' can't be used as a publish lock name: '{ch}' is not allowed. Plugin " +
                "ids may use letters, digits, dot, underscore and hyphen.");
        }

        return $"{pluginId}.lock";
    }

    /// <summary>A fresh lock body for this machine, with a random token.</summary>
    public static PublishLockBody NewBody(string pluginId) => new()
    {
        PluginId = pluginId,
        Machine = Environment.MachineName,
        User = Environment.UserName,
        TakenAtUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        Token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
    };

    public static byte[] Serialize(PublishLockBody body) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Json));

    /// <summary>Longest a machine name, user name or timestamp may be before it is not one.</summary>
    public const int MaxFieldLength = 64;

    /// <summary>
    /// Reads a lock body, or null when the bytes are not one. Null is a usable answer here — a lock
    /// we cannot understand is still a lock we must not delete — so this reports rather than throws.
    ///
    /// <para>Everything in here came off a server that may be lying, and it ends up in an error
    /// message that is READ ALOUD. Unbounded fields are therefore not a cosmetic problem: a
    /// fabricated lock with a 60 KiB user name turns "someone else is publishing" into tens of
    /// thousands of spoken characters with no action in them. So the shape is checked, not just the
    /// version.</para>
    /// </summary>
    /// <param name="expectedPluginId">
    /// Which plugin's lock the caller went looking for. A body naming a different one is not this
    /// lock — it is a file in this lock's place — and reporting its contents would describe a holder
    /// of something else.
    /// </param>
    public static PublishLockBody? TryParse(byte[]? bytes, string expectedPluginId)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > MaxBodyBytes) return null;

        PublishLockBody? body;
        try
        {
            body = JsonSerializer.Deserialize<PublishLockBody>(bytes, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        // `required` only guarantees the member was PRESENT — an explicit null satisfies it and
        // arrives here as null.
        if (body is not { V: 1 }) return null;
        if (body.Machine is null || body.User is null || body.TakenAtUtc is null) return null;
        if (!string.Equals(body.PluginId, expectedPluginId, StringComparison.Ordinal)) return null;
        if (!IsOurTokenShape(body.Token)) return null;

        return IsPlainAndShort(body.Machine) && IsPlainAndShort(body.User) && IsPlainAndShort(body.TakenAtUtc)
            ? body
            : null;
    }

    /// <summary>
    /// The token this tool writes: 64 lowercase hex characters. Anything else is not a token we can
    /// compare, and a lock we cannot compare is one we must leave alone — so refusing here fails
    /// closed, including against a future format this build does not know.
    /// </summary>
    private static bool IsOurTokenShape(string? token) =>
        token is { Length: 64 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Short enough to say, and free of control characters — which a screen reader renders as
    /// nothing at all, so a name made of them would be announced as blank, and embedded newlines
    /// would let a fabricated lock forge extra lines of the message around it.
    /// </summary>
    private static bool IsPlainAndShort(string value) =>
        value.Length is > 0 and <= MaxFieldLength && !value.Any(char.IsControl);

    /// <summary>
    /// Whether a lock found on the server is the one this machine took. Ordinal and length-checked:
    /// the token is the only thing that distinguishes our lock from one that replaced it.
    /// </summary>
    public static bool IsOurs(PublishLockBody? found, PublishLockBody ours) =>
        found is not null &&
        string.Equals(found.Token, ours.Token, StringComparison.Ordinal) &&
        string.Equals(found.PluginId, ours.PluginId, StringComparison.Ordinal);
}
