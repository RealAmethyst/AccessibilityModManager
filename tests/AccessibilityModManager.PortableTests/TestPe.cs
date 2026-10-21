using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace AccessibilityModManager.PortableTests;

/// <summary>Builds a minimal Windows image with a real import table for loader detection tests.</summary>
internal static class TestPe
{
    private const int SectionRva = 0x1000;
    private const int SectionFileOffset = 0x200;

    public static byte[] Build(Machine machine, params (string Dll, string[] Functions)[] imports)
    {
        var pe32Plus = machine == Machine.Amd64;
        var thunkSize = pe32Plus ? 8 : 4;
        var section = new List<byte>();
        var descriptorsSize = (imports.Length + 1) * 20;
        section.AddRange(new byte[descriptorsSize]);
        for (var index = 0; index < imports.Length; index++)
        {
            var (dll, functions) = imports[index];
            var lookup = section.Count;
            section.AddRange(new byte[(functions.Length + 1) * thunkSize]);
            for (var function = 0; function < functions.Length; function++)
            {
                var hint = section.Count;
                section.AddRange(new byte[2]);
                section.AddRange(Encoding.ASCII.GetBytes(functions[function]));
                section.Add(0);
                if (section.Count % 2 != 0) section.Add(0);
                var thunk = BitConverter.GetBytes((long)(SectionRva + hint));
                for (var b = 0; b < thunkSize; b++) section[lookup + function * thunkSize + b] = thunk[b];
            }
            var name = section.Count;
            section.AddRange(Encoding.ASCII.GetBytes(dll));
            section.Add(0);
            var descriptor = index * 20;
            WriteInt(section, descriptor, SectionRva + lookup);
            WriteInt(section, descriptor + 12, SectionRva + name);
            WriteInt(section, descriptor + 16, SectionRva + lookup);
        }

        var rawSize = Align(Math.Max(section.Count, 1), 0x200);
        var image = new byte[SectionFileOffset + rawSize];
        section.CopyTo(image, SectionFileOffset);
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3c), 0x40);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(image, 0x40);
        var coff = 0x44;
        var optionalSize = pe32Plus ? 240 : 224;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff), (ushort)machine);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 16), (ushort)optionalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 18), 0x0022);

        var optional = coff + 20;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), (ushort)(pe32Plus ? 0x20b : 0x10b));
        // Both header formats place SectionAlignment at offset 32.
        var afterBase = optional + 32;
        if (pe32Plus) BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(optional + 24), 0x140000000);
        else BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(optional + 28), 0x400000);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(afterBase), 0x1000);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(afterBase + 4), 0x200);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(afterBase + 8), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(afterBase + 16), 6);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(afterBase + 24),
            SectionRva + Align(rawSize, 0x1000));
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(afterBase + 28), SectionFileOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(afterBase + 36), 3);
        var sizes = afterBase + 40;
        var rvaCount = sizes + (pe32Plus ? 36 : 20);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(rvaCount), 16);
        var directories = rvaCount + 4;
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(directories + 8), SectionRva);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(directories + 12), descriptorsSize);

        var header = optional + optionalSize;
        Encoding.ASCII.GetBytes(".idata").CopyTo(image, header);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(header + 8), section.Count);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(header + 12), SectionRva);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(header + 16), rawSize);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(header + 20), SectionFileOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(header + 36), 0xC0000040);
        return image;
    }

    /// <summary>A launcher that starts the game suspended and writes a module into it.</summary>
    public static byte[] Injector(Machine machine) => Build(machine,
        ("KERNEL32.dll", ["CreateProcessW", "VirtualAllocEx", "WriteProcessMemory",
            "CreateRemoteThread", "ResumeThread"]));

    public static byte[] Dll(Machine machine) => Build(machine, ("KERNEL32.dll", ["GetProcAddress"]));

    public static byte[] Game(Machine machine, params string[] importedDlls) => Build(machine,
        [("KERNEL32.dll", ["ExitProcess"]), .. importedDlls.Select(dll => (dll, new[] { "Init" }))]);

    /// <summary>Creates a Proton directory whose built-in DLL list contains <paramref name="modules"/>.</summary>
    public static string Proton(string root, params string[] modules)
    {
        var proton = Path.Combine(root, "proton-tree", "proton");
        foreach (var architecture in new[] { "x86_64-windows", "i386-windows" })
        {
            var directory = Path.Combine(root, "proton-tree", "files", "lib", "wine", architecture);
            Directory.CreateDirectory(directory);
            foreach (var module in modules)
                File.WriteAllText(Path.Combine(directory, module + ".dll"), "builtin");
        }
        if (!File.Exists(proton)) File.WriteAllText(proton, "proton");
        return proton;
    }

    private static void WriteInt(List<byte> buffer, int offset, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        for (var index = 0; index < 4; index++) buffer[offset + index] = bytes[index];
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
