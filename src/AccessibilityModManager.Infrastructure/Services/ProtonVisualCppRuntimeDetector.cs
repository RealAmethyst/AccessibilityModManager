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
        if (PeImage.Read(image) is not { } pe) return VisualCppArchitecture.None;
        var architecture = pe.Machine switch
        {
            Machine.Amd64 => VisualCppArchitecture.X64,
            Machine.I386 => VisualCppArchitecture.X86,
            _ => VisualCppArchitecture.None
        };
        return pe.Imports.Keys.Any(name =>
            (name.StartsWith("msvcp140", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("vcruntime140", StringComparison.OrdinalIgnoreCase)) &&
            name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            ? architecture : VisualCppArchitecture.None;
    }
}
