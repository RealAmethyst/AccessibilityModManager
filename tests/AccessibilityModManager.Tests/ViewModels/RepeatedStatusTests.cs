using System.ComponentModel;
using AccessibilityModManager.App.ViewModels;
using AccessibilityModManager.Core.Interfaces;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Patreon;
using AccessibilityModManager.Tests.Helpers;
using Xunit;

namespace AccessibilityModManager.Tests.ViewModels;

/// <summary>
/// Doing the same thing twice has to be heard twice.
///
/// <para>Status lines are announced by their text CHANGING, and an observable property drops an
/// assignment equal to what is already there. So a second successful Save would set
/// "Settings saved." over "Settings saved." and raise nothing — the user presses a button and gets
/// silence, which reads as the button being broken. The status is blanked before each action so the
/// result is always a real change.</para>
/// </summary>
public class RepeatedStatusTests
{
    [Fact]
    public async Task SavingTwiceAnnouncesTwice()
    {
        var vm = Build();
        var results = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.StatusMessage)) results.Add(vm.StatusMessage);
        };

        await vm.SaveSettingsCommand.ExecuteAsync(null);
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // Two real announcements, each preceded by the clear that guarantees the change.
        Assert.Equal(2, results.Count(r => r == "Settings saved."));
    }

    [Fact]
    public async Task TheClearedStateIsNeverTheAnnouncement()
    {
        var vm = Build();
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // Blanking is a means, not a message: the line ends on the result, and LiveRegion drops
        // empty values rather than announcing "blank".
        Assert.Equal("Settings saved.", vm.StatusMessage);
    }

    private static SettingsViewModel Build()
    {
        var http = new HttpClient();
        var patreon = new PatreonService(
            new PatreonClient(http, PatreonAppRegistry.Manager, TestLogger.Create()),
            new StubAccountStore(),
            new PatreonEntitlementCache(),
            http,
            TestLogger.Create());

        return new SettingsViewModel(new StubConfigService(), patreon, TestLogger.Create());
    }

    private sealed class StubConfigService : IConfigService
    {
        private readonly AppConfig _config = new();
        public Task<AppConfig> LoadAsync() => Task.FromResult(_config);
        public Task SaveAsync(AppConfig config) => Task.CompletedTask;
        public async Task<AppConfig> UpdateAsync(Action<AppConfig> change)
        {
            var config = await LoadAsync();
            change(config);
            await SaveAsync(config);
            return config;
        }
        public string? LastLoadProblem => null;
        public void AcknowledgeLoadProblem() { }
    }

    private sealed class StubAccountStore : IPatreonAccountStore
    {
        public Task<PatreonAccount?> LoadAsync() => Task.FromResult<PatreonAccount?>(null);
        public Task SaveAsync(PatreonAccount account) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }
}
