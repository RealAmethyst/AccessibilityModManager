namespace AccessibilityModManager.AuthorTool;

/// <summary>
/// Compile-time toggles. <see cref="IsRegistryAdmin"/> is true when the build was produced
/// with <c>-p:DefineConstants=REGISTRY_ADMIN</c> (or via the build script's <c>-Admin</c>
/// switch). The signing UI is hidden in normal user builds.
/// </summary>
internal static class BuildFlags
{
#if REGISTRY_ADMIN
    public const bool IsRegistryAdmin = true;
#else
    public const bool IsRegistryAdmin = false;
#endif
}
