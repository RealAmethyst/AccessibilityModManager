using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Finds the executable a packaged Unreal Engine game actually runs. Steam starts a small
/// bootstrap program at the install root; it reads the real program's path, relative to its own
/// folder, from its RT_RCDATA resource 201 and starts it. Mod code runs inside that real program,
/// so loader placement and per-process loader configuration follow it, while Steam's launch
/// command keeps naming the bootstrap program.
/// </summary>
public static class UnrealBootstrapStub
{
    private const int RcDataType = 10;
    private const int ExecutableResourceId = 201;
    private const int MaxDirectoryEntries = 4096;
    private const int MaxPathBytes = 1024;

    /// <summary>
    /// The game-relative executable the game process runs: the bootstrap target when
    /// <paramref name="gameExecutable"/> is an Unreal Engine bootstrap program, otherwise
    /// <paramref name="gameExecutable"/> unchanged. A bootstrap program whose target is unsafe,
    /// missing or of another architecture is refused rather than treated as the game.
    /// </summary>
    public static string ProcessExecutable(string gameDirectory, string gameExecutable) =>
        Target(gameDirectory, gameExecutable) ?? gameExecutable;

    /// <summary>The bootstrap target as a game-relative path, or null when this is not a bootstrap program.</summary>
    public static string? Target(string gameDirectory, string gameExecutable)
    {
        var stubRelative = gameExecutable.Replace('\\', '/');
        var stubPath = PathSafety.CombineContained(gameDirectory, stubRelative);
        PathSafety.EnsureNoReparseTraversal(gameDirectory, stubPath, "game executable");
        if (!File.Exists(stubPath)) return null;

        Machine stubMachine;
        string? text;
        try
        {
            using var stream = File.OpenRead(stubPath);
            using var pe = new PEReader(stream);
            stubMachine = pe.PEHeaders.CoffHeader.Machine;
            text = ReadExecutableResource(pe);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        if (text is null || !text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;

        var relative = text.Replace('\\', '/');
        var segments = relative.Split('/');
        if (relative.StartsWith('/') || relative.Any(c => char.IsControl(c) || c == ':') ||
            segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidOperationException(
                $"The game's Unreal Engine launcher names an unsafe program path '{text}'.");
        var stubFolder = Path.GetDirectoryName(stubRelative)!.Replace('\\', '/');
        var targetRelative = stubFolder.Length == 0 ? relative : stubFolder + "/" + relative;
        var targetPath = PathSafety.CombineContained(gameDirectory, targetRelative.Split('/'));
        if (targetPath.Equals(stubPath, StringComparison.Ordinal))
            throw new InvalidOperationException("The game's Unreal Engine launcher names itself as the game.");
        PathSafety.EnsureNoReparseTraversal(gameDirectory, targetPath, "game program");
        if (!File.Exists(targetPath))
            throw new InvalidOperationException(
                $"The game's Unreal Engine launcher starts '{targetRelative}', which is missing. " +
                "Verify the game files in Steam.");
        if (ReadMachine(targetPath) != stubMachine)
            throw new InvalidOperationException(
                $"'{targetRelative}' is not a Windows program with the launcher's architecture.");
        return targetRelative;
    }

    /// <summary>
    /// Reads RT_RCDATA 201 as UTF-16 text, trailing NULs removed. Offsets in the resource tree are
    /// relative to its start, and every read is bounded by the resource directory.
    /// </summary>
    private static string? ReadExecutableResource(PEReader pe)
    {
        var directory = pe.PEHeaders.PEHeader?.ResourceTableDirectory;
        if (directory is not { RelativeVirtualAddress: > 0, Size: >= 16 } resources) return null;
        var block = pe.GetSectionData(resources.RelativeVirtualAddress);
        var limit = Math.Min(block.Length, resources.Size);
        if (limit < 16) return null;
        var tree = block.GetReader(0, limit);

        var names = FindSubdirectory(tree, 0, RcDataType);
        var languages = names is null ? null : FindSubdirectory(tree, names.Value, ExecutableResourceId);
        if (languages is null) return null;
        // The bootstrap program stores one language; read the first entry, as FindResource does
        // for a neutral lookup with a single language.
        var entry = FirstEntryTarget(tree, languages.Value);
        if (entry is null || (entry.Value & 0x80000000) != 0 || entry.Value > limit - 16) return null;
        tree.Offset = (int)entry.Value;
        var dataRva = tree.ReadInt32();
        var size = tree.ReadInt32();
        if (dataRva <= 0 || size <= 0 || size > MaxPathBytes || size % 2 != 0) return null;
        var data = pe.GetSectionData(dataRva);
        if (data.Length < size) return null;
        var bytes = data.GetContent(0, size);
        var text = Encoding.Unicode.GetString(bytes.AsSpan()).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    private static int? FindSubdirectory(BlobReader tree, int directoryOffset, int id)
    {
        if (directoryOffset > tree.Length - 16) return null;
        tree.Offset = directoryOffset + 12;
        int named = tree.ReadUInt16();
        int ids = tree.ReadUInt16();
        if (named + ids > MaxDirectoryEntries || directoryOffset + 16 + (named + ids) * 8 > tree.Length)
            return null;
        tree.Offset = directoryOffset + 16 + named * 8;
        for (var index = 0; index < ids; index++)
        {
            var entryId = tree.ReadUInt32();
            var target = tree.ReadUInt32();
            if (entryId != id) continue;
            if ((target & 0x80000000) == 0) return null;
            var offset = target & 0x7FFFFFFF;
            return offset <= tree.Length - 16 ? (int)offset : null;
        }
        return null;
    }

    private static uint? FirstEntryTarget(BlobReader tree, int directoryOffset)
    {
        tree.Offset = directoryOffset + 12;
        int count = tree.ReadUInt16() + tree.ReadUInt16();
        if (count == 0 || directoryOffset + 24 > tree.Length) return null;
        tree.Offset = directoryOffset + 20;
        return tree.ReadUInt32();
    }

    private static Machine? ReadMachine(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.PEHeaders.CoffHeader.Machine;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
