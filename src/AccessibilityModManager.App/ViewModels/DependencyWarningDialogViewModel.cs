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
        var hasRequired = prompt.Items.Any(i => i.IsRequired);
        Headline = hasRequired
            ? $"{prompt.ModName} needs additional components"
            : $"Optional components for {prompt.ModName}";
        Subheading = "Required components are selected and cannot be cleared. Optional components " +
                     "start unchecked. The manager verifies each selected download with SHA256.";
        Items = prompt.Items.Select(i => new DependencyPromptItemViewModel(i)).ToList();
    }

    public IReadOnlyList<string> SelectedOptionalDependencyIds => Items
        .Where(i => !i.IsRequired && i.IsSelected)
        .Select(i => i.DependencyId)
        .ToList();
}

public sealed class DependencyPromptItemViewModel : ObservableObject
{
    private readonly DependencyInstallPromptItem _item;
    private bool _isSelected;

    public DependencyPromptItemViewModel(DependencyInstallPromptItem item)
    {
        _item = item;
        _isSelected = item.IsRequired;
    }

    public string DependencyId => _item.Dependency.Id;
    public string Heading => _item.Dependency.Id;
    public string KindLine => $"Action: {_item.KindLabel}";
    public string DownloadLine => $"Download: {_item.DownloadUrl}";

    public bool IsRequired => _item.IsRequired;
    public bool CanChangeSelection => !IsRequired;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsRequired && !value)
            {
                // Required items are enforced here as well as in the engine. Refreshing the value
                // makes a two-way binding snap back if a nonstandard control attempts to clear it.
                OnPropertyChanged(nameof(IsSelected));
                return;
            }

            if (!SetProperty(ref _isSelected, value)) return;
            OnPropertyChanged(nameof(AnnouncementText));
        }
    }

    public bool NeedsAdmin => _item.NeedsAdmin;
    public Visibility NeedsAdminVisibility => NeedsAdmin ? Visibility.Visible : Visibility.Collapsed;

    public string AnnouncementText
    {
        get
        {
            var admin = NeedsAdmin ? " (needs administrator)" : "";
            var requirement = IsRequired ? "required" : "optional";
            var selection = IsSelected ? "selected" : "not selected";
            return $"{Heading}{admin}, {requirement}, {selection}. {KindLine}. {DownloadLine}";
        }
    }

    public override string ToString() => AnnouncementText;
}
