namespace AccessibilityModManager.Core.Models;

/// <summary>One signed release can carry distinct Windows and Linux payloads.</summary>
public static class ReleasePackages
{
    public static void Validate(ModRelease release)
    {
        if (release.LinuxPackage is not { } linux) return;
        if (ReleaseTarget.Normalize(release.TargetPlatform) is not (ReleaseTarget.Windows or ReleaseTarget.WindowsLinux))
            throw new InvalidOperationException("A separate Linux package needs a Windows or shared primary package.");
        if (linux.TargetPlatform is not (ReleaseTarget.Linux or ReleaseTarget.Proton or ReleaseTarget.XivLauncher))
            throw new InvalidOperationException("Choose native Linux, Proton, or XIVLauncher for the separate Linux package.");
        if (linux.PackageUrl is null || !linux.PackageUrl.IsAbsoluteUri || linux.PackageUrl.Scheme != "https")
            throw new InvalidOperationException("The Linux package needs an absolute HTTPS download URL.");
        if (linux.Sha256 is not { Length: 64 } || !linux.Sha256.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("The Linux package needs its 64-character SHA-256 hash.");
    }

    public static ModRelease ForPlatform(ModRelease release, bool linux)
    {
        if (!linux || release.LinuxPackage is not { } package) return release;
        Validate(release);
        return new ModRelease
        {
            GameId = release.GameId, PluginId = release.PluginId, Version = release.Version,
            Channel = release.Channel, Notes = release.Notes, ChangelogUrl = release.ChangelogUrl,
            Compatibility = release.Compatibility, TargetPlatform = package.TargetPlatform,
            Sha256 = package.Sha256, PackageUrl = release.Patreon is null ? package.PackageUrl : null,
            Patreon = release.Patreon is { } gate ? new PatreonGate
            {
                CampaignId = gate.CampaignId, TierIds = gate.TierIds.ToList(),
                ServerUrl = package.PackageUrl.AbsoluteUri
            } : null
        };
    }

    /// <summary>Enumerate physical packages for hashing, publishing and backup, retaining their shared access.</summary>
    public static IEnumerable<ModRelease> All(ModRelease release)
    {
        yield return release;
        if (release.LinuxPackage is not null) yield return ForPlatform(release, linux: true);
    }
}
