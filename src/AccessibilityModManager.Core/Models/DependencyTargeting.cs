namespace AccessibilityModManager.Core.Models;

public static class DependencyTargeting
{
    public static bool AppliesTo(Dependency dependency, string? targetPlatform)
    {
        Validate(dependency);
        var target = ReleaseTarget.ForRuntime(targetPlatform);
        // Old readers see a known Linux-only dependency and ignore it on Windows. The new
        // host-tool metadata narrows it to the dedicated XIVLauncher installation workflow.
        if (dependency.Fix?.Xlm is not null) return target == ReleaseTarget.XivLauncher;
        if (dependency.TargetPlatforms is null)
            return target is ReleaseTarget.Windows or ReleaseTarget.Proton;
        return dependency.TargetPlatforms.Contains(target, StringComparer.Ordinal);
    }

    public static List<Dependency> ForTarget(IEnumerable<Dependency> dependencies, string? targetPlatform) =>
        dependencies.Where(dependency => AppliesTo(dependency, targetPlatform)).ToList();

    public static List<Dependency> ForPackage(IEnumerable<Dependency> dependencies, string? targetPlatform) =>
        ReleaseTarget.Normalize(targetPlatform) == ReleaseTarget.WindowsLinux
            ? dependencies.Where(dependency => AppliesTo(dependency, ReleaseTarget.Windows) ||
                AppliesTo(dependency, ReleaseTarget.Linux)).ToList()
            : ForTarget(dependencies, targetPlatform);

    public static void Validate(Dependency dependency)
    {
        if (dependency.Fix?.Xlm is not null &&
            (dependency.TargetPlatforms is not { Count: 1 } || dependency.TargetPlatforms[0] != ReleaseTarget.Linux ||
             dependency.IsGameInstaller || !dependency.Required || dependency.Fix.AutoInstall is not null))
            throw new InvalidOperationException("XLM must be a required XIVLauncher-on-Linux dependency, not a game installer.");
        if (dependency.TargetPlatforms is null) return;
        if (dependency.TargetPlatforms.Count == 0)
            throw new InvalidOperationException(
                $"Dependency '{dependency.Id}' has no target platforms. Select at least one or use automatic compatibility.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in dependency.TargetPlatforms)
        {
            if (target is not (ReleaseTarget.Windows or ReleaseTarget.Proton or ReleaseTarget.Linux or ReleaseTarget.XivLauncher) || !seen.Add(target))
                throw new InvalidOperationException(
                    $"Dependency '{dependency.Id}' has an invalid or repeated target platform.");
        }
    }
}
