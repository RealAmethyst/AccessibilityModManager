using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AccessibilityModManager.Infrastructure.Services;

public sealed record ProtonContext(
    string ProtonExecutable,
    string SteamRoot,
    string CompatDataPath,
    string AppId);

/// <summary>
/// Installs a hash-pinned Windows Desktop Runtime inside one game's Proton prefix as the ordinary
/// Unix user. The host Linux .NET runtime is not visible to Windows code running under Proton.
/// </summary>
public sealed class ProtonWindowsDesktopRuntime(HttpClient httpClient, string cacheDirectory)
{
    public const string Version = "9.0.20";
    public static readonly Uri InstallerUrl = new(
        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/9.0.20/windowsdesktop-runtime-9.0.20-win-x64.exe");
    public const string InstallerSha512 =
        "5c7caacef8bd65a7631ff0be6cd1059b523eb9ab4e1021078921926da0bdc05b468bcbdf1f95fc12cb984bc4e97df9fbc40f942a618c716902c7c99150c4de7e";

    private const long MaxInstallerBytes = 128L * 1024 * 1024;
    private static readonly Regex RuntimeLine = new(
        @"^(Microsoft\.(?:NETCore|WindowsDesktop)\.App) (\d+\.\d+\.\d+) \[",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public async Task<bool> EnsureAsync(ProtonContext context, CancellationToken ct = default)
        => await EnsureAsync(context, Version, InstallerSha512, ct);

    public async Task<bool> EnsureAsync(
        ProtonContext context, string version, string sha512, CancellationToken ct = default)
    {
        Validate(context);
        ValidateRuntime(version, sha512);
        if (await IsInstalledAsync(context, version, ct)) return false;

        var installer = await EnsureInstallerAsync(version, sha512, ct);
        var result = await RunProtonAsync(context,
            ["runinprefix", installer, "/install", "/quiet", "/norestart"], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Windows Desktop Runtime installer exited with code {result.ExitCode} in " +
                $"Proton prefix '{context.CompatDataPath}'.");
        if (!await IsInstalledAsync(context, version, ct))
            throw new InvalidOperationException(
                $"The Windows Desktop Runtime installer exited successfully, but .NET {version} " +
                "was not available inside the game's Proton prefix.");
        return true;
    }

    public async Task<bool> IsInstalledAsync(ProtonContext context, CancellationToken ct = default)
        => await IsInstalledAsync(context, Version, ct);

    public async Task<bool> IsInstalledAsync(
        ProtonContext context, string version, CancellationToken ct = default)
    {
        Validate(context);
        ValidateRuntimeVersion(version);
        var dotnet = Path.Combine(context.CompatDataPath, "pfx", "drive_c",
            "Program Files", "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet)) return false;

        var result = await RunProtonAsync(context, ["runinprefix", dotnet, "--list-runtimes"], ct);
        if (result.ExitCode != 0) return false;

        var found = RuntimeLine.Matches(result.Output)
            .Select(m => (Name: m.Groups[1].Value, Version: System.Version.Parse(m.Groups[2].Value)))
            .ToArray();
        var minimum = new System.Version(version);
        return found.Any(r => r.Name == "Microsoft.NETCore.App" &&
                              r.Version.Major == minimum.Major && r.Version.Minor == minimum.Minor &&
                              r.Version >= minimum) &&
               found.Any(r => r.Name == "Microsoft.WindowsDesktop.App" &&
                              r.Version.Major == minimum.Major && r.Version.Minor == minimum.Minor &&
                              r.Version >= minimum);
    }

    private async Task<string> EnsureInstallerAsync(string version, string sha512, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, $"windowsdesktop-runtime-{version}-win-x64.exe");
        if (File.Exists(path))
        {
            await VerifyInstallerAsync(path, sha512, ct);
            return path;
        }

        var temp = path + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            var installerUrl = new Uri($"https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/{version}/windowsdesktop-runtime-{version}-win-x64.exe");
            using var response = await httpClient.GetAsync(installerUrl,
                HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps ||
                response.RequestMessage.RequestUri.Host != "builds.dotnet.microsoft.com" ||
                response.Content.Headers.ContentLength > MaxInstallerBytes)
                throw new InvalidDataException("Windows Desktop Runtime download is not safe to accept.");

            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                long count = 0;
                int length;
                while ((length = await input.ReadAsync(buffer, ct)) != 0)
                {
                    count += length;
                    if (count > MaxInstallerBytes)
                        throw new InvalidDataException("Windows Desktop Runtime download exceeds the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, length), ct);
                }
            }
            await VerifyInstallerAsync(temp, sha512, ct);
            File.Move(temp, path, overwrite: false);
            return path;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static async Task VerifyInstallerAsync(string path, string sha512, CancellationToken ct)
    {
        if (new FileInfo(path).Length > MaxInstallerBytes)
            throw new InvalidDataException("Windows Desktop Runtime installer exceeds the size limit.");
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA512.HashDataAsync(stream, ct));
        if (!actual.Equals(sha512, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Windows Desktop Runtime installer SHA-512 mismatch.");
    }

    private static void ValidateRuntime(string version, string sha512)
    {
        ValidateRuntimeVersion(version);
        if (sha512.Length != 128 || !sha512.All(Uri.IsHexDigit))
            throw new InvalidDataException("Windows Desktop Runtime needs a valid SHA-512 digest.");
    }

    private static void ValidateRuntimeVersion(string version)
    {
        if (!System.Version.TryParse(version, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0 ||
            version != parsed.ToString(3))
            throw new InvalidDataException("Windows Desktop Runtime needs a major.minor.patch version.");
    }

    internal static async Task<(int ExitCode, string Output)> RunProtonAsync(
        ProtonContext context, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(context.ProtonExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = context.SteamRoot
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["STEAM_COMPAT_DATA_PATH"] = context.CompatDataPath;
        start.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = context.SteamRoot;
        start.Environment["STEAM_COMPAT_APP_ID"] = context.AppId;
        start.Environment["DOTNET_ROOT"] = string.Empty;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start Proton for runtime setup.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(10), ct);
            return (process.ExitCode, await stdout + "\n" + await stderr);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    internal static void Validate(ProtonContext context)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Proton runtime setup requires Linux.");
        if (string.IsNullOrWhiteSpace(context.AppId) || !context.AppId.All(char.IsAsciiDigit) ||
            !Path.IsPathFullyQualified(context.ProtonExecutable) || !File.Exists(context.ProtonExecutable) ||
            !Path.IsPathFullyQualified(context.SteamRoot) || !Directory.Exists(context.SteamRoot) ||
            !Path.IsPathFullyQualified(context.CompatDataPath) ||
            Path.GetFileName(Path.TrimEndingDirectorySeparator(context.CompatDataPath)) != context.AppId ||
            Path.GetFileName(Path.GetDirectoryName(context.CompatDataPath)) != "compatdata" ||
            !Directory.Exists(Path.Combine(context.CompatDataPath, "pfx")))
            throw new InvalidOperationException("Proton runtime setup needs a valid Steam game prefix and Proton executable.");
    }
}
