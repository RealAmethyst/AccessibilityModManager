using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AccessibilityModManager.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessibilityModManager.App.ViewModels;

/// <summary>
/// View-model for the deps-consent dialog, step 1 of the F16=C two-step combined consent.
/// Shows the user every dependency that will be auto-downloaded before any download starts,
/// with the URL, action kind, and a NeedsAdmin badge for runInstaller-with-admin items.
/// </summary>
public sealed class DependencyWarningDialogViewModel : ObservableObject
{
    public string Headline { get; }
    public string Subheading { get; }
    public IReadOnlyList<DependencyPromptItemViewModel> Items { get; }

    public DependencyWarningDialogViewModel(DependencyInstallPrompt prompt)
    {
        Headline = $"{prompt.ModName} needs additional components";
        Subheading = "These will be downloaded and installed before the mod itself. The manager " +
                     "verifies each download with SHA256 and only proceeds if it matches.";
        Items = prompt.Items.Select(i => new DependencyPromptItemViewModel(i)).ToList();
    }
}

public sealed class DependencyPromptItemViewModel
{
    private readonly DependencyInstallPromptItem _item;

    public DependencyPromptItemViewModel(DependencyInstallPromptItem item)
    {
        _item = item;
    }

    public string Heading => _item.Dependency.Id;
    public string KindLine => $"Action: {_item.KindLabel}";
    public string DownloadLine => $"Download: {_item.DownloadUrl}";

    public bool NeedsAdmin => _item.NeedsAdmin;
    public Visibility NeedsAdminVisibility => NeedsAdmin ? Visibility.Visible : Visibility.Collapsed;

    public string AnnouncementText
    {
        get
        {
            var admin = NeedsAdmin ? " (needs administrator)" : "";
            return $"{Heading}{admin}. {KindLine}. {DownloadLine}";
        }
    }

    public override string ToString() => AnnouncementText;
}
