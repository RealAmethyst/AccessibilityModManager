using System.Diagnostics;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Runs from the extracted package, so an upgrade never overwrites its running helper.</summary>
public static class LinuxInstallCommand
{
    public static bool TryRun(string[] args, LinuxApplication app, out string? error)
    {
        error = null;
        if (args.Length == 0 || args[0] is not ("--install" or "--apply-update")) return false;
        try
        {
            if (args[0] == "--apply-update")
            {
                if (app != LinuxApplication.Manager || args.Length != 2 || !int.TryParse(args[1], out var pid) || pid <= 0)
                    throw new InvalidOperationException("Invalid manager update handoff.");
                try
                {
                    using var parent = Process.GetProcessById(pid);
                    if (!parent.WaitForExit(60000))
                        throw new IOException("The manager is still running. Close it before installing the update.");
                }
                catch (ArgumentException) { /* The manager has already exited. */ }
            }
            var result = LinuxApplicationInstaller.Install(AppContext.BaseDirectory, app);
            Console.WriteLine(app.Name + " installed. Open it from your application menu.");
            if (result.BackupDirectory is not null) Console.WriteLine("Previous build: " + result.BackupDirectory);
            if (args[0] == "--apply-update")
            {
                _ = Process.Start(new ProcessStartInfo(result.Executable) { UseShellExecute = false,
                    ArgumentList = { "--updated" } }) ?? throw new IOException("The update installed, but the manager could not restart. Open it from the application menu.");
                // This helper runs in our private downloaded package, never in the live install.
                var extraction = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
                var download = extraction?.Parent;
                if (extraction?.Name == "unpacked" && download?.Name.StartsWith("amm-update-", StringComparison.Ordinal) == true &&
                    download.Parent?.FullName == Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
                {
                    try { download.Delete(true); }
                    catch (IOException) { /* A temporary download can be removed later. */ }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex)
        {
            error = "Installation failed: " + ex.Message;
            Console.Error.WriteLine(error);
            Environment.ExitCode = 1;
        }
        return true;
    }
}
