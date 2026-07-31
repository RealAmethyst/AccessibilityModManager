using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.Security;

/// <summary>
/// The registry replay guard, in the states where it used to give up quietly.
///
/// <para>A validly signed OLD registry is the cheapest attack available against a signed catalog:
/// the signature is genuine, so nothing else in the chain objects, and a registry published before
/// <c>indexTrust</c> existed names no signing key at all — which sends the manager down the unsigned
/// path and discards everything signing was introduced to provide.</para>
///
/// <para>Two things stop it, and they cover different ranges. The shipped FLOOR refuses anything
/// older than the version this build was made against, and cannot be absent or rolled back because
/// it is a constant. The high-water MARKER refuses anything older than the newest this machine has
/// accepted, which is the only thing that covers registries published after the build. The marker
/// used to fail open in three ways, so the range it covers could be lost without anything saying
/// so.</para>
/// </summary>
public sealed class RegistryReplayHardeningTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly RSA _rsa = RSA.Create(2048);

    public RegistryReplayHardeningTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ammtest_replay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _rsa.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private static readonly Uri RegistryUrl = new("https://example.invalid/plugin-registry.json");
    private string MarkerPath => Path.Combine(_tempRoot, "registry-highwater.txt");

    // ---- the shipped floor ---------------------------------------------------------------

    [Fact]
    public async Task A_fresh_install_refuses_a_registry_older_than_the_one_this_build_was_made_against()
    {
        // No marker at all — the state of every new install, and of anyone who clears app data or
        // moves to a new machine. The marker refuses nothing here, so without the floor a replayed
        // pre-signing registry would be accepted and the manager would read an unsigned catalog.
        Assert.False(File.Exists(MarkerPath));

        var client = Client(version: "2");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.FetchRegistryAsync(RegistryUrl, default));

        Assert.Contains("older than version 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_floor_is_a_floor_and_not_an_equality()
    {
        // A newer registry must always be accepted. If this ever inverted, publishing a new registry
        // would strand every manager built before it — the one failure that cannot be fixed by
        // publishing again.
        var fetched = await Client(version: "4").FetchRegistryAsync(RegistryUrl, default);

        Assert.Equal("4", fetched.Value.RegistryVersion);
    }

    /// <summary>
    /// The <c>registryVersion</c> actually published at accessibilitymods.com, confirmed by fetching
    /// the live document on 2026-07-30.
    ///
    /// <para>Pinned INDEPENDENTLY of the floor, and that independence is the entire point. Raising
    /// <see cref="RegistryTrustKey.MinimumRegistryVersion"/> past what is really published is the
    /// one change here capable of refusing every user's catalog at once — and a test that takes both
    /// sides of the comparison from the same constant cannot see it happen. Updating this literal
    /// means someone checked the live registry.</para>
    /// </summary>
    private const string LiveRegistryVersion = "3";

    [Fact]
    public void The_shipped_floor_is_not_ahead_of_what_is_actually_published()
    {
        var floor = RegistryTrustKey.MinimumRegistryVersion;

        Assert.True(
            VersionComparer.Instance.Compare(floor, LiveRegistryVersion) <= 0,
            $"The shipped floor is {floor} but the published registry is {LiveRegistryVersion}, so every " +
            "manager would refuse the real catalog. Publish the newer registry, confirm it is live, then " +
            $"update {nameof(LiveRegistryVersion)} in this test.");
    }

    [Fact]
    public async Task A_registry_at_the_published_version_is_accepted()
    {
        var fetched = await Client(version: LiveRegistryVersion).FetchRegistryAsync(RegistryUrl, default);

        Assert.Equal(LiveRegistryVersion, fetched.Value.RegistryVersion);
    }

    // ---- the marker, in the states where it used to fail open -----------------------------

    [Fact]
    public async Task A_marker_that_exists_and_cannot_be_read_refuses_rather_than_reading_as_absent()
    {
        // The fail-open this replaces: an unreadable marker was logged and treated as absent, so
        // this machine's ratchet silently reset to nothing and every registry published since the
        // build became replayable. Absent and unreadable are now different answers.
        await Client(version: "5").FetchRegistryAsync(RegistryUrl, default);
        Assert.True(File.Exists(MarkerPath));

        // Hold it open with no sharing — the shape a lock or a permissions fault takes.
        using var hold = new FileStream(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Client(version: "4").FetchRegistryAsync(RegistryUrl, default));

        Assert.Contains("couldn't be read", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_marker_still_proceeds_because_that_is_every_first_run()
    {
        // The other half, and the reason the distinction has to be exact rather than "any failure
        // refuses": if absence refused too, no installation could ever accept its first registry.
        Assert.False(File.Exists(MarkerPath));

        var fetched = await Client(version: "9").FetchRegistryAsync(RegistryUrl, default);

        Assert.Equal("9", fetched.Value.RegistryVersion);
        Assert.True(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task A_registry_that_cannot_be_recorded_is_not_accepted()
    {
        // Accepting one without recording it loses the ratchet at the moment it should have
        // advanced, and no later run can tell that it happened. Previously a logged warning.
        //
        // The write is isolated from the read deliberately: a read-only marker still READS fine, so
        // the version comparison happens as normal and only the write back fails. (A directory in
        // the marker's place fails the read first, which refuses for the other reason entirely —
        // correct behaviour, but it would have proved the wrong thing.)
        await Client(version: "5").FetchRegistryAsync(RegistryUrl, default);
        File.SetAttributes(MarkerPath, FileAttributes.ReadOnly);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Client(version: "6").FetchRegistryAsync(RegistryUrl, default));

            Assert.Contains("couldn't be saved", ex.Message, StringComparison.Ordinal);

            // And it really did not advance: the recorded position is still the one that was written
            // when a write succeeded.
            File.SetAttributes(MarkerPath, FileAttributes.Normal);
            Assert.Equal("5", File.ReadAllLines(MarkerPath)[0].Trim());
        }
        finally
        {
            if (File.Exists(MarkerPath)) File.SetAttributes(MarkerPath, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("   \n   \n")]
    public async Task A_marker_that_reads_fine_but_says_nothing_refuses_rather_than_starting_over(string content)
    {
        // The third route to "absent", and the one the explicit flag exists for. File.ReadAllLines
        // succeeds on an empty or blank marker, which left the recorded version null — so the
        // comparison was skipped entirely and whatever arrived was accepted and written down. A
        // machine that had accepted 9 would take a replayed 4. Truncation is exactly what a botched
        // write, a full disk or a crash leaves behind.
        await Client(version: "9").FetchRegistryAsync(RegistryUrl, default);
        File.WriteAllText(MarkerPath, content);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Client(version: "4").FetchRegistryAsync(RegistryUrl, default));

        Assert.Contains("damaged", ex.Message, StringComparison.Ordinal);

        // And the damaged marker was not overwritten with the replayed position, which would have
        // made the rollback permanent.
        Assert.Equal(content, File.ReadAllText(MarkerPath));
    }

    [Fact]
    public async Task A_marker_truncated_to_its_version_line_refuses_at_that_version()
    {
        // The half-truncated case, between "says nothing" and "intact". The numeric ratchet survives
        // — the version is still there — but the CONTENT ratchet is gone, and that is the half
        // guarding the failure the hash exists for: two differently signed documents under one
        // version number. Tolerating it would accept the replayed variant and write its hash in,
        // making the loss permanent.
        //
        // Checked in the history rather than assumed: every build that has written this file wrote
        // both lines (since 580692a), so a one-line marker can only be truncation. There is no
        // legacy format to be tolerant of.
        await Client(version: "9").FetchRegistryAsync(RegistryUrl, default);
        File.WriteAllText(MarkerPath, "9");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Client(version: "9", json: AlternativeRegistryAtVersion9).FetchRegistryAsync(RegistryUrl, default));

        Assert.Contains("incomplete", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_marker_does_not_block_a_genuinely_newer_registry()
    {
        // The other side of it: refusing at the recorded version must not brick the machine. A
        // strictly newer version is unambiguous whatever the hash says, and it rebuilds the marker
        // with both lines.
        await Client(version: "9").FetchRegistryAsync(RegistryUrl, default);
        File.WriteAllText(MarkerPath, "9");

        var fetched = await Client(version: "10").FetchRegistryAsync(RegistryUrl, default);

        Assert.Equal("10", fetched.Value.RegistryVersion);
        Assert.Equal(2, File.ReadAllLines(MarkerPath).Length);
    }

    /// <summary>A second, differently-signed document carrying the same version number.</summary>
    private const string AlternativeRegistryAtVersion9 = """
    {
      "registryVersion": "9",
      "updatedAt": "2026-07-30T00:00:00Z",
      "plugins": [
        { "id": "plug-b", "name": "B", "author": "B", "description": "d",
          "repoIndexUrl": "https://example.invalid/b.json" }
      ]
    }
    """;

    [Fact]
    public async Task A_registry_already_recorded_exactly_needs_no_write_at_all()
    {
        // Refusing here would buy nothing: the marker already names this exact version AND content,
        // so the write it insists on would reproduce the file byte for byte. Meanwhile a read-only
        // marker or a restrictive ACL is not self-correcting the way a full disk is, so demanding
        // the write turns it into a catalog that can never refresh — while the server is returning
        // precisely what this machine already trusts.
        await Client(version: "9").FetchRegistryAsync(RegistryUrl, default);
        File.SetAttributes(MarkerPath, FileAttributes.ReadOnly);

        try
        {
            var fetched = await Client(version: "9").FetchRegistryAsync(RegistryUrl, default);

            Assert.Equal("9", fetched.Value.RegistryVersion);
        }
        finally
        {
            if (File.Exists(MarkerPath)) File.SetAttributes(MarkerPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task A_marker_transaction_that_cannot_be_serialised_refuses_on_the_network_path_too()
    {
        // The lock used to be required only on the cache path, so a network fetch proceeded
        // unserialised — and two app copies could then read the same marker and write their
        // acceptances in either order, regressing it to the older of two accepted registries.
        //
        // Deliberately slow: acquisition retries for ten seconds before giving up, and that wait is
        // the behaviour under test. Worth one slow case, since nothing else pins this change.
        var lockPath = Path.Combine(_tempRoot, "registry-highwater.lock");
        using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Client(version: "9").FetchRegistryAsync(RegistryUrl, default));
    }

    // ---- the shared acceptance rules the manager did not used to run ----------------------

    [Fact]
    public async Task The_manager_refuses_a_registry_listing_one_plugin_twice()
    {
        // This check lived only in the AuthorTool's pre-publish mirror, which production never
        // called — so the side whose refusal actually protects a user was the weaker of the two.
        // It matters beyond tidiness: two entries for one id are two answers to "which key signs
        // this catalog", and the reader would have to pick one.
        var json = $$"""
        {
          "registryVersion": "3",
          "updatedAt": "2026-07-30T00:00:00Z",
          "plugins": [
            { "id": "plug-a", "name": "A", "author": "A", "description": "d",
              "repoIndexUrl": "https://example.invalid/a.json" },
            { "id": "plug-a", "name": "A again", "author": "A", "description": "d",
              "repoIndexUrl": "https://example.invalid/b.json" }
          ]
        }
        """;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Client(json: json).FetchRegistryAsync(RegistryUrl, default));

        Assert.Contains("listed twice", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sharing_the_rules_did_not_downgrade_the_https_refusal()
    {
        // UrlValidator throws SecurityException. Collecting every rule into a report of strings
        // would have flattened that into a generic failure — a silent loss of signal that no test
        // outside this one would have noticed.
        var json = $$"""
        {
          "registryVersion": "3",
          "updatedAt": "2026-07-30T00:00:00Z",
          "plugins": [
            { "id": "plug-a", "name": "A", "author": "A", "description": "d",
              "repoIndexUrl": "http://example.invalid/a.json" }
          ]
        }
        """;

        await Assert.ThrowsAsync<System.Security.SecurityException>(
            () => Client(json: json).FetchRegistryAsync(RegistryUrl, default));
    }

    // ---- harness --------------------------------------------------------------------------

    private PluginRegistryClient Client(string version = "9", string? json = null)
    {
        json ??= $$"""
        {
          "registryVersion": "{{version}}",
          "updatedAt": "2026-07-30T00:00:00Z",
          "plugins": [
            { "id": "plug-a", "name": "A", "author": "A", "description": "d",
              "repoIndexUrl": "https://example.invalid/a.json" }
          ]
        }
        """;

        var signature = Convert.ToBase64String(
            _rsa.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        var handler = new RouteHandler(url => url.Contains(".sig") ? signature : json);
        var verifier = new RegistrySignatureVerifier(_rsa.ExportSubjectPublicKeyInfoPem(), TestLogger.Create());

        // No floor override exists, deliberately. These cases use versions above the shipped floor
        // so they exercise the marker rather than the floor; the floor has its own cases, which
        // serve versions below it.
        return new PluginRegistryClient(
            new HttpClient(handler), TestLogger.Create(), verifier, _tempRoot);
    }
}
