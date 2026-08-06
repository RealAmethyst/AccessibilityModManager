using System.ComponentModel;
using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Tests.ViewModels;

public class DependencyWarningDialogViewModelTests
{
    [Fact]
    public void RequiredAndOptionalItems_ExposeAccessibleSelectionSemantics()
    {
        var vm = new DependencyWarningDialogViewModel(new DependencyInstallPrompt
        {
            ModName = "Blind Soldier",
            Version = "",
            Items = new[]
            {
                Item("required-runtime", isRequired: true),
                Item("seventh-heaven", isRequired: false)
            }
        });

        var required = vm.Items[0];
        Assert.True(required.IsRequired);
        Assert.True(required.IsSelected);
        Assert.False(required.CanChangeSelection);
        Assert.Contains("required", required.AnnouncementText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("selected", required.AnnouncementText, StringComparison.OrdinalIgnoreCase);

        required.IsSelected = false;
        Assert.True(required.IsSelected);

        var optional = vm.Items[1];
        Assert.False(optional.IsRequired);
        Assert.False(optional.IsSelected);
        Assert.True(optional.CanChangeSelection);
        Assert.Contains("optional", optional.AnnouncementText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not selected", optional.AnnouncementText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vm.SelectedOptionalDependencyIds);
    }

    [Fact]
    public void SelectingOptionalItem_UpdatesAnnouncementAndDecisionIds()
    {
        var vm = new DependencyWarningDialogViewModel(new DependencyInstallPrompt
        {
            ModName = "Blind Soldier",
            Version = "",
            Items = new[] { Item("seventh-heaven", isRequired: false) }
        });
        var item = Assert.Single(vm.Items);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.IsSelected = true;

        Assert.Contains(nameof(item.IsSelected), changed);
        Assert.Contains(nameof(item.AnnouncementText), changed);
        Assert.Contains("selected", item.AnnouncementText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not selected", item.AnnouncementText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "seventh-heaven" }, vm.SelectedOptionalDependencyIds);
    }

    private static DependencyInstallPromptItem Item(string id, bool isRequired) => new()
    {
        Dependency = new Dependency
        {
            Id = id,
            Type = "framework",
            Required = isRequired
        },
        IsRequired = isRequired,
        KindLabel = "Run installer",
        DownloadUrl = $"https://example.invalid/{id}.exe",
        NeedsAdmin = false
    };
}
