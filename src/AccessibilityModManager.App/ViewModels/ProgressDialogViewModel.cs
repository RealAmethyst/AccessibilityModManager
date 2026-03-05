using AccessibilityModManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccessibilityModManager.App.ViewModels;

public partial class ProgressDialogViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private string _stepDescription = string.Empty;

    [ObservableProperty]
    private bool _isCancellable = true;

    public void Start(string title, string message, CancellationTokenSource cts)
    {
        Title = title;
        Message = message;
        Percentage = 0;
        StepDescription = string.Empty;
        _cts = cts;
        IsCancellable = true;
    }

    public void OnProgress(ProgressInfo info)
    {
        Percentage = info.Percentage;
        Message = info.StatusText;
        StepDescription = info.StepDescription ?? string.Empty;
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        IsCancellable = false;
        Message = "Cancelling...";
    }
}
