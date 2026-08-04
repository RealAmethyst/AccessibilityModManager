using System.ComponentModel;
using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.Core.Models;
using Xunit;

namespace AccessibilityModManager.Tests.ViewModels;

/// <summary>
/// The progress dialog has exactly ONE spoken line, and it speaks per PHASE, not per event.
///
/// <para>This is the contract that stops an install talking over itself: downloads report after
/// every 81,920-byte read, and the script output region replaces its whole contents with the
/// accumulated transcript on every line. Marking those as live regions would have NVDA re-reading
/// the transcript continuously. StepDescription is the one live binding, and these tests pin that
/// only real phase transitions move it.</para>
/// </summary>
public class ProgressAnnouncementTests
{
    private static (ProgressDialogViewModel Vm, Func<int> StepChanges) Track()
    {
        var vm = new ProgressDialogViewModel();
        var count = 0;
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProgressDialogViewModel.StepDescription)) count++;
        };
        return (vm, () => count);
    }

    private static ProgressInfo Downloading(double pct, long kb) => new()
    {
        Percentage = pct,
        StatusText = $"Downloading... {kb:N0} / 5,000 KB",
        StepDescription = "Downloading package"
    };

    [Fact]
    public void AWholeDownloadAnnouncesItsPhaseOnce()
    {
        var (vm, stepChanges) = Track();

        for (var i = 1; i <= 60; i++)
            vm.OnProgress(Downloading(i / 60d * 100, i * 80));

        // Sixty progress reports, one phase.
        Assert.Equal(1, stepChanges());
        Assert.Equal("Downloading package", vm.StepDescription);
        // The byte counter still updates for the eye — it is simply not the live line.
        Assert.Contains("4,800 / 5,000 KB", vm.Message);
    }

    [Fact]
    public void MovingToANewPhaseAnnouncesAgain()
    {
        var (vm, stepChanges) = Track();

        vm.OnProgress(Downloading(50, 2500));
        vm.OnProgress(new ProgressInfo
        {
            Percentage = 60,
            StatusText = "Extracting files",
            StepDescription = "Extracting package"
        });

        Assert.Equal(2, stepChanges());
        Assert.Equal("Extracting package", vm.StepDescription);
    }

    [Fact]
    public void StartingAScriptIsAPhaseAndIsAnnounced()
    {
        var (vm, stepChanges) = Track();

        vm.OnProgress(Downloading(100, 5000));
        vm.OnScriptStarting("Pre-install", "setup.ps1");

        Assert.Equal(2, stepChanges());
        Assert.Equal("Running pre-install script: setup.ps1", vm.StepDescription);
    }

    [Fact]
    public void ScriptOutputNeverAnnounces()
    {
        var (vm, stepChanges) = Track();
        vm.OnScriptStarting("Pre-install", "setup.ps1");
        var afterStart = stepChanges();

        for (var i = 0; i < 40; i++)
            vm.OnScriptOutputLine($"line {i}");

        // The transcript grows and stays readable, but it is not the announcement stream.
        Assert.Equal(afterStart, stepChanges());
        Assert.Contains("line 39", vm.ScriptOutput);
    }

    [Fact]
    public void FinishingAScriptIsAnnouncedOnce()
    {
        var (vm, stepChanges) = Track();
        vm.OnScriptStarting("Post-install", "after.ps1");
        var afterStart = stepChanges();

        vm.OnScriptFinished(exitCode: 0, succeeded: true);

        Assert.Equal(afterStart + 1, stepChanges());
        Assert.Contains("completed", vm.StepDescription);
    }

    [Fact]
    public void AFailedScriptSaysSoRatherThanJustStopping()
    {
        var (vm, _) = Track();
        vm.OnScriptStarting("Pre-install", "setup.ps1");

        vm.OnScriptFinished(exitCode: 3, succeeded: false);

        Assert.Contains("failed", vm.StepDescription);
    }

    [Fact]
    public void CancellingIsAPhaseSoItIsHeard()
    {
        var (vm, _) = Track();
        using var cts = new CancellationTokenSource();
        vm.Start("Installing", "Starting", cts);

        vm.CancelCommand.Execute(null);

        Assert.Equal("Cancelling", vm.StepDescription);
    }
}
