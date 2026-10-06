namespace AccessibilityModManager.Core.Models;

public static class WineDllProxy
{
    public static string ModuleName(string nameOrRule)
    {
        var separator = nameOrRule.IndexOf('=');
        var name = separator < 0 ? nameOrRule : nameOrRule[..separator];
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public static bool SameOverride(string first, string second)
    {
        var firstParts = first.Split('=', 2);
        var secondParts = second.Split('=', 2);
        return firstParts.Length == 2 && secondParts.Length == 2 &&
               ModuleName(firstParts[0]).Equals(ModuleName(secondParts[0]),
                   StringComparison.OrdinalIgnoreCase) &&
               firstParts[1].Equals(secondParts[1], StringComparison.OrdinalIgnoreCase);
    }

    public static string PathFor(ProtonLaunchConfig launch, string rule)
    {
        var module = ModuleName(rule);
        return launch.WineDllProxyPaths
                   .FirstOrDefault(pair =>
                       ModuleName(pair.Key).Equals(module, StringComparison.OrdinalIgnoreCase)).Value
               ?? module + ".dll";
    }
}
