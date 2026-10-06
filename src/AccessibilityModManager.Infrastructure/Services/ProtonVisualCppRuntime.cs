using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Installs a pinned Microsoft VC++ v14 runtime inside one game's Proton prefix.</summary>
public sealed class ProtonVisualCppRuntime(HttpClient httpClient, string cacheDirectory)
{
    public const string Version = "14.51.36247.0";
    public const string X64Sha512 =
        "c42c82cfd5eaa2165b600f6109cb14ff4308cdf4cff656a1375b4a933f1630bfe54c99c4d0fb9ed3bea4dc4e4fdaa3db8e7c58356cca07c2954f4c297757d62d";
    public const string X86Sha512 =
        "df8e1806e5fb27522e9f985a21ee79c1853bbdaf8a9f3f586e6033ef67f864c578c0673b6eefb86e915e16004a42d262d504d000910d9cfc403f5a7f78fcb575";

    private const long MaxInstallerBytes = 64L * 1024 * 1024;
    private static readonly Uri X64Url = new(
        "https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe");
    private static readonly Uri X86Url = new(
        "https://download.visualstudio.microsoft.com/download/pr/ece44298-3977-4f73-ab91-c13fe79cfea8/F0BAB33A302B3CDB2E11113760D016F54FD3D2632C65BA7834FAC4F0ABD7F1A3/VC_redist.x86.exe");
    private static readonly Regex InstalledVersion = new(
        @"\bVersion\s+REG_SZ\s+v?(\d+\.\d+\.\d+\.\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task EnsureAsync(ProtonContext context, VisualCppArchitecture architectures,
        CancellationToken ct = default)
    {
        ProtonWindowsDesktopRuntime.Validate(context);
        foreach (var architecture in new[] { VisualCppArchitecture.X64, VisualCppArchitecture.X86 })
        {
            if ((architectures & architecture) == 0 || await IsInstalledAsync(context, architecture, ct))
                continue;
            var installer = await EnsureInstallerAsync(architecture, ct);
            var result = await ProtonWindowsDesktopRuntime.RunProtonAsync(context,
                ["runinprefix", installer, "/install", "/quiet", "/norestart"], ct);
            if (result.ExitCode is not (0 or 3010) ||
                !await IsInstalledAsync(context, architecture, ct))
                throw new InvalidOperationException(
                    $"The x{(architecture == VisualCppArchitecture.X64 ? "64" : "86")} " +
                    $"Visual C++ {Version} runtime did not install in Proton prefix '{context.CompatDataPath}' " +
                    $"(installer exit code {result.ExitCode}).");
        }
    }

    public async Task<bool> IsInstalledAsync(ProtonContext context,
        VisualCppArchitecture architecture, CancellationToken ct = default)
    {
        ProtonWindowsDesktopRuntime.Validate(context);
        var key = architecture switch
        {
            VisualCppArchitecture.X64 => @"HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64",
            VisualCppArchitecture.X86 => @"HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86",
            _ => throw new ArgumentOutOfRangeException(nameof(architecture))
        };
        var result = await ProtonWindowsDesktopRuntime.RunProtonAsync(context,
            ["runinprefix", "reg", "query", key, "/v", "Version"], ct);
        var match = InstalledVersion.Match(result.Output);
        return result.ExitCode == 0 && match.Success &&
               System.Version.TryParse(match.Groups[1].Value, out var installed) &&
               installed >= new System.Version(Version);
    }

    private async Task<string> EnsureInstallerAsync(VisualCppArchitecture architecture,
        CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDirectory);
        var x64 = architecture == VisualCppArchitecture.X64;
        var path = Path.Combine(cacheDirectory, $"vc_redist.{(x64 ? "x64" : "x86")}-{Version}.exe");
        var sha512 = x64 ? X64Sha512 : X86Sha512;
        if (File.Exists(path))
        {
            await VerifyAsync(path, sha512, ct);
            return path;
        }

        var temp = path + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await httpClient.GetAsync(x64 ? X64Url : X86Url,
                HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps ||
                response.RequestMessage.RequestUri.Host != "download.visualstudio.microsoft.com" ||
                response.Content.Headers.ContentLength > MaxInstallerBytes)
                throw new InvalidDataException("Visual C++ runtime download is not safe to accept.");
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
                        throw new InvalidDataException("Visual C++ runtime installer exceeds the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, length), ct);
                }
            }
            await VerifyAsync(temp, sha512, ct);
            File.Move(temp, path, overwrite: false);
            return path;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static async Task VerifyAsync(string path, string expected, CancellationToken ct)
    {
        if (new FileInfo(path).Length > MaxInstallerBytes)
            throw new InvalidDataException("Visual C++ runtime installer exceeds the size limit.");
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA512.HashDataAsync(stream, ct));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Visual C++ runtime installer SHA-512 mismatch.");
    }
}
