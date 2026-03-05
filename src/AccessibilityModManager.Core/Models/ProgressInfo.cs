namespace AccessibilityModManager.Core.Models;

/// <summary>
/// Reported by long-running operations for UI progress display.
/// </summary>
public sealed class ProgressInfo
{
    public required double Percentage { get; init; }
    public required string StatusText { get; init; }
    public string? StepDescription { get; init; }
}
