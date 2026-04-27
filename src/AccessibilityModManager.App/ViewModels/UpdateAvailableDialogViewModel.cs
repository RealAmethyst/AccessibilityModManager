using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessibilityModManager.App.ViewModels;

/// <summary>
/// View-model for <see cref="Views.UpdateAvailableDialog"/>. Renders the version + changelog
/// the user is about to install. The dialog itself owns the user's choice (Install / Skip /
/// View on GitHub) — this VM is just data.
/// </summary>
public sealed class UpdateAvailableDialogViewModel : ObservableObject
{
    public UpdateInfo Update { get; }
    public Version CurrentVersion { get; }

    public UpdateAvailableDialogViewModel(UpdateInfo update, Version currentVersion)
    {
        Update = update;
        CurrentVersion = currentVersion;
    }

    public string Headline => $"Version {Update.Version} is available";
    public string Subheadline => $"You're on {CurrentVersion}. Install now to get the latest fixes and features.";
    public string ChangelogText =>
        string.IsNullOrWhiteSpace(Update.ReleaseNotes)
            ? "(No release notes were published with this version.)"
            : Update.ReleaseNotes!;
}
