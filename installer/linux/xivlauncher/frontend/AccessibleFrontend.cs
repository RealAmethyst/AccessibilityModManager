// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Core.Accounts;
using XIVLauncher.Core.Accounts.Secrets.Providers;
using XIVLauncher.Core.Components.MainPage;

namespace XIVLauncher.Core;

public static class AccessibleFrontend
{
    private static LoginWindow? window;

    public static void Run(string[] args)
    {
        AppBuilder.Configure<Application>().UsePlatformDetect().Start((application, _) =>
        {
            application.Name = "XIVLauncher Accessible";
            application.Styles.Add(new FluentTheme());
            window = new LoginWindow(args);
            window.Show();
            application.Run(window);
        }, args);
    }

    public static void Show() => Dispatcher.UIThread.Post(() => { window?.Show(); window?.Activate(); });
    public static void Hide() => Dispatcher.UIThread.Post(() => window?.Hide());
    public static async Task<string?> RequestOtpAsync()
        => await Dispatcher.UIThread.InvokeAsync(() => window!.RequestOtpAsync());
    public static async Task MessageAsync(string text, string title)
        => await Dispatcher.UIThread.InvokeAsync(() => window!.MessageAsync(text, title));
    public static void Message(string text, string title, string button, Action? after)
        => Dispatcher.UIThread.Post(async () => { await window!.MessageAsync(text, title, button); after?.Invoke(); });

    private sealed record AccountChoice(XivAccount Account)
    {
        public override string ToString() => Account.UserName + (Account.UseSteamServiceAccount ? " (Steam)" : "");
    }

    internal sealed class LoginWindow : Window
    {
        private readonly Func<Task<LauncherApp>>? initializeOverride;
        private readonly bool settingsOnly;
        private readonly string[] arguments;
        private readonly StackPanel form = new() { Spacing = 10 };
        private readonly ComboBox accounts = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBox username = new();
        private readonly TextBox password = new() { PasswordChar = '●' };
        private readonly CheckBox otp = new() { Content = "Use one-time password" };
        private readonly CheckBox steam = new() { Content = "Steam service account" };
        private readonly CheckBox trial = new() { Content = "Free trial account" };
        private readonly CheckBox remember = new() { Content = "Remember password" };
        private readonly CheckBox automatic = new() { Content = "Log in automatically" };
        private readonly Button submit = new();
        private readonly Button stopAutomatic = new() { Content = "Disable automatic login", IsVisible = false };
        private readonly TextBlock status = new() { Text = "Preparing XIVLauncher.", Focusable = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        private readonly TextBlock detail = new() { Focusable = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1, IsIndeterminate = true };
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private LauncherApp? launcher;
        private FileStream? sessionLock;
        private bool busy;
        private bool loadingAccount;

        internal LoginWindow(string[] args, Func<Task<LauncherApp>>? initializeOverride = null)
        {
            this.initializeOverride = initializeOverride;
            arguments = args;
            settingsOnly = args.Contains("--login-settings");
            Title = settingsOnly ? "XIVLauncher login settings" : "XIVLauncher Accessible";
            Width = 640;
            Height = 720;
            MinWidth = 450;
            MinHeight = 400;
            AutomationProperties.SetName(this, Title);
            var root = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
            Content = new ScrollViewer { Content = root };
            root.Children.Add(new TextBlock { Text = Title, FontSize = 24 });
            Label(form, "Saved account", accounts);
            Label(form, "Account username", username);
            Label(form, "Password", password);
            foreach (var control in new Control[] { steam, trial, otp, remember, automatic }) form.Children.Add(control);
            submit.Content = settingsOnly ? "Save login settings" : "Log in and play";
            submit.IsDefault = true;
            submit.Click += async (_, _) => await SubmitAsync();
            form.Children.Add(submit);
            form.IsVisible = false;
            root.Children.Add(form);
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(detail, "Download details");
            AutomationProperties.SetName(progress, "Download progress");
            root.Children.Add(status);
            root.Children.Add(detail);
            root.Children.Add(progress);
            root.Children.Add(stopAutomatic);
            stopAutomatic.Click += (_, _) =>
            {
                launcher!.Settings.IsAutologin = false;
                launcher.AutoLogin = false;
                automatic.IsChecked = false;
                stopAutomatic.IsEnabled = false;
                status.Text = "Automatic login disabled for the next launch.";
            };
            var close = new Button { Content = "Close" };
            close.Click += (_, _) => Close();
            root.Children.Add(close);
            remember.IsCheckedChanged += (_, _) =>
            {
                automatic.IsEnabled = remember.IsChecked == true;
                if (!automatic.IsEnabled) automatic.IsChecked = false;
            };
            accounts.SelectionChanged += async (_, _) =>
            {
                if (!loadingAccount && accounts.SelectedItem is AccountChoice choice)
                {
                    loadingAccount = true;
                    form.IsEnabled = false;
                    try { await LoadAccountAsync(choice.Account); }
                    catch (Exception ex) { password.Text = ""; status.Text = "Could not read the saved password: " + ex.Message; }
                    finally { loadingAccount = false; form.IsEnabled = true; }
                }
            };
            username.PropertyChanged += (_, change) =>
            {
                if (change.Property != TextBox.TextProperty || loadingAccount || busy) return;
                password.Text = "";
                automatic.IsChecked = false;
            };
            Closing += (_, e) =>
            {
                if (!busy) return;
                e.Cancel = true;
                status.Text = "XIVLauncher is working. Wait for it to finish, or stop the session through Steam.";
            };
            Closed += (_, _) => { timer.Stop(); sessionLock?.Dispose(); };
            timer.Tick += (_, _) => UpdateProgress();
            Opened += async (_, _) => await InitializeAsync();
        }

        private static void Label(StackPanel panel, string text, Control field)
        {
            var label = new TextBlock { Text = text };
            panel.Children.Add(label);
            panel.Children.Add(field);
            AutomationProperties.SetName(field, text);
            AutomationProperties.SetLabeledBy(field, label);
        }

        private async Task InitializeAsync()
        {
            try
            {
                // The same launcher owns configuration and credentials for the whole session.
                // Refuse to race the ordinary launcher, an existing game, or another frontend.
                if (initializeOverride is not null)
                    launcher = await initializeOverride();
                else
                {
                    foreach (var process in Process.GetProcesses())
                    {
                        using (process)
                            if (process.Id != Environment.ProcessId &&
                                (process.ProcessName.StartsWith("XIVLauncher", StringComparison.OrdinalIgnoreCase) ||
                                 process.ProcessName.StartsWith("ffxiv", StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidOperationException("Close the existing XIVLauncher or FINAL FANTASY XIV session first, then open this launcher again.");
                    }
                    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xlcore");
                    Directory.CreateDirectory(data);
                    sessionLock = new FileStream(Path.Combine(data, ".amm-accessible.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    launcher = await Task.Run(() => Program.Initialize([], settingsOnly));
                }
                loadingAccount = true;
                var choices = launcher.Accounts.Accounts.Select(account => new AccountChoice(account)).ToArray();
                accounts.ItemsSource = choices;
                var selected = launcher.Credentials.Current;
                accounts.SelectedItem = choices.FirstOrDefault(choice => choice.Account == selected);
                accounts.IsVisible = choices.Length > 0;
                if (selected is not null) await LoadAccountAsync(selected);
                else
                {
                    steam.IsChecked = Environment.GetEnvironmentVariable("STEAM_COMPAT_APP_ID") is "39210" or "312060";
                    trial.IsChecked = Environment.GetEnvironmentVariable("STEAM_COMPAT_APP_ID") == "312060";
                    automatic.IsEnabled = false;
                }
                loadingAccount = false;
                if (!launcher.CanSavePassword)
                {
                    remember.IsChecked = automatic.IsChecked = false;
                    remember.IsEnabled = automatic.IsEnabled = false;
                    status.Text = "The desktop keyring is unavailable. You can log in, but your password cannot be saved.";
                }
                else status.Text = settingsOnly ? "Changes use XIVLauncher's own saved account and keyring." : "Ready to log in.";
                progress.IsVisible = false;
                timer.Start();
                if (!settingsOnly && !arguments.Contains("--no-auto-login") && automatic.IsChecked == true &&
                    !string.IsNullOrWhiteSpace(username.Text) && !string.IsNullOrEmpty(password.Text))
                    await SubmitAsync();
                else
                {
                    form.IsVisible = true;
                    username.Focus();
                }
            }
            catch (Exception exception)
            {
                progress.IsVisible = false;
                status.Text = "XIVLauncher could not start: " + exception.Message;
                status.Focus();
            }
        }

        private async Task LoadAccountAsync(XivAccount account)
        {
            username.Text = account.UserName;
            otp.IsChecked = account.UseOtp;
            steam.IsChecked = account.UseSteamServiceAccount;
            trial.IsChecked = account.IsFreeTrial;
            remember.IsChecked = account.SavePassword;
            automatic.IsChecked = account.SavePassword && launcher!.Settings.IsAutologin == true;
            password.Text = await Task.Run(() => launcher!.Credentials.ReadPassword(account));
        }

        private async Task SubmitAsync()
        {
            if (busy || loadingAccount || launcher is null) return;
            var user = username.Text?.Trim().ToLowerInvariant() ?? "";
            var secret = password.Text ?? "";
            if (user.Length == 0 || secret.Length == 0)
            {
                status.Text = "Enter your account username and password.";
                (user.Length == 0 ? username : password).Focus();
                return;
            }
            var useOtp = otp.IsChecked == true;
            var useSteam = steam.IsChecked == true;
            var freeTrial = trial.IsChecked == true;
            launcher.RememberPassword = remember.IsChecked == true;
            launcher.AutoLogin = automatic.IsChecked == true;
            busy = true;
            form.IsEnabled = false;
            form.IsVisible = settingsOnly;
            progress.IsVisible = !settingsOnly;
            stopAutomatic.IsVisible = !settingsOnly && launcher.AutoLogin;
            status.Text = settingsOnly ? "Saving login settings." : "Logging in.";
            try
            {
                if (settingsOnly)
                {
                    await Task.Run(() => launcher.Credentials.Save(user, secret, useOtp, useSteam, freeTrial,
                        launcher.RememberPassword, launcher.AutoLogin));
                    status.Text = "Login settings saved. Launch FINAL FANTASY XIV from Steam.";
                    password.Text = "";
                    status.Focus();
                    return;
                }
                launcher.StartLoading("Logging in.");
                var finished = await Task.Run(() => launcher.LoginAsync(user, secret, useOtp, useSteam, freeTrial));
                if (finished)
                {
                    busy = false;
                    password.Text = "";
                    Close();
                    return;
                }
                status.Text = "The game was not started. Review the message and try again.";
            }
            catch (Exception exception)
            {
                // Never write credentials or a full authentication response to frontend logs.
                await MessageAsync(exception.Message, "XIVLauncher could not continue");
                status.Text = "Login or setup did not finish. You can try again.";
            }
            finally
            {
                busy = false;
                form.IsEnabled = true;
                form.IsVisible = true;
                progress.IsVisible = false;
                stopAutomatic.IsVisible = false;
                secret = "";
                if (IsVisible && !settingsOnly) password.Focus();
            }
        }

        private void UpdateProgress()
        {
            if (!busy || settingsOnly || launcher is null) return;
            var loading = launcher.LoadingPage;
            if (!string.IsNullOrEmpty(loading.Line1)) status.Text = loading.Line1;
            detail.Text = string.Join("\n", new[] { loading.Line2, loading.Line3 }.Where(line => !string.IsNullOrWhiteSpace(line)));
            progress.IsIndeterminate = loading.IsIndeterminate;
            progress.Value = float.IsFinite(loading.Progress) ? Math.Clamp(loading.Progress, 0, 1) : 0;
        }

        public async Task MessageAsync(string text, string title, string button = "OK")
        {
            Show();
            var body = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            AutomationProperties.SetName(body, "Message");
            var ok = new Button { Content = button };
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            panel.Children.Add(body);
            panel.Children.Add(ok);
            var dialog = new Window { Title = title, Width = 600, Height = 330, Content = panel };
            ok.Click += (_, _) => dialog.Close();
            dialog.Opened += (_, _) => body.Focus();
            await dialog.ShowDialog(this);
        }

        public async Task<string?> RequestOtpAsync()
        {
            Show();
            var input = new TextBox { MaxLength = 6 };
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            Label(panel, "One-time password", input);
            var confirm = new Button { Content = "Continue", IsDefault = true };
            var cancel = new Button { Content = "Cancel" };
            var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
            panel.Children.Add(error);
            panel.Children.Add(confirm);
            panel.Children.Add(cancel);
            var dialog = new Window { Title = "XIVLauncher one-time password", Width = 480, Height = 280, Content = panel };
            confirm.Click += (_, _) =>
            {
                var code = input.Text?.Trim() ?? "";
                if (code.Length != 6 || !code.All(char.IsAsciiDigit))
                {
                    error.Text = "Enter the six-digit one-time password.";
                    input.Focus();
                    return;
                }
                input.Text = "";
                dialog.Close(code);
            };
            cancel.Click += (_, _) => dialog.Close(null);
            dialog.Opened += (_, _) => input.Focus();
            return await dialog.ShowDialog<string?>(this);
        }
    }
}
