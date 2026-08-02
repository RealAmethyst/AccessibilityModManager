using System.IO;
using System.Runtime.InteropServices;

namespace AccessibilityModManager.Infrastructure.Security;

/// <summary>
/// Writing a file so that a machine which loses power has either the old contents or the new ones,
/// and never a cache's promise of the new ones.
///
/// Both halves have to be durable, and getting only the first half right was a real mistake here:
/// the content was written through, and then <c>File.Move</c> committed the rename through the
/// ordinary cache. .NET's move calls <c>MoveFileEx</c> with <c>MOVEFILE_COPY_ALLOWED</c> and
/// <c>MOVEFILE_REPLACE_EXISTING</c>, never <c>MOVEFILE_WRITE_THROUGH</c> — the flag Windows
/// documents as "do not return until the move is on the disk". A perfectly flushed temporary file
/// could still be followed by a lost rename.
///
/// Used for every file whose disappearance would be a security problem rather than an
/// inconvenience: on the publishing side the journal, the signing keys and the config that says
/// which key is which; on the manager side the registry high-water marker and the claim replay
/// records. Those last two are ratchets — losing a committed advance is exactly a rollback, and
/// a rollback is what they exist to refuse — so an ordinary atomic write is not enough for them.
/// </summary>
public static class DurableFile
{
    public static void Write(string path, byte[] bytes)
    {
        var temp = path + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                   bufferSize: 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        Replace(temp, path);
    }

    public static void Write(string path, string text) =>
        Write(path, System.Text.Encoding.UTF8.GetBytes(text));

    private static void Replace(string from, string to)
    {
        if (!MoveFileExW(Extended(from), Extended(to), MoveFileReplaceExisting | MoveFileWriteThrough))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// The extended-length form, because the Win32 call does not get the path handling that
    /// <c>System.IO</c> applies for free. Unprefixed <c>MoveFileExW</c> paths are capped at
    /// MAX_PATH, so a long or redirected profile directory would let the temporary file be written
    /// and flushed and then make the durable rename fail — publishing blocked by a path length.
    /// </summary>
    private static string Extended(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full[2..];
        return @"\\?\" + full;
    }

    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;

    /// <summary>
    /// <c>DllImport</c> rather than <c>LibraryImport</c>: the source generator emits unsafe
    /// marshalling code, which would mean turning <c>AllowUnsafeBlocks</c> on for the whole of
    /// Infrastructure to gain nothing here. Two UTF-16 strings and a flag word marshal the same
    /// either way.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW",
        CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);
}
