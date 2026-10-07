using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using AccessibilityModManager.Infrastructure.Security;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed record LinuxApplication(string Id, string Name, string Executable, string DesktopFile, string Category)
{
    public static readonly LinuxApplication Manager = new("AccessibilityModManager", "Accessibility Mod Manager",
        "AccessibilityModManager.LinuxApp", "accessibility-mod-manager.desktop", "Game");
    public static readonly LinuxApplication Author = new("AccessibilityModManager-Author", "Plugin Index Author",
        "AccessibilityModManager.LinuxAuthorTool", "accessibility-mod-author.desktop", "Development");
}

public sealed record LinuxPackageMetadata(string Product, string Version, string RuntimeIdentifier);
public sealed record LinuxInstallResult(string Executable, string? BackupDirectory);

/// <summary>Per-user installation, shared by first installation and the verified in-app updater.</summary>
public static class LinuxApplicationInstaller
{
    public static string DataRoot => Path.GetFullPath(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
        ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"));

    public static string InstallDirectory(LinuxApplication app, string? dataRoot = null) =>
        Path.Combine(dataRoot ?? DataRoot, app.Id, "linux-x64");

    public static LinuxPackageMetadata ValidatePackage(string source, LinuxApplication app, Version? expectedVersion = null)
    {
        var metadata = JsonSerializer.Deserialize<LinuxPackageMetadata>(File.ReadAllText(Path.Combine(source, "release.json")))
            ?? throw new InvalidDataException("The Linux package metadata is missing.");
        if (metadata.Product != app.Id || metadata.RuntimeIdentifier != "linux-x64" ||
            !Version.TryParse(metadata.Version, out var version) || expectedVersion is not null && version != expectedVersion)
            throw new InvalidDataException("The package is for a different application, version or platform.");
        foreach (var name in new[] { app.Executable, app.Executable + ".dll", "libhostfxr.so", "libcoreclr.so" })
        {
            var path = Path.Combine(source, name);
            PathSafety.EnsureNoReparseTraversal(source, path, "application package");
            if (!File.Exists(path)) throw new InvalidDataException("The Linux package is incomplete: " + name);
        }
        return metadata;
    }

    public static LinuxInstallResult Install(string source, LinuxApplication app, string? dataRoot = null)
    {
        if (!OperatingSystem.IsLinux() || UpdateChecker.CurrentRuntime != "linux-x64")
            throw new PlatformNotSupportedException("This package requires x86-64 Linux.");
        source = Path.GetFullPath(source);
        ValidatePackage(source, app);
        var root = Path.GetFullPath(dataRoot ?? DataRoot);
        var destination = InstallDirectory(app, root);
        if (source == destination || source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Extract the new download outside the installed application folder first.");
        var appRoot = Path.GetDirectoryName(destination)!;
        var desktops = Path.Combine(root, "applications");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(desktops);
        PathSafety.EnsureNoReparseTraversal(root, destination, "application installation");
        PathSafety.EnsureNoReparseTraversal(root, desktops, "application menu");
        using var installLock = new FileStream(Path.Combine(appRoot, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Directory.Exists(destination) && !File.Exists(Path.Combine(destination, app.Executable)))
            throw new IOException("The destination exists but does not contain this application. No files were changed.");
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..8];
        var stage = destination + ".new-" + stamp;
        var backup = destination + ".backup-" + stamp;
        var desktop = Path.Combine(desktops, app.DesktopFile);
        var desktopTemp = desktop + ".new-" + stamp;
        PathSafety.EnsureNoReparseTraversal(root, desktop, "application shortcut");
        var oldDesktop = File.Exists(desktop) ? File.ReadAllBytes(desktop) : null;
        var movedOld = false;
        var movedNew = false;
        try
        {
            CopyTree(source, stage);
            ValidatePackage(stage, app);
            var executable = Path.Combine(destination, app.Executable);
            File.WriteAllText(desktopTemp, DesktopEntry(app, executable));
            File.SetUnixFileMode(desktopTemp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            if (Directory.Exists(destination)) { Directory.Move(destination, backup); movedOld = true; }
            Directory.Move(stage, destination);
            movedNew = true;
            if (oldDesktop is not null) File.WriteAllBytes(desktop + ".backup-" + stamp, oldDesktop);
            File.Move(desktopTemp, desktop, true);
            return new LinuxInstallResult(executable, movedOld ? backup : null);
        }
        catch
        {
            if (movedNew) Directory.Delete(destination, true);
            if (movedOld) Directory.Move(backup, destination);
            if (oldDesktop is null) { if (File.Exists(desktop)) File.Delete(desktop); }
            else File.WriteAllBytes(desktop, oldDesktop);
            throw;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (File.Exists(desktopTemp)) File.Delete(desktopTemp);
        }
    }

    public static async Task<string> ExtractUpdateAsync(string archive, UpdateInfo info, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (info.RuntimeIdentifier != "linux-x64") throw new InvalidDataException("Not a Linux update.");
        var extraction = Path.Combine(Path.GetDirectoryName(archive)!, "unpacked");
        if (Path.Exists(extraction)) throw new IOException("The update extraction directory already exists.");
        Directory.CreateDirectory(extraction);
        try
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using var reader = new TarReader(gzip);
            TarEntry? entry;
            long size = 0;
            int count = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while ((entry = await reader.GetNextEntryAsync(false, ct)) is not null)
            {
                if (++count > 20000 || (size += entry.Length) > 2L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("The update archive exceeds its size limits.");
                if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("Links and special files are not allowed in application updates.");
                var path = PathSafety.CombineContained(extraction, entry.Name.TrimEnd('/'));
                if (path == extraction || !seen.Add(path)) throw new InvalidDataException("Repeated or invalid archive path.");
                if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(path);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, ct);
                    }
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead |
                        UnixFileMode.OtherRead | (entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)));
                }
            }
            var package = Path.Combine(extraction, $"AccessibilityModManager-{info.Version}-linux-x64");
            if (Directory.GetFileSystemEntries(extraction).Length != 1 || !Directory.Exists(package))
                throw new InvalidDataException("The archive does not contain the expected manager package.");
            ValidatePackage(package, LinuxApplication.Manager, info.Version);
            return package;
        }
        catch { Directory.Delete(extraction, true); throw; }
    }

    private static void CopyTree(string source, string destination)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Application packages cannot contain symbolic links.");
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Application packages cannot contain symbolic links.");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyTree(entry, target);
            else
            {
                File.Copy(entry, target);
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead |
                    UnixFileMode.OtherRead | (File.GetUnixFileMode(entry) &
                    (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)));
            }
        }
    }

    private static string DesktopEntry(LinuxApplication app, string executable)
    {
        // Desktop entries have string escaping followed by Exec quoting, not shell escaping.
        if (executable.Any(c => c is '\n' or '\r' or '\t' or '='))
            throw new InvalidOperationException("The installation path contains unsupported desktop-entry characters.");
        var argument = executable.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");
        argument = argument.Replace("\\", "\\\\");
        return $"[Desktop Entry]\nType=Application\nName={app.Name}\nExec=\"{argument}\"\nTerminal=false\nCategories={app.Category};\n";
    }
}
