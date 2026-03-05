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
    Task FixAsync(Dependency dep, CancellationToken ct = default);
}
