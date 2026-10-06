using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;

namespace AccessibilityModManager.Infrastructure.Services;

[Flags]
public enum VisualCppArchitecture
{
    None = 0,
    X86 = 1,
    X64 = 2
}

/// <summary>Finds VC++ v14 imports in native files that a verified Proton package will install.</summary>
public static class ProtonVisualCppRuntimeDetector
{
    private const long MaxInspectedFileBytes = 128L * 1024 * 1024;

    public static VisualCppArchitecture Detect(ZipArchive archive)
    {
        var required = VisualCppArchitecture.None;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (!name.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
                !(name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                  name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (entry.Length > MaxInspectedFileBytes)
            {
                // A large native module still needs a working runtime. Installing both versions
                // is safer than claiming an uninspected module has no VC++ imports.
                required |= VisualCppArchitecture.X86 | VisualCppArchitecture.X64;
                continue;
            }

            using var input = entry.Open();
            using var output = new MemoryStream((int)entry.Length);
            input.CopyTo(output);
            required |= Detect(output.ToArray());
            if (required == (VisualCppArchitecture.X86 | VisualCppArchitecture.X64)) break;
        }
        return required;
    }

    public static VisualCppArchitecture Detect(byte[] image)
    {
        try
        {
            using var stream = new MemoryStream(image, writable: false);
            using var pe = new PEReader(stream);
            var architecture = pe.PEHeaders.CoffHeader.Machine switch
            {
                Machine.Amd64 => VisualCppArchitecture.X64,
                Machine.I386 => VisualCppArchitecture.X86,
                _ => VisualCppArchitecture.None
            };
            var directory = pe.PEHeaders.PEHeader?.ImportTableDirectory;
            if (architecture == VisualCppArchitecture.None || directory is null ||
                directory.Value.RelativeVirtualAddress == 0 || directory.Value.Size < 20)
                return VisualCppArchitecture.None;

            var descriptor = FileOffset(pe.PEHeaders.SectionHeaders, image.Length,
                directory.Value.RelativeVirtualAddress);
            if (descriptor < 0) return VisualCppArchitecture.None;
            var count = Math.Min(directory.Value.Size / 20, 4096);
            for (var index = 0; index < count && descriptor + 20 <= image.Length; index++, descriptor += 20)
            {
                var nameRva = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(descriptor + 12, 4));
                if (nameRva == 0) break;
                var nameOffset = FileOffset(pe.PEHeaders.SectionHeaders, image.Length, nameRva);
                if (nameOffset < 0) continue;
                var end = nameOffset;
                while (end < image.Length && end - nameOffset < 128 && image[end] != 0) end++;
                if (end == image.Length || end - nameOffset == 128) continue;
                var name = System.Text.Encoding.ASCII.GetString(image, nameOffset, end - nameOffset);
                if ((name.StartsWith("msvcp140", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("vcruntime140", StringComparison.OrdinalIgnoreCase)) &&
                    name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    return architecture;
            }
        }
        catch (BadImageFormatException) { }
        catch (IOException) { }
        return VisualCppArchitecture.None;
    }

    private static int FileOffset(IReadOnlyList<SectionHeader> sections, int length, int rva)
    {
        foreach (var section in sections)
        {
            var delta = (long)rva - section.VirtualAddress;
            if (delta < 0 || delta >= section.SizeOfRawData) continue;
            var offset = section.PointerToRawData + delta;
            if (offset >= 0 && offset < length) return (int)offset;
        }
        return -1;
    }
}
