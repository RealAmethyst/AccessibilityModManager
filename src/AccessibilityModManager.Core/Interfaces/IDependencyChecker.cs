using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public enum DependencyStatusKind
{
    Installed,
    Missing,
    Incompatible
}

public sealed class DependencyStatus
{
    public required Dependency Dependency { get; init; }
    public required DependencyStatusKind Status { get; init; }
    public string? Details { get; init; }
}

public interface IDependencyChecker
{
    Task<List<DependencyStatus>> CheckAsync(GameInstall game, CancellationToken ct = default);

    /// <summary>
    /// Opens the dependency's manual download page. Returns false when nothing could be opened
    /// (no URL configured, or the URL isn't a safe https address) so the UI can tell the user
    /// instead of failing silently.
    /// </summary>
    Task<bool> FixAsync(Dependency dep, CancellationToken ct = default);
}
