namespace AccessibilityModManager.Infrastructure.Installer;

/// <summary>
/// Helpers for locating a portable app (emulator) inside the folder the manager extracted it to.
/// Kept as a standalone static so both the install flow and the reuse-across-games check share one
/// definition of "where does the exe actually live" — and so it's unit-testable without the WPF VM.
/// See EMULATOR_INSTALL_QUESTIONS.md (F4).
/// </summary>
public static class PortableAppLayout
{
    /// <summary>
    /// The folder that actually holds <paramref name="exeName"/>: <paramref name="folder"/> itself
    /// if the exe sits at its top level (the expected layout), or its single sub-folder if the ZIP
    /// wrapped everything in one directory (e.g. <c>MyEmulator/emulator.exe</c>). Returns
    /// <paramref name="folder"/> unchanged when no exe name is given, and null when the folder is
    /// missing or the exe can't be found in either place.
    /// </summary>
    public static string? ResolveInstallRoot(string folder, string? exeName)
    {
        if (!Directory.Exists(folder)) return null;
        if (string.IsNullOrWhiteSpace(exeName)) return folder;
        if (File.Exists(Path.Combine(folder, exeName))) return folder;

        string[] subs;
        try { subs = Directory.GetDirectories(folder); }
        catch { return null; }
        if (subs.Length == 1 && File.Exists(Path.Combine(subs[0], exeName))) return subs[0];

        return null;
    }
}
