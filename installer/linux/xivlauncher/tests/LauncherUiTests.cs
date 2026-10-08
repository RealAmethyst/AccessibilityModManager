using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Config.Net;
using XIVLauncher.Common;
using XIVLauncher.Core;
using XIVLauncher.Core.Accounts.Secrets;
using XIVLauncher.Core.Configuration;
using XIVLauncher.Core.Configuration.Parsers;

[assembly: AvaloniaTestApplication(typeof(LauncherUiTests.ApplicationFactory))]

public sealed class LauncherUiTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-xl-ui-" + Guid.NewGuid().ToString("N"));
    public sealed class ApplicationFactory
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    private FakeLauncher CreateBackend(bool automatic)
    {
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder<ILauncherConfig>().UseIniFile(Path.Combine(root, "launcher.ini"))
            .UseTypeParser(new DirectoryInfoParser()).UseTypeParser(new AddonListParser()).Build();
        var launcher = new FakeLauncher(new Storage("test", root), config, new FakeSecrets());
        launcher.Credentials.Save("test-user", "test-password", false, true, false, true, automatic);
        return launcher;
    }

    [AvaloniaFact]
    public async Task SettingsUsesLabeledMaskedControlsAndNeverStartsLogin()
    {
        var backend = CreateBackend(true);
        var window = new AccessibleFrontend.LoginWindow(["--login-settings"], () => Task.FromResult<LauncherApp>(backend));
        window.Show();
        await Until(() => window.GetVisualDescendants().OfType<TextBox>().Any(box => box.IsEffectivelyVisible && box.Text == "test-user"));
        var boxes = window.GetVisualDescendants().OfType<TextBox>().ToArray();
        var user = boxes.Single(box => AutomationProperties.GetName(box) == "Account username");
        var password = boxes.Single(box => AutomationProperties.GetName(box) == "Password");
        Assert.True(user.IsFocused);
        Assert.Equal('●', password.PasswordChar);
        Assert.NotNull(AutomationProperties.GetLabeledBy(password));
        Assert.Equal("test-password", password.Text);
        Assert.Equal(0, backend.Logins);
        user.Text = "another-user";
        Assert.Equal("", password.Text);
        Assert.False(window.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => Equals(box.Content, "Log in automatically")).IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AutomaticLoginSkipsLoginFormAndUsesRememberedCredentialsOnce()
    {
        var backend = CreateBackend(true);
        var window = new AccessibleFrontend.LoginWindow([], () => Task.FromResult<LauncherApp>(backend));
        window.Show();
        await Until(() => backend.Logins == 1);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), box => box.IsEffectivelyVisible);
        backend.Finish.TrySetResult(true);
        await Until(() => !window.IsVisible);
        Assert.Equal("test-user", backend.Username);
        Assert.Equal("test-password", backend.Password);
    }

    [AvaloniaFact]
    public async Task DisabledAutomaticLoginWaitsForTheLoginButton()
    {
        var backend = CreateBackend(false);
        var window = new AccessibleFrontend.LoginWindow([], () => Task.FromResult<LauncherApp>(backend));
        window.Show();
        await Until(() => window.GetVisualDescendants().OfType<TextBox>().Any(box => box.IsEffectivelyVisible));
        Assert.Equal(0, backend.Logins);
        var login = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Log in and play"));
        login.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => backend.Logins == 1);
        backend.Finish.TrySetResult(true);
        await Until(() => !window.IsVisible);
    }

    [AvaloniaFact]
    public async Task OtpDialogReturnsOnlyEnteredCodeAndCancelReturnsNull()
    {
        var backend = CreateBackend(false);
        var window = new AccessibleFrontend.LoginWindow(["--login-settings"], () => Task.FromResult<LauncherApp>(backend));
        window.Show();
        await Until(() => window.GetVisualDescendants().OfType<TextBox>().Any(box => box.IsEffectivelyVisible));
        var request = window.RequestOtpAsync();
        var dialog = Assert.Single(window.OwnedWindows);
        var input = dialog.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.Equal("One-time password", AutomationProperties.GetName(input));
        input.Text = "123456";
        dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Continue"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("123456", await request);
        Assert.Equal("", input.Text);
        request = window.RequestOtpAsync();
        Assert.Single(window.OwnedWindows).Close();
        Assert.Null(await request);
        Assert.Equal(0, backend.Logins);
        window.Close();
    }

    private static async Task Until(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 100 && !ready(); attempt++) await Task.Delay(10);
        Assert.True(ready());
    }

    private sealed class FakeLauncher(Storage storage, ILauncherConfig settings, ISecretProvider secrets)
        : LauncherApp(storage, null, null, settings, secrets)
    {
        public int Logins;
        public string? Username;
        public string? Password;
        public TaskCompletionSource<bool> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<bool> LoginAsync(string username, string password, bool otp, bool steam, bool trial)
        {
            Username = username;
            Password = password;
            Interlocked.Increment(ref Logins);
            return Finish.Task;
        }
    }

    private sealed class FakeSecrets : ISecretProvider
    {
        private readonly Dictionary<string, string> entries = [];
        public string? GetPassword(string accountName) => entries.GetValueOrDefault(accountName);
        public void SavePassword(string accountName, string password) => entries[accountName] = password;
        public void DeletePassword(string accountName) => entries.Remove(accountName);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
