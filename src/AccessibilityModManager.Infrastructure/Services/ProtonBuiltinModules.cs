using System.Reflection.PortableExecutable;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Lists the Windows DLLs that a Proton build supplies itself. Wine prefers these built-in
/// modules over a DLL beside the game, while Windows loads the DLL beside the game first.
/// </summary>
public static class ProtonBuiltinModules
{
    public static IReadOnlySet<string> Read(string protonExecutable, Machine machine)
    {
        var architecture = machine switch
        {
            Machine.Amd64 => "x86_64-windows",
            Machine.I386 => "i386-windows",
            _ => throw new InvalidOperationException("Proton built-in DLLs are only known for x86 and x64 games.")
        };
        var root = Path.GetDirectoryName(Path.GetFullPath(protonExecutable))!;
        var directory = new[]
            {
                Path.Combine(root, "files", "lib", "wine", architecture),
                Path.Combine(root, "files", "lib64", "wine", architecture),
                Path.Combine(root, "dist", "lib", "wine", architecture)
            }
            .FirstOrDefault(Directory.Exists)
            ?? throw new InvalidOperationException(
                $"The selected Proton has no {architecture} built-in DLL folder. Choose another Proton version.");
        var modules = Directory.EnumerateFiles(directory, "*.dll")
            .Select(path => Path.GetFileNameWithoutExtension(path).ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (modules.Count == 0)
            throw new InvalidOperationException("The selected Proton lists no built-in Windows DLLs.");
        return modules;
    }
}
