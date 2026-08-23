namespace AccessibilityModManager.AuthorTool;

/// <summary>
/// Compile-time toggles shared with the headless authoring workflows. The signing UI is hidden in
/// normal user builds.
/// </summary>
internal static class BuildFlags
{
    public const bool IsRegistryAdmin =
        AccessibilityModManager.Authoring.Workflows.AuthoringBuildFlags.IsRegistryAdmin;
}
