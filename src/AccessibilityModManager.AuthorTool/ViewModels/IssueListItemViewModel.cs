using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessibilityModManager.AuthorTool.ViewModels;

/// <summary>
/// One row in the registry admin's issue list. Wraps the underlying <see cref="GitHubIssue"/>
/// with parse + validation state so the admin can see at a glance whether to accept.
/// </summary>
public sealed partial class IssueListItemViewModel : ObservableObject
{
    public GitHubIssue Issue { get; }
    public PluginEntry? ParsedEntry { get; }
    public bool IsParseable => ParsedEntry != null;

    [ObservableProperty]
    private bool _isValidating;

    [ObservableProperty]
    private IndexValidationResult? _validation;

    public string DisplayTitle => $"#{Issue.Number} {Issue.Title}";
    public string DisplaySubtitle => $"by {Issue.Author}";

    public string ParseStatusText => IsParseable
        ? $"Has registry-entry block — id={ParsedEntry!.Id}"
        : "Manual review needed (no parseable registry-entry block)";

    public string? ValidationStatusText
    {
        get
        {
            if (!IsParseable) return null;
            if (IsValidating) return "Validating index.json...";
            if (Validation is null) return "Validation pending";
            if (Validation.Ok)
                return $"index.json OK: {Validation.GameCount} game(s), {Validation.ReleaseCount} release(s)";
            return $"FAIL: {Validation.Error}";
        }
    }

    public bool CanAccept => IsParseable && Validation is { Ok: true };

    public IssueListItemViewModel(GitHubIssue issue)
    {
        Issue = issue;
        ParsedEntry = RegistryMembershipChecker.TryExtractEntryFromIssueBody(issue.Body);
    }

    partial void OnIsValidatingChanged(bool value)
    {
        OnPropertyChanged(nameof(ValidationStatusText));
        OnPropertyChanged(nameof(CanAccept));
    }

    partial void OnValidationChanged(IndexValidationResult? value)
    {
        OnPropertyChanged(nameof(ValidationStatusText));
        OnPropertyChanged(nameof(CanAccept));
    }

    public override string ToString() => DisplayTitle;
}
