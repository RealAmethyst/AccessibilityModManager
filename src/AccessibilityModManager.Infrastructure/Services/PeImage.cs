using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Reads a Windows image's machine and its import table: imported DLL names and the functions
/// imported by name. Ordinal imports are omitted. Invalid images return null.
/// </summary>
public sealed record PeImage(Machine Machine, IReadOnlyDictionary<string, IReadOnlySet<string>> Imports)
{
    private const int MaxDescriptors = 4096;
    // Bounds the work a malformed image can cause across all import descriptors.
    private const int MaxImportedFunctions = 65536;
    private const int MaxNameLength = 512;

    public bool ImportsFunction(string function) =>
        Imports.Values.Any(functions => functions.Contains(function));

    public static PeImage? Read(string path)
    {
        try
        {
            return Read(File.ReadAllBytes(path));
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

    public static PeImage? Read(byte[] image)
    {
        try
        {
            using var stream = new MemoryStream(image, writable: false);
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            var imports = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
            var directory = headers.PEHeader?.ImportTableDirectory;
            if (directory is null || directory.Value.RelativeVirtualAddress == 0 || directory.Value.Size < 20)
                return new PeImage(headers.CoffHeader.Machine, imports);

            var pe32Plus = headers.PEHeader!.Magic == PEMagic.PE32Plus;
            var thunkSize = pe32Plus ? 8 : 4;
            var descriptor = FileOffset(headers.SectionHeaders, image.Length,
                directory.Value.RelativeVirtualAddress);
            if (descriptor < 0) return new PeImage(headers.CoffHeader.Machine, imports);
            var thunks = 0;
            for (var index = 0; index < MaxDescriptors && descriptor + 20 <= image.Length;
                 index++, descriptor += 20)
            {
                var lookupRva = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(descriptor, 4));
                var nameRva = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(descriptor + 12, 4));
                var addressRva = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(descriptor + 16, 4));
                if (nameRva == 0) break;
                var dll = ReadName(image, FileOffset(headers.SectionHeaders, image.Length, nameRva));
                if (dll is null) continue;

                var functions = imports.TryGetValue(dll, out var existing)
                    ? (HashSet<string>)existing
                    : new HashSet<string>(StringComparer.Ordinal);
                imports[dll] = functions;
                var thunk = FileOffset(headers.SectionHeaders, image.Length,
                    lookupRva != 0 ? lookupRva : addressRva);
                for (; thunk >= 0 && thunk + thunkSize <= image.Length &&
                       thunks < MaxImportedFunctions; thunks++, thunk += thunkSize)
                {
                    var value = pe32Plus
                        ? BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(thunk, 8))
                        : BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(thunk, 4));
                    if (value == 0) break;
                    var ordinal = pe32Plus ? (value & 0x8000000000000000UL) != 0 : (value & 0x80000000UL) != 0;
                    if (ordinal || value > int.MaxValue) continue;
                    var hint = FileOffset(headers.SectionHeaders, image.Length, (int)value);
                    if (hint < 0) continue;
                    if (ReadName(image, hint + 2) is { } function) functions.Add(function);
                }
            }
            return new PeImage(headers.CoffHeader.Machine, imports);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadName(byte[] image, int offset)
    {
        if (offset < 0 || offset >= image.Length) return null;
        var end = offset;
        while (end < image.Length && end - offset < MaxNameLength && image[end] != 0) end++;
        if (end == image.Length || end == offset || end - offset == MaxNameLength) return null;
        return Encoding.ASCII.GetString(image, offset, end - offset);
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
