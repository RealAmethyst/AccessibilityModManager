using System.Text;
using AccessibilityModManager.AuthorTool.Services;
using Xunit;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// The read-only server check.
///
/// <para>The property worth testing is not that it reports nicely — it is that it cannot make things
/// worse. A diagnostic that strands the publish lock would block publishing entirely, which is a
/// good deal more damage than the uncertainty it was run to remove.</para>
/// </summary>
public sealed class ServerSelfTestTests
{
    private const string PluginId = "amethyst";

    private sealed class FakeTransport : IPublishTransport
    {
        public readonly List<string> Events = [];
        public byte[]? Live;
        public Exception? FailAcquire;
        public Exception? FailRead;
        public Exception? FailRelease;
        public PublishLockRelease Release = PublishLockRelease.Released;

        public Task<ServerUploadService.PublishLockHandle> AcquireLockAsync(string pluginId, CancellationToken ct)
        {
            Events.Add("lock");
            if (FailAcquire is not null) throw FailAcquire;
            return Task.FromResult(new ServerUploadService.PublishLockHandle(
                "/locks/" + pluginId, PublishLock.NewBody(pluginId)));
        }

        public Task<PublishLockRelease> ReleaseLockAsync(
            ServerUploadService.PublishLockHandle handle, CancellationToken ct)
        {
            Events.Add("unlock");
            if (FailRelease is not null) throw FailRelease;
            return Task.FromResult(Release);
        }

        public Task<ServerUploadService.RemoteIndex> ReadIndexAsync(string pluginId, CancellationToken ct)
        {
            Events.Add("read");
            if (FailRead is not null) throw FailRead;
            return Task.FromResult(new ServerUploadService.RemoteIndex(Live is not null, Live));
        }

        public Task PublishIndexAsync(
            string pluginId, byte[] indexJson, Func<Task> beforeSwitchAsync, CancellationToken ct)
        {
            Events.Add("upload");
            return Task.CompletedTask;
        }
    }

    private static Task<IReadOnlyList<ServerCheckStep>> RunAsync(FakeTransport transport) =>
        ServerSelfTest.RunAsync(transport, PluginId, CancellationToken.None);

    [Fact]
    public async Task It_takes_the_lock_reads_and_gives_the_lock_back()
    {
        var transport = new FakeTransport { Live = Encoding.UTF8.GetBytes("{}") };

        var steps = await RunAsync(transport);

        Assert.Equal(["lock", "read", "unlock"], transport.Events);
        Assert.All(steps, s => Assert.True(s.Ok));
    }

    [Fact]
    public async Task It_never_publishes_anything()
    {
        var transport = new FakeTransport { Live = Encoding.UTF8.GetBytes("{}") };

        await RunAsync(transport);

        // The whole point is that this can be run before the one-way door without consequences.
        Assert.DoesNotContain("upload", transport.Events);
    }

    [Fact]
    public async Task A_failed_read_still_gives_the_lock_back()
    {
        // The case that would otherwise turn a diagnostic into an outage: the check fails, leaves
        // the lock behind, and now nothing can publish until somebody clears it by hand.
        var transport = new FakeTransport { FailRead = new IOException("no route to host") };

        var steps = await RunAsync(transport);

        Assert.Equal(["lock", "read", "unlock"], transport.Events);
        Assert.Contains(steps, s => !s.Ok && s.Name.Contains("Read", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.Ok && s.Name.Contains("Release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_lock_it_never_took_is_not_released()
    {
        // Releasing a lock this run did not take would delete somebody else's.
        var transport = new FakeTransport
        {
            FailAcquire = new PublishLockHeldException("Another copy is publishing.", null)
        };

        var steps = await RunAsync(transport);

        Assert.Equal(["lock"], transport.Events);
        Assert.Single(steps);
        Assert.False(steps[0].Ok);
    }

    [Fact]
    public async Task A_lock_that_will_not_release_is_reported_rather_than_swallowed()
    {
        var transport = new FakeTransport
        {
            Live = Encoding.UTF8.GetBytes("{}"),
            FailRelease = new IOException("the connection dropped")
        };

        var steps = await RunAsync(transport);

        var release = Assert.Single(steps, s => s.Name.Contains("Release", StringComparison.Ordinal));
        Assert.False(release.Ok);

        // And it says what to do about it, because a lock left behind blocks publishing.
        Assert.Contains("Clear publish lock", release.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_abnormal_release_is_not_reported_as_success()
    {
        var transport = new FakeTransport
        {
            Live = Encoding.UTF8.GetBytes("{}"),
            Release = PublishLockRelease.NotOurs
        };

        var steps = await RunAsync(transport);

        Assert.False(Assert.Single(steps, s => s.Name.Contains("Release", StringComparison.Ordinal)).Ok);
    }

    [Fact]
    public async Task Nothing_published_yet_is_a_pass_not_a_failure()
    {
        // Before the first publish there is genuinely no index, and the server saying so plainly is
        // the ANSWER, not an error. Reporting it as a failure would train the author to ignore this.
        var steps = await RunAsync(new FakeTransport { Live = null });

        Assert.All(steps, s => Assert.True(s.Ok));
    }

    [Fact]
    public void The_summary_says_plainly_whether_it_is_safe_to_go_on()
    {
        var (goodTitle, goodMessage) = ServerSelfTest.Describe(
            [new ServerCheckStep("Take the publish lock", true, "fine")]);
        Assert.Contains("ready", goodTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was changed", goodMessage, StringComparison.Ordinal);

        var (badTitle, badMessage) = ServerSelfTest.Describe(
            [new ServerCheckStep("Take the publish lock", false, "broken")]);
        Assert.Contains("failed", badTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shouldn't be switched on", badMessage, StringComparison.Ordinal);
    }
}
