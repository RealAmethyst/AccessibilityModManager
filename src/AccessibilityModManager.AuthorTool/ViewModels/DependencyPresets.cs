using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed class DependencyPreset
{
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required Func<Dependency> Build { get; init; }

    public Dependency ToModel() => Build();
    public override string ToString() => DisplayName;
}

public static class DependencyPresetsBag
{
    public static IReadOnlyList<DependencyPreset> Presets => DependencyPresets.All;
}

public static class DependencyPresets
{
    public static IReadOnlyList<DependencyPreset> All { get; } =
        DependencyPresetCatalog.All
            .Select(preset => new DependencyPreset
            {
                DisplayName = preset.DisplayName,
                Description = preset.Description,
                Build = preset.ToDependency
            })
            .ToArray();
}
