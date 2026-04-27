using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed partial class AuthorInfoDialogViewModel : ObservableObject
{
    public string PluginId { get; }

    [ObservableProperty]
    private string? _displayName;

    [ObservableProperty]
    private string? _bio;

    [ObservableProperty]
    private string? _websiteUrl;

    [ObservableProperty]
    private string? _discordUrl;

    [ObservableProperty]
    private string? _patreonUrl;

    [ObservableProperty]
    private string? _gitHubUrl;

    [ObservableProperty]
    private string? _donationUrl;

    public bool Confirmed { get; private set; }
    public Action? CloseDialog { get; set; }

    public AuthorInfoDialogViewModel(string pluginId, PluginAuthorInfo? existing)
    {
        PluginId = pluginId;
        if (existing != null)
        {
            _displayName = existing.DisplayName;
            _bio = existing.Bio;
            _websiteUrl = existing.WebsiteUrl;
            _discordUrl = existing.DiscordUrl;
            _patreonUrl = existing.PatreonUrl;
            _gitHubUrl = existing.GitHubUrl;
            _donationUrl = existing.DonationUrl;
        }
    }

    [RelayCommand]
    private void Save()
    {
        Confirmed = true;
        CloseDialog?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        CloseDialog?.Invoke();
    }

    public PluginAuthorInfo ToModel() => new()
    {
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName!.Trim(),
        Bio = string.IsNullOrWhiteSpace(Bio) ? null : Bio!.Trim(),
        WebsiteUrl = string.IsNullOrWhiteSpace(WebsiteUrl) ? null : WebsiteUrl!.Trim(),
        DiscordUrl = string.IsNullOrWhiteSpace(DiscordUrl) ? null : DiscordUrl!.Trim(),
        PatreonUrl = string.IsNullOrWhiteSpace(PatreonUrl) ? null : PatreonUrl!.Trim(),
        GitHubUrl = string.IsNullOrWhiteSpace(GitHubUrl) ? null : GitHubUrl!.Trim(),
        DonationUrl = string.IsNullOrWhiteSpace(DonationUrl) ? null : DonationUrl!.Trim()
    };
}
