using AccessibilityModManager.Core.Interfaces;

namespace AccessibilityModManager.Infrastructure.Installer;

/// <summary>
/// Thrown by <see cref="InstallerEngine"/> when an install is blocked because a required
/// dependency is missing or incompatible. The UI can catch this and surface the list to the
/// user so they can use the per-dep "Fix" buttons before retrying.
/// </summary>
public sealed class MissingRequiredDependencyException : Exception
{
    public IReadOnlyList<DependencyStatus> Blockers { get; }

    public MissingRequiredDependencyException(IReadOnlyList<DependencyStatus> blockers, string summary)
        : base($"Install blocked — required dependencies not satisfied: {summary}")
    {
        Blockers = blockers;
    }
}
