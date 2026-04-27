using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessibilityModManager.AuthorTool.ViewModels;

/// <summary>
/// One selectable tag in the AuthorTool's Filters editor. Wraps a <see cref="TagDefinition"/>
/// (or a custom author-defined tag) with an <c>IsSelected</c> bool that the checkbox binds to.
/// Custom tags can be removed; core tags can't.
/// </summary>
public sealed partial class TagSelection : ObservableObject
{
    private readonly Action _onToggle;

    public string Id { get; }
    public string Label { get; }
    public string Category { get; }
    public bool IsCustom { get; }

    [ObservableProperty]
    private bool _isSelected;

    public TagSelection(string id, string label, string category, bool isSelected, bool isCustom, Action onToggle)
    {
        Id = id;
        Label = label;
        Category = category;
        IsCustom = isCustom;
        _isSelected = isSelected;
        _onToggle = onToggle;
    }

    partial void OnIsSelectedChanged(bool value) => _onToggle();

    public override string ToString() => Label;
}

public sealed partial class LanguageSelection : ObservableObject
{
    private readonly Action _onToggle;

    public string Code { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public LanguageSelection(string code, string label, bool isSelected, Action onToggle)
    {
        Code = code;
        Label = label;
        _isSelected = isSelected;
        _onToggle = onToggle;
    }

    partial void OnIsSelectedChanged(bool value) => _onToggle();

    public override string ToString() => Label;
}
