// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using XIVLauncher.Common;
using XIVLauncher.Common.Unix.Compatibility;

namespace XIVLauncher.Core;

internal static class WineSpeechSetup
{
    private const string Key = @"HKCU\Software\Wine";
    internal sealed record Result(int ExitCode, string Output);

    public static void Prepare(CompatibilityTools tools, Storage storage)
    {
        Result Run(string[] args)
        {
            using var process = tools.RunInPrefix(args, redirectOutput: true, writeLog: true);
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return new Result(process.ExitCode, output);
        }
        Ensure(Run, () =>
        {
            var folder = storage.GetFolder("amm-backups");
            var path = Path.Combine(folder.FullName, "wine-before-speech-" + Guid.NewGuid().ToString("N") + ".reg");
            return tools.UnixToWinePath(path);
        });
    }

    internal static void Ensure(Func<string[], Result> run, Func<string> backupPath)
    {
        // Keep Wine-XIV's version exports hidden: FFXIV uses them for platform detection.
        // The manager supplies Prism with Wine detection that does not expose them.
        var query = run(["reg", "query", Key, "/v", "HideWineExports"]);
        if (query.ExitCode == 0 && Regex.IsMatch(query.Output,
            @"^\s*HideWineExports\s+REG_SZ\s+Y\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return;
        if (query.ExitCode is not (0 or 1))
            throw new InvalidOperationException("Could not check Wine's speech compatibility setting.");
        var backup = run(["reg", "export", Key, backupPath()]);
        if (backup.ExitCode != 0)
            throw new InvalidOperationException("Could not back up Wine settings before restoring game compatibility. The game was not started.");
        var write = run(["reg", "add", Key, "/v", "HideWineExports", "/t", "REG_SZ", "/d", "Y", "/f"]);
        if (write.ExitCode != 0)
            throw new InvalidOperationException("Could not restore Wine's game compatibility setting. The game was not started.");
    }
}
