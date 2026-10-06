using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Infrastructure.Installer;

/// <summary>Checks whether a declared game dependency can be verified and applied on Linux.</summary>
public static class LinuxDependencySupport
{
    public static void EnsureSupported(GameDefinition game, string targetPlatform = ReleaseTarget.Proton)
    {
        if (!OperatingSystem.IsLinux()) return;

        foreach (var dependency in game.Dependencies.Where(d => !d.IsGameInstaller &&
                     DependencyTargeting.AppliesTo(d, targetPlatform)))
        {
            if (dependency.Type != "framework" ||
                string.IsNullOrWhiteSpace(dependency.Check?.FilePath) ||
                !string.IsNullOrWhiteSpace(dependency.Check.RegistryKey))
            {
                throw new PlatformNotSupportedException(
                    $"Dependency '{dependency.Id}' needs a game-relative file check for Linux. " +
                    "Windows registry and system dependency checks cannot verify a Proton install.");
            }

            if (dependency.Fix?.AutoInstall is RunInstallerAutoInstall)
            {
                throw new PlatformNotSupportedException(
                    $"Dependency '{dependency.Id}' uses a Windows installer. " +
                    "Publish it as a bundled file, extractZip, or copyFile dependency for Linux.");
            }

            if (dependency.Fix?.AutoInstall is ExtractAppAutoInstall)
            {
                throw new PlatformNotSupportedException(
                    $"Dependency '{dependency.Id}' uses extractApp, which is only for game installers.");
            }
        }
    }
}
