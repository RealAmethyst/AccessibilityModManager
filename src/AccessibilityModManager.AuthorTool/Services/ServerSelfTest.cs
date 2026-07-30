using AccessibilityModManager.Infrastructure.CatalogClaims;

namespace AccessibilityModManager.AuthorTool.Services;

/// <summary>One thing that was tried against the server, and how it went.</summary>
public sealed record ServerCheckStep(string Name, bool Ok, string Detail);

/// <summary>
/// Exercises the parts of the server that signed publishing depends on, and changes nothing.
///
/// <para>It exists because of an honest gap. Ordinary publishing has used the same upload-and-switch
/// path for a long time and is well worn. Signed publishing additionally takes a lock so two copies
/// cannot publish at once, and reads the published index back over SFTP rather than over HTTPS — and
/// neither of those had ever run against the real server, only against a stand-in. They also only
/// run ON the signed path, which only exists after the key is anchored in the registry. So without
/// this, the first time that machinery met the real server would be during the first signed publish,
/// which is the worst available moment to discover a wrong path or a missing directory.</para>
///
/// <para>It takes the lock, reads the index, and releases the lock. It never uploads, never signs,
/// never writes to the project, and releases the lock however it ends — a self-test that could strand
/// a lock would be worse than no self-test, because a stranded lock blocks publishing.</para>
/// </summary>
public static class ServerSelfTest
{
    public static async Task<IReadOnlyList<ServerCheckStep>> RunAsync(
        IPublishTransport transport, string pluginId, CancellationToken ct)
    {
        var steps = new List<ServerCheckStep>();

        ServerUploadService.PublishLockHandle handle;
        try
        {
            handle = await transport.AcquireLockAsync(pluginId, ct);
            steps.Add(new ServerCheckStep("Take the publish lock", true,
                "The lock directory is reachable and writable, and nothing else holds the lock."));
        }
        catch (PublishLockHeldException ex)
        {
            // Not a failure of the machinery — it worked, and it found a lock. Worth separating,
            // because "somebody is publishing" and "this is broken" want opposite reactions.
            steps.Add(new ServerCheckStep("Take the publish lock", false,
                $"Something already holds it. {ex.Message}"));
            return steps;
        }
        catch (Exception ex)
        {
            steps.Add(new ServerCheckStep("Take the publish lock", false, ex.Message));
            return steps;
        }

        try
        {
            var live = await transport.ReadIndexAsync(pluginId, ct);

            steps.Add(new ServerCheckStep("Read the published index over SFTP", true,
                live.Present
                    ? $"Found it: {live.Bytes!.Length} bytes, " +
                      (ClaimProof.TryExtract(live.Bytes) is not null
                          ? "and it already carries a signature block."
                          : "unsigned, which is what it should be before signing is switched on.")
                    : "Nothing is published for this plugin yet, and the server said so clearly " +
                      "rather than failing — which is the answer that matters, because a failed " +
                      "read must never be mistaken for an empty catalog."));
        }
        catch (Exception ex)
        {
            steps.Add(new ServerCheckStep("Read the published index over SFTP", false, ex.Message));
        }
        finally
        {
            try
            {
                var release = await transport.ReleaseLockAsync(handle, CancellationToken.None);
                steps.Add(new ServerCheckStep("Release the publish lock",
                    release == PublishLockRelease.Released,
                    release == PublishLockRelease.Released
                        ? "Released cleanly."
                        : $"Came back {release} — the lock was not removed the way it should have been. " +
                          "Use 'Clear publish lock' if publishing then refuses."));
            }
            catch (Exception ex)
            {
                steps.Add(new ServerCheckStep("Release the publish lock", false,
                    $"{ex.Message}\n\nUse 'Clear publish lock' if publishing then refuses."));
            }
        }

        return steps;
    }

    /// <summary>Turns the results into something worth reading aloud.</summary>
    public static (string Title, string Message) Describe(IReadOnlyList<ServerCheckStep> steps)
    {
        var failed = steps.Count(s => !s.Ok);
        var body = string.Join("\n\n", steps.Select(s => $"{(s.Ok ? "Worked" : "Failed")}: {s.Name}.\n{s.Detail}"));

        return failed == 0
            ? ("Your server is ready",
               "Everything signed publishing needs from your server works.\n\n" + body +
               "\n\nNothing was changed.")
            : ($"{failed} of {steps.Count} checks failed",
               body + "\n\nNothing was changed. Signing shouldn't be switched on until these pass.");
    }
}
