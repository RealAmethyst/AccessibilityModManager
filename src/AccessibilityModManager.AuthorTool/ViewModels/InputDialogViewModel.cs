using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessibilityModManager.AuthorTool.ViewModels;

public sealed partial class InputDialogViewModel : ObservableObject
{
    public string Title { get; }
    public string Prompt { get; }

    [ObservableProperty]
    private string _value;

    public bool Confirmed { get; private set; }
    public Action? CloseDialog { get; set; }

    public InputDialogViewModel(string title, string prompt, string? defaultValue)
    {
        Title = title;
        Prompt = prompt;
        _value = defaultValue ?? "";
    }

    public void Confirm()
    {
        Confirmed = true;
        CloseDialog?.Invoke();
    }

    public void Cancel()
    {
        Confirmed = false;
        CloseDialog?.Invoke();
    }
}
