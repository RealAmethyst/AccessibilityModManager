using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IConfigService
{
    Task<AppConfig> LoadAsync();
    Task SaveAsync(AppConfig config);

    /// <summary>
    /// Human-readable description of a recovery a load performed (config was corrupt and the
    /// backup was used, or everything was unreadable and defaults were applied). Sticky: stays
    /// set across later clean loads until <see cref="AcknowledgeLoadProblem"/> — the app shows it
    /// once at startup so a silent settings reset never goes unnoticed.
    /// </summary>
    string? LastLoadProblem { get; }

    /// <summary>Clears <see cref="LastLoadProblem"/> after it has been shown to the user.</summary>
    void AcknowledgeLoadProblem();
}
