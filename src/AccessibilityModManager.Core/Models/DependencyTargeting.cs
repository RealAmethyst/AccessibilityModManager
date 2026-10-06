namespace AccessibilityModManager.Core.Models;

public static class DependencyTargeting
{
    public static bool AppliesTo(Dependency dependency, string? targetPlatform)
    {
        Validate(dependency);
        var target = ReleaseTarget.Normalize(targetPlatform);
        if (dependency.TargetPlatforms is null)
            return target is ReleaseTarget.Windows or ReleaseTarget.Proton;
        return dependency.TargetPlatforms.Contains(target, StringComparer.Ordinal);
    }

    public static List<Dependency> ForTarget(IEnumerable<Dependency> dependencies, string? targetPlatform) =>
        dependencies.Where(dependency => AppliesTo(dependency, targetPlatform)).ToList();

    public static void Validate(Dependency dependency)
    {
        if (dependency.TargetPlatforms is null) return;
        if (dependency.TargetPlatforms.Count == 0)
            throw new InvalidOperationException(
                $"Dependency '{dependency.Id}' has no target platforms. Select at least one or use automatic compatibility.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in dependency.TargetPlatforms)
        {
            if (target is null || !seen.Add(ReleaseTarget.Normalize(target)))
                throw new InvalidOperationException(
                    $"Dependency '{dependency.Id}' has an invalid or repeated target platform.");
        }
    }
}
