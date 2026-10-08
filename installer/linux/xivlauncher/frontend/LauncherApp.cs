// SPDX-License-Identifier: GPL-3.0-or-later
// Accessible UI adapter for XIVLauncher.Core's existing login/patch/launch controller.
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;
using XIVLauncher.Core.Accounts;
using XIVLauncher.Core.Accounts.Secrets;
using XIVLauncher.Core.Accounts.Secrets.Providers;
using XIVLauncher.Core.Components.LoadingPage;
using XIVLauncher.Core.Components.MainPage;
using XIVLauncher.Core.Configuration;
using XIVLauncher.PlatformAbstractions;

namespace XIVLauncher.Core;

public class LauncherApp
{
    public enum LauncherState { Main, Loading, Settings, SteamDeckPrompt }
    public LauncherState State { get; set; }
    public ILauncherConfig Settings { get; }
    public bool CanSavePassword { get; }
    public Launcher Launcher { get; }
    public ISteam? Steam => Program.Steam;
    public Storage Storage { get; }
    public AccountManager Accounts { get; }
    public CommonUniqueIdCache UniqueIdCache { get; }
    public LoadingPage LoadingPage { get; } = new();
    public MainPage Controller { get; }
    public LauncherCredentials Credentials { get; }
    public bool RememberPassword { get; set; }
    public bool AutoLogin { get; set; }

    public LauncherApp(Storage storage, string? frontierUrl, string? cutOffBootver,
        ILauncherConfig? settings = null, ISecretProvider? secrets = null)
    {
        Settings = settings ?? Program.Config;
        CanSavePassword = secrets is not null || Program.Secrets is KeychainSecretProvider { IsAvailable: true };
        Storage = storage;
        Accounts = new AccountManager(storage.GetFile("accounts.json"));
        UniqueIdCache = new CommonUniqueIdCache(storage.GetFile("uidCache.json"));
        Credentials = new LauncherCredentials(Accounts, Settings, secrets ?? Program.Secrets);
        Launcher = frontierUrl is null ? null! : new Launcher(Program.Steam, UniqueIdCache, frontierUrl,
            Settings.AcceptLanguage ?? ApiHelpers.GenerateAcceptLanguage());
        Controller = new MainPage(this);
        if (!EnvironmentSettings.IsNoKillswitch && !string.IsNullOrEmpty(cutOffBootver) &&
            SeVersion.Parse(Repository.Boot.GetVer(Settings.GamePath)) > SeVersion.Parse(cutOffBootver))
            throw new InvalidOperationException("XIVLauncher needs an update for the current login protocol. Login is disabled until a compatible accessible launcher is available.");
    }

    public void StartLoading(string line1, string line2 = "", string line3 = "", bool isIndeterminate = true,
        bool canCancel = false, bool canDisableAutoLogin = false)
    {
        State = LauncherState.Loading;
        LoadingPage.Line1 = line1;
        LoadingPage.Line2 = line2;
        LoadingPage.Line3 = line3;
        LoadingPage.IsIndeterminate = isIndeterminate;
    }

    public void StopLoading() => State = LauncherState.Main;
    public virtual Task<bool> LoginAsync(string username, string password, bool otp, bool steam, bool trial)
    {
        if (Repository.Ffxiv.GetVer(Settings.GamePath) == Constants.BASE_GAME_VERSION && Settings.IsUidCacheEnabled == true)
            throw new InvalidOperationException("Disable the launcher's UID cache before downloading a fresh game installation.");
        return Controller.Login(username, password, otp, steam, trial, AutoLogin, LoginAction.Game);
    }
    public void AskForOtp() { }
    public Task<string?> WaitForOtpAsync() => AccessibleFrontend.RequestOtpAsync();
    public void ShowMessage(string text, string title = "XIVLauncher", string modalButtonText = "OK", Action? modalPressedAction = null)
        => AccessibleFrontend.Message(text, title, modalButtonText, modalPressedAction);
    public void ShowMessageBlocking(string text, string title = "XIVLauncher", bool canContinue = true)
        => AccessibleFrontend.MessageAsync(text, title).GetAwaiter().GetResult();
    public void ShowExceptionBlocking(Exception exception, string context)
        => ShowMessageBlocking(exception.Message, "XIVLauncher: " + context);
}
