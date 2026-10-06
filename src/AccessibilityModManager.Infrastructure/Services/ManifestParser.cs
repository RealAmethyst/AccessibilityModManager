using System.Text.Json;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Strict manifest parser. Rejects unknown action types — no arbitrary code execution.
/// </summary>
public sealed class ManifestParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly HashSet<string> AllowedActionTypes = ["copyFile", "copyFolder", "replaceFile"];
    private static readonly HashSet<string> AllowedVerifyTypes = ["fileExists", "folderExists", "hashEquals"];

    private readonly ILogger _logger;

    public ManifestParser(ILogger logger)
    {
        _logger = logger;
    }

    public Manifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Manifest deserialized to null");

        if (string.IsNullOrWhiteSpace(manifest.GameId))
            throw new InvalidOperationException("Manifest missing required field: gameId");

        if (string.IsNullOrWhiteSpace(manifest.PluginId))
            throw new InvalidOperationException("Manifest missing required field: pluginId");

        if (string.IsNullOrWhiteSpace(manifest.ModVersion))
            throw new InvalidOperationException("Manifest missing required field: modVersion");

        ReleaseTarget.Normalize(manifest.TargetPlatform);
        ValidateProtonLaunch(manifest);

        if (ReleaseTarget.Normalize(manifest.TargetPlatform) == ReleaseTarget.Proton &&
            (manifest.PreInstall is not null || manifest.PostInstall is not null ||
             manifest.PostUninstall is not null))
            throw new InvalidOperationException(
                "Proton packages cannot run Windows lifecycle scripts through the Linux host. " +
                "Bundle loader files in the package and declare runtime setup in protonLaunch.");

        ValidateActions(manifest);
        ValidateVerifyRules(manifest);
        foreach (var dependency in manifest.Dependencies)
            DependencyTargeting.Validate(dependency);

        _logger.Information("Parsed manifest: {PluginId}/{GameId} v{Version}",
            manifest.PluginId, manifest.GameId, manifest.ModVersion);

        return manifest;
    }

    private static void ValidateProtonLaunch(Manifest manifest)
    {
        if (manifest.ProtonLaunch is null)
        {
            if (ReleaseTarget.Normalize(manifest.TargetPlatform) == ReleaseTarget.Proton)
                throw new InvalidOperationException("A Steam Proton package needs protonLaunch metadata.");
            return;
        }
        if (ReleaseTarget.Normalize(manifest.TargetPlatform) != ReleaseTarget.Proton)
            throw new InvalidOperationException("protonLaunch requires targetPlatform 'proton'.");

        if (string.IsNullOrWhiteSpace(manifest.ProtonLaunch.SteamAppId) ||
            !manifest.ProtonLaunch.SteamAppId.All(char.IsAsciiDigit))
            throw new InvalidOperationException("protonLaunch.steamAppId must contain only digits.");
        if (string.IsNullOrWhiteSpace(manifest.ProtonLaunch.GameDisplayName))
            throw new InvalidOperationException("protonLaunch.gameDisplayName is required.");
        RequirePortableRelativePath(manifest.ProtonLaunch.GameExecutable, "protonLaunch.gameExecutable");
        var launch = manifest.ProtonLaunch;
        if (launch.LaunchMode is not ("direct" or "replaceExecutable"))
            throw new InvalidOperationException("protonLaunch.launchMode must be 'direct' or 'replaceExecutable'.");
        if (launch.LaunchMode == "replaceExecutable")
        {
            RequirePortableRelativePath(launch.LauncherPath!, "protonLaunch.launcherPath");
            if (!launch.LauncherPath!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("protonLaunch.launcherPath must name a Windows executable.");
        }
        else if (launch.LauncherPath is not null)
            throw new InvalidOperationException("protonLaunch.launcherPath is only used with replaceExecutable.");
        if (launch.BridgeDirectory is not null)
            RequirePortableRelativePath(launch.BridgeDirectory, "protonLaunch.bridgeDirectory");
        if (launch.WineDllOverrides is null)
            throw new InvalidOperationException("protonLaunch.wineDllOverrides must be a list.");
        if (launch.WineDllOverrides.Count > 16)
            throw new InvalidOperationException("protonLaunch.wineDllOverrides exceeds the 16-DLL limit.");
        var overrideNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in launch.WineDllOverrides)
        {
            var parts = rule?.Split('=', 2);
            var canonical = parts is { Length: 2 } ? WineDllProxy.ModuleName(parts[0]) : null;
            if (parts is not { Length: 2 } || parts[0].Length == 0 ||
                parts[0].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) ||
                parts[1] is not ("n" or "b" or "n,b" or "b,n") ||
                canonical is null || canonical.Length is < 1 or > 64 ||
                !overrideNames.Add(canonical))
                throw new InvalidOperationException("protonLaunch.wineDllOverrides contains an invalid or repeated Wine DLL rule.");
        }
        if (launch.WineDllProxyPaths is null)
            throw new InvalidOperationException("protonLaunch.wineDllProxyPaths must be an object.");
        var proxyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path) in launch.WineDllProxyPaths)
        {
            var canonical = WineDllProxy.ModuleName(name);
            if (!overrideNames.Contains(canonical) || !proxyNames.Add(canonical))
                throw new InvalidOperationException("protonLaunch.wineDllProxyPaths has an unknown or repeated DLL name.");
            RequirePortableRelativePath(path, "protonLaunch.wineDllProxyPaths");
            if (!Path.GetFileName(path).Equals(canonical + ".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A Wine DLL proxy path must end with its override DLL name.");
        }
        if ((manifest.ProtonLaunch.ReloadedRoot is null) !=
            (manifest.ProtonLaunch.ReloadedModId is null))
            throw new InvalidOperationException("protonLaunch needs both reloadedRoot and reloadedModId together.");
        if (manifest.ProtonLaunch.ReloadedRoot is { } reloadedRoot)
        {
            RequirePortableRelativePath(reloadedRoot, "protonLaunch.reloadedRoot");
            var modId = manifest.ProtonLaunch.ReloadedModId!;
            if (string.IsNullOrWhiteSpace(modId) ||
                modId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
                throw new InvalidOperationException("protonLaunch.reloadedModId is invalid.");
        }
        var runtime = manifest.ProtonLaunch.WindowsDesktopRuntimeVersion;
        if (runtime is not null &&
            (!System.Version.TryParse(runtime, out var version) || version.Build < 0 ||
             version.Revision >= 0 || runtime != version.ToString(3)))
            throw new InvalidOperationException("protonLaunch.windowsDesktopRuntimeVersion must be a major.minor.patch version.");
        var runtimeHash = launch.WindowsDesktopRuntimeSha512;
        if (runtimeHash is not null &&
            (runtimeHash.Length != 128 || !runtimeHash.All(Uri.IsHexDigit)))
            throw new InvalidOperationException("protonLaunch.windowsDesktopRuntimeSha512 must be a 128-character SHA-512 hex digest.");
        if (runtime is null && runtimeHash is not null)
            throw new InvalidOperationException("protonLaunch.windowsDesktopRuntimeSha512 requires a runtime version.");
        if (runtime is not null && runtime != ProtonWindowsDesktopRuntime.Version && runtimeHash is null)
            throw new InvalidOperationException("A non-legacy Windows Desktop Runtime needs its official installer SHA-512.");
    }

    private static void RequirePortableRelativePath(string path, string field)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') ||
            path.Contains('\\') || path.Contains(':') || path.Contains('\0') ||
            path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidOperationException($"{field} must be a safe game-relative path using forward slashes.");
    }

    public Manifest ParseFile(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return Parse(json);
    }

    private void ValidateActions(Manifest manifest)
    {
        foreach (var action in manifest.InstallActions)
        {
            var typeName = action switch
            {
                CopyFileAction => "copyFile",
                CopyFolderAction => "copyFolder",
                ReplaceFileAction => "replaceFile",
                _ => "unknown"
            };

            if (!AllowedActionTypes.Contains(typeName))
            {
                _logger.Error("Manifest contains disallowed action type: {Type}", typeName);
                throw new InvalidOperationException(
                    $"Manifest contains disallowed install action type: '{typeName}'. Only {string.Join(", ", AllowedActionTypes)} are allowed.");
            }
        }
    }

    private void ValidateVerifyRules(Manifest manifest)
    {
        foreach (var rule in manifest.Verify)
        {
            if (!AllowedVerifyTypes.Contains(rule.Type))
            {
                _logger.Error("Manifest contains disallowed verify type: {Type}", rule.Type);
                throw new InvalidOperationException(
                    $"Manifest contains disallowed verify rule type: '{rule.Type}'. Only {string.Join(", ", AllowedVerifyTypes)} are allowed.");
            }
        }
    }
}
