using System.IO;
using System.Runtime.InteropServices;

namespace AccessibilityModManager.AuthorTool.Services;

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
/// inconvenience: the publishing journal, the registry high-water, the signing keys, and the config
/// that says which key is which.
/// </summary>
internal static partial class DurableFile
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

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existingFileName, string newFileName, uint flags);
}
