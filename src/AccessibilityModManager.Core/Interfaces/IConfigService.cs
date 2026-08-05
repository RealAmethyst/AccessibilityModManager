using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Core.Interfaces;

public interface IConfigService
{
    Task<AppConfig> LoadAsync();
    Task SaveAsync(AppConfig config);

    /// <summary>
    /// Applies one change to the settings under a cross-process lock: re-reads the file, runs
    /// <paramref name="change"/> on it, and saves — so nothing that happened in between is lost.
    ///
    /// <para>Load-modify-save is the ordinary pattern in this app, and it is a read-modify-write
    /// race the moment two of them overlap. Adding a source takes a noticeable while (the catalog
    /// is fetched and the user reads a notice), and an ordinary settings save landing in that window
    /// would be written from a snapshot taken before the source existed — silently discarding it, or
    /// discarding the filter change, depending on which finished last. Two copies of the manager can
    /// do the same thing to each other, which in-process locking cannot see.</para>
    ///
    /// <para>Returns the saved configuration.</para>
    /// </summary>
    Task<AppConfig> UpdateAsync(Action<AppConfig> change);

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
