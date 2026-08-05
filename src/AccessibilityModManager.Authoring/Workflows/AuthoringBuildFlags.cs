namespace AccessibilityModManager.Authoring.Workflows;

/// <summary>
/// Compile-time authoring capabilities shared by the WPF and command-line front ends.
/// Registry administration is deliberately absent from ordinary builds even though its commands
/// remain discoverable.
/// </summary>
public static class AuthoringBuildFlags
{
#if REGISTRY_ADMIN
    public const bool IsRegistryAdmin = true;
#else
    public const bool IsRegistryAdmin = false;
#endif
}
