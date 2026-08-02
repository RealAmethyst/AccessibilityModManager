namespace AccessibilityModManager.Infrastructure.Security;

/// <summary>
/// Serializes a read-compare-write across app COPIES, not just threads.
///
/// <para>Every ratchet in this codebase — the registry high-water, the claim replay store — has the
/// same shape: read the recorded position, compare, write the new one. Without a cross-process lock
/// two copies of the manager can each read the same position and then write their acceptances in
/// reverse order, leaving the marker at the OLDER of two accepted catalogs. That is a silent
/// regression of the exact protection the marker exists to give.</para>
///
/// <para><b>It always fails closed.</b> An earlier version took a "required" flag and, when false,
/// carried on with in-process serialization only. Every caller passed true, and the false branch was
/// a way to end up holding no lock while believing otherwise, so it is gone. The guarded transaction
/// takes milliseconds; a lock that stays busy for the full wait is a machine in trouble, not
/// contention. The OS releases the handle however a holder dies, so a crashed process cannot leave
/// one behind.</para>
/// </summary>
public static class CrossProcessFileLock
{
    private const int AttemptCount = 100;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Takes the lock at <paramref name="lockPath"/>, creating its directory if needed. The returned
    /// stream is the lock — dispose it to release. Throws when the lock cannot be taken;
    /// <paramref name="what"/> names it in the message.
    /// </summary>
    public static async Task<FileStream> AcquireAsync(string lockPath, string what)
    {
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"couldn't prepare the {what} lock", ex);
            }
        }

        for (var attempt = 0; attempt < AttemptCount; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Held by another copy. Only IOException means contention — anything else is a
                // fault, and retrying a fault a hundred times just delays the refusal.
                await Task.Delay(RetryDelay);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"couldn't acquire the {what} lock", ex);
            }
        }

        throw new InvalidOperationException($"the {what} lock stayed busy");
    }
}
