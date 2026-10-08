using System.Diagnostics;
using System.Security.Cryptography;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Runs XLM's documented install-steam-tool operation without a terminal or game launch.</summary>
public sealed class XlmInstaller(HttpClient httpClient, ILogger logger, Action? ensureClosed = null)
{
    public static string ToolDirectory(string steamRoot) => Path.Combine(steamRoot, "compatibilitytools.d", "XLM");

    public static void Verify(string steamRoot)
    {
        var root = ToolDirectory(steamRoot);
        PathSafety.EnsureNoReparseTraversal(steamRoot, root, "XLM tool directory");
        foreach (var name in new[] { "xlm", "xlm.sh", "toolmanifest.vdf", "compatibilitytool.vdf" })
        {
            var path = Path.Combine(root, name);
            PathSafety.EnsureNoReparseTraversal(root, path, "XLM tool file");
            if (!File.Exists(path)) throw new InvalidOperationException("XLM's Steam compatibility tool is missing or incomplete. Install it again from the manager.");
        }
        if (SteamLocalConfigEditor.ReadBlock(File.ReadAllText(Path.Combine(root, "compatibilitytool.vdf")),
                "compatibilitytools", "compat_tools", "xlm") is null)
            throw new InvalidDataException("The installed tool does not declare the expected XLM compatibility tool.");
    }

    public async Task EnsureInstalledAsync(string steamRoot, Dependency dependency,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        (ensureClosed ?? XivLauncherFiles.EnsureClosed)();
        if (dependency.Fix?.Xlm is not { } auto ||
            auto.Sha256.Length != 64 || !auto.Sha256.All(Uri.IsHexDigit) ||
            !Uri.TryCreate(dependency.Fix.DownloadUrl, UriKind.Absolute, out var url))
            throw new InvalidDataException("XLM needs an HTTPS executable download and its SHA-256.");
        UrlValidator.RequireHttps(url, "XLM dependency");
        var target = ToolDirectory(steamRoot);
        if (Directory.Exists(target))
        {
            // XLM updates itself. Reuse an existing complete installation; do not downgrade it,
            // replace the user's launch script, or take ownership of their shared launcher.
            Verify(steamRoot);
            return;
        }
        var stage = Path.Combine(Path.GetTempPath(), "amm-xlm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            progress?.Report(new ProgressInfo { Percentage = 100, StatusText = "Downloading and verifying XIVLauncher's Steam tool." });
            var binary = Path.Combine(stage, "xlm");
            using (var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(binary);
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += count;
                    if (total > 128L * 1024 * 1024) throw new InvalidDataException("The XLM download exceeds its size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            await using (var stream = File.OpenRead(binary))
                if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)).Equals(auto.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The XLM download failed SHA-256 verification. It was not run.");
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var info = new ProcessStartInfo(binary)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = stage
            };
            foreach (var arg in new[] { "install-steam-tool", "--xlm-updater-disable",
                         "--extra-launch-args=--run-as-steam-compat-tool=true", "--steam-compat-path", Path.Combine(stage, "compatibilitytools.d") })
                info.ArgumentList.Add(arg);
            progress?.Report(new ProgressInfo { Percentage = 100, StatusText = "Installing XIVLauncher's Steam compatibility tool." });
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the verified XLM installer.");
            // Drain both streams concurrently; raw installer output never enters the accessible status control.
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            logger.Debug("XLM installer output: {Output}; {Error}", await stdout, await stderr);
            if (process.ExitCode != 0) throw new InvalidOperationException("XLM installation failed. Details were saved in the manager log.");
            Verify(stage);
            (ensureClosed ?? XivLauncherFiles.EnsureClosed)();
            PathSafety.EnsureNoReparseTraversal(steamRoot, target, "XLM installation");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Stage on the destination filesystem before the final atomic rename.
            var incoming = target + ".amm-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(incoming);
            try
            {
                foreach (var file in Directory.EnumerateFiles(ToolDirectory(stage)))
                    File.Copy(file, Path.Combine(incoming, Path.GetFileName(file)));
                Directory.Move(incoming, target);
            }
            finally { if (Directory.Exists(incoming)) Directory.Delete(incoming, true); }
            Verify(steamRoot);
        }
        finally { Directory.Delete(stage, true); }
    }
}
