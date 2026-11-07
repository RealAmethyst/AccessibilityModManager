using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace AccessibilityModManager.PortableTests;

/// <summary>Builds a minimal Windows image with a real import table for loader detection tests.</summary>
internal static class TestPe
{
    private const int SectionRva = 0x1000;
    private const int SectionFileOffset = 0x200;

    public static byte[] Build(Machine machine, params (string Dll, string[] Functions)[] imports) =>
        Build(machine, null, imports);

    /// <summary>
    /// An Unreal Engine bootstrap program: RT_RCDATA 201 holds the real program's path relative to
    /// this one, and 202 the project name, both as UTF-16 text.
    /// </summary>
    public static byte[] UnrealBootstrap(Machine machine, string target, string project = "Game\0") =>
        Build(machine, new Dictionary<int, byte[]>
        {
            [201] = Encoding.Unicode.GetBytes(target),
            [202] = Encoding.Unicode.GetBytes(project)
        }, ("KERNEL32.dll", ["CreateProcessW", "WaitForSingleObject"]));

    public static byte[] Build(Machine machine, IReadOnlyDictionary<int, byte[]>? rcData,
        params (string Dll, string[] Functions)[] imports)
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

        var resources = -1;
        var resourcesSize = 0;
        if (rcData is not null)
        {
            while (section.Count % 4 != 0) section.Add(0);
            resources = section.Count;
            var tree = ResourceTree(rcData, SectionRva + resources);
            section.AddRange(tree);
            resourcesSize = tree.Length;
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
        if (resources >= 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(directories + 16), SectionRva + resources);
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(directories + 20), resourcesSize);
        }

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

    /// <summary>A resource tree with one RT_RCDATA type, one language per id, and data after the tree.</summary>
    private static byte[] ResourceTree(IReadOnlyDictionary<int, byte[]> rcData, int treeRva)
    {
        var ids = rcData.Keys.Order().ToArray();
        var typeDirectory = 16 + 8;
        var nameDirectories = typeDirectory + 16 + ids.Length * 8;
        var dataEntries = nameDirectories + ids.Length * 24;
        var data = dataEntries + ids.Length * 16;
        var buffer = new List<byte>(new byte[data]);
        WriteDirectory(0, [(10, 0x80000000u | (uint)typeDirectory)]);
        WriteDirectory(typeDirectory, ids.Select((id, index) =>
            (id, 0x80000000u | (uint)(nameDirectories + index * 24))).ToArray());
        for (var index = 0; index < ids.Length; index++)
        {
            WriteDirectory(nameDirectories + index * 24, [(0x0409, (uint)(dataEntries + index * 16))]);
            while (buffer.Count % 4 != 0) buffer.Add(0);
            var offset = buffer.Count;
            buffer.AddRange(rcData[ids[index]]);
            WriteInt(buffer, dataEntries + index * 16, treeRva + offset);
            WriteInt(buffer, dataEntries + index * 16 + 4, rcData[ids[index]].Length);
        }
        return buffer.ToArray();

        void WriteDirectory(int offset, (int Id, uint Target)[] entries)
        {
            buffer[offset + 14] = (byte)entries.Length;
            for (var entry = 0; entry < entries.Length; entry++)
            {
                WriteInt(buffer, offset + 16 + entry * 8, entries[entry].Id);
                WriteInt(buffer, offset + 20 + entry * 8, unchecked((int)entries[entry].Target));
            }
        }
    }

    private static void WriteInt(List<byte> buffer, int offset, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        for (var index = 0; index < 4; index++) buffer[offset + index] = bytes[index];
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
