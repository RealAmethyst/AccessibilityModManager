namespace AccessibilityModManager.Infrastructure.Security;

/// <summary>
/// Reading a local file with a ceiling, so a damaged or planted one cannot be buffered whole before
/// anything looks at its size.
///
/// <para>The network reads are bounded while they stream; these are the same documents once they are
/// on disk, and <c>File.ReadAllBytes</c> on a cache envelope or a replay record is the one place the
/// ceiling had been left off. Nothing here is a trust check — the trust checks run on the contents —
/// it is only a refusal to load an implausible amount of it.</para>
/// </summary>
public static class BoundedFile
{
    /// <summary>
    /// The whole file, or a refusal naming <paramref name="what"/>. Throws
    /// <see cref="FileNotFoundException"/> / <see cref="DirectoryNotFoundException"/> exactly as a
    /// plain read would, because callers distinguish "absent" from "unreadable" and that distinction
    /// must survive.
    /// </summary>
    public static byte[] ReadAllBytes(string path, int maxBytes, string what)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Checked before allocating, and re-checked while reading: Length is a snapshot, and a file
        // being appended to between the two would otherwise slip past it.
        if (stream.Length > maxBytes)
            throw new InvalidOperationException($"The {what} is larger than {maxBytes} bytes. Refusing it.");

        using var buffer = new MemoryStream((int)stream.Length);
        var chunk = new byte[81920];

        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidOperationException($"The {what} is larger than {maxBytes} bytes. Refusing it.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
